using MoleHill.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// The sketch relaxer behind mhRelaxRoute: a drawn line slides towards a route the search would price well,
/// as faithfully as asked, never getting worse, around pins the user places; and where only more route can
/// climb the ground it says so, and Help me finds the switchbacks.
/// </summary>
public sealed class RouteRelaxerTests
{
    private readonly ITestOutputHelper _output;

    public RouteRelaxerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>A 300 m square, level, with a 12 m round hill in the middle.</summary>
    private static RouteHeightGrid Hill() =>
        RouteHeightGrid.FromFunction(0, 0, 2.0, 151, 151, (x, y) =>
            12.0 * Math.Exp(-(((x - 150) * (x - 150)) + ((y - 150) * (y - 150))) / (2 * 30.0 * 30.0)));

    /// <summary>The command's defaults: 8%, a 4 m corridor, a 10 m minimum radius, Simplicity 2.</summary>
    private static RouteRelaxSettings Settings(double fidelity = 0.2) => new()
    {
        MaxGrade = 0.08,
        CorridorWidth = 4.0,
        MinimumRadius = 10.0,
        Fidelity = fidelity,
        Simplicity = 2.0,
        ProfileTolerance = 0.1,
    };

    private static RouteRelaxer Create(RouteHeightGrid grid, IReadOnlyList<(double X, double Y)> sketch, RouteRelaxSettings settings)
    {
        RouteRelaxer? relaxer = RouteRelaxer.TryCreate(grid, sketch, settings, out RouteRelaxFailure failure, out _);
        Assert.Equal(RouteRelaxFailure.None, failure);
        return relaxer!;
    }

    [Fact]
    public void Step_SketchOverAHill_GoesRoundItAndCostsLess()
    {
        RouteHeightGrid grid = Hill();
        RouteRelaxer relaxer = Create(grid, new[] { (30.0, 150.0), (270.0, 150.0) }, Settings());
        RouteRelaxEvaluation before = relaxer.Evaluate(fitCurve: false);

        // A straight sketch has no bend and no distance from itself, so it starts with its cost as its energy.
        Assert.Equal(relaxer.Cost(), relaxer.Energy(), 6);
        double previous = relaxer.Energy();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int round = 0; round < 100 && !relaxer.IsSettled; round++)
        {
            relaxer.Step(20);
            double energy = relaxer.Energy();
            Assert.True(energy <= previous * (1 + 1e-9) + 1e-9, $"energy rose from {previous:F2} to {energy:F2}");
            previous = energy;
        }

        RouteRelaxEvaluation after = relaxer.Evaluate(fitCurve: true);
        _output.WriteLine(
            $"{relaxer.Sweeps} sweeps in {watch.ElapsedMilliseconds} ms, {relaxer.StationCount} stations; cost {before.Cost:F0} -> {after.Cost:F0} " +
            $"({after.Improvement:P0}); cut {before.Metrics.CutVolume:F0} -> {after.Metrics.CutVolume:F0}, fill {before.Metrics.FillVolume:F0} -> {after.Metrics.FillVolume:F0}; " +
            $"length {before.Metrics.PlanLength:F0} -> {after.Metrics.PlanLength:F0}; curve {after.Curve!.ControlPointCount} control points; issues {before.Issues.Count} -> {after.Issues.Count}");

        Assert.True(relaxer.IsSettled, "The band should settle.");
        Assert.True(after.Improvement > 0.05, $"Only {after.Improvement:P1} cheaper than the sketch.");
        Assert.True(after.Metrics.CutVolume + after.Metrics.FillVolume < 0.5 * (before.Metrics.CutVolume + before.Metrics.FillVolume));
        Assert.True(after.Metrics.PlanLength > before.Metrics.PlanLength, "Going round the hill is longer.");
        Assert.True(after.Curve.ControlPointCount <= 15, $"{after.Curve.ControlPointCount} control points");

        // The ends never move.
        double[] plan = relaxer.PlanXy();
        Assert.Equal((30.0, 150.0), (plan[0], plan[1]));
        Assert.Equal((270.0, 150.0), (plan[^2], plan[^1]));
    }

    [Fact]
    public void Step_HighFidelity_KeepsTheSketch()
    {
        RouteHeightGrid grid = Hill();
        RouteRelaxer loose = Create(grid, new[] { (30.0, 150.0), (270.0, 150.0) }, Settings(fidelity: 0.2));
        RouteRelaxer firm = Create(grid, new[] { (30.0, 150.0), (270.0, 150.0) }, Settings(fidelity: 50.0));
        loose.Step(2000);
        firm.Step(2000);

        double looseStray = MaxDistanceFromLine(loose.PlanXy(), 150.0);
        double firmStray = MaxDistanceFromLine(firm.PlanXy(), 150.0);
        _output.WriteLine($"stray: loose {looseStray:F1} m, firm {firmStray:F1} m");
        Assert.True(firmStray < 0.25 * looseStray, $"Fidelity 50 strayed {firmStray:F1} m against {looseStray:F1} m at 0.2.");
    }

    [Fact]
    public void MovePin_PullsTheBandThroughIt()
    {
        RouteHeightGrid grid = Hill();
        RouteRelaxer relaxer = Create(grid, new[] { (30.0, 150.0), (270.0, 150.0) }, Settings());
        relaxer.Step(400);

        int pin = relaxer.AddPin(150.0, 150.0);
        Assert.True(relaxer.MovePin(pin, 150.0, 60.0));
        relaxer.Step(400);

        (int Id, double X, double Y) held = relaxer.Pins().Single(p => p.Id == pin);
        Assert.Equal((150.0, 60.0), (held.X, held.Y));
        Assert.True(relaxer.DistanceToBand(150.0, 60.0) < 1e-9);

        Assert.True(relaxer.RemovePin(pin));
        Assert.False(relaxer.RemovePin(RouteRelaxer.StartPin));
        Assert.Equal(2, relaxer.Pins().Count);
    }

    [Fact]
    public void MovePin_DraggedFarAside_BulgesInsteadOfSpiking()
    {
        // Dragged 60 m off a straight sketch in small steps, as a mouse does. Before the guide moved with the
        // pin, the band hugged the sketch to the pin and reached it by a narrow out-and-back spike (live).
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 2.0, 151, 151, (x, y) => 0.01 * x);
        RouteRelaxer relaxer = Create(grid, new[] { (30.0, 150.0), (270.0, 150.0) }, Settings(fidelity: 1.0));
        relaxer.Step(200);
        int pin = relaxer.AddPin(150.0, 150.0);
        for (int step = 1; step <= 30; step++)
        {
            Assert.True(relaxer.MovePin(pin, 150.0, 150.0 - (2.0 * step)));
            relaxer.Step(4);
        }

        relaxer.Step(2000);

        double[] plan = relaxer.PlanXy();
        double sharpest = 0.0;
        for (int i = 1; i < (plan.Length / 2) - 1; i++)
        {
            double ux = plan[i * 2] - plan[(i - 1) * 2], uy = plan[(i * 2) + 1] - plan[((i - 1) * 2) + 1];
            double vx = plan[(i + 1) * 2] - plan[i * 2], vy = plan[((i + 1) * 2) + 1] - plan[(i * 2) + 1];
            double turn = Math.Atan2(Math.Abs((ux * vy) - (uy * vx)), (ux * vx) + (uy * vy)) * 180.0 / Math.PI;
            sharpest = Math.Max(sharpest, turn);
        }

        RouteRelaxEvaluation evaluation = relaxer.Evaluate(fitCurve: true);
        _output.WriteLine($"sharpest turn {sharpest:F1} deg, length {evaluation.Metrics.PlanLength:F0} m, tightest bend {evaluation.Curve!.SmallestRadius:F1} m");
        Assert.True(relaxer.DistanceToBand(150.0, 90.0) < 1e-9);
        // A spike turns nearly back on itself at the pin; a bulge turns gently everywhere.
        Assert.True(sharpest < 10.0, $"A {sharpest:F0} degree turn: the band spiked (it turned 22 degrees before the guide moved with the pin).");
        Assert.True(evaluation.Curve.SmallestRadius > 20.0, $"Tightest bend {evaluation.Curve.SmallestRadius:F1} m.");
    }

    [Fact]
    public void TryCreate_SketchOffTheTerrain_FailsWithWhere()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 2.0, 101, 101, (x, y) => x > 120 && x < 140 ? double.NaN : 0.0);

        RouteRelaxer? relaxer = RouteRelaxer.TryCreate(grid, new[] { (20.0, 100.0), (180.0, 100.0) }, Settings(), out RouteRelaxFailure failure, out (double X, double Y) where);

        Assert.Null(relaxer);
        Assert.Equal(RouteRelaxFailure.OffTerrain, failure);
        Assert.InRange(where.X, 115.0, 125.0);
    }

    [Fact]
    public void Step_PlateauEdges_StayOnTheTerrain()
    {
        // A route between two points near a hole: the band must never be priced or moved across it.
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 2.0, 101, 101, (x, y) =>
            (x - 100) * (x - 100) + (y - 100) * (y - 100) < 30 * 30 ? double.NaN : 0.02 * x);
        RouteRelaxer relaxer = Create(grid, new[] { (40.0, 60.0), (160.0, 60.0) }, Settings(fidelity: 0.0));
        relaxer.Step(2000);

        double[] plan = relaxer.PlanXy();
        for (int i = 0; i < plan.Length / 2; i++)
            Assert.True(grid.TrySample(plan[i * 2], plan[(i * 2) + 1], out _), $"Station {i} left the terrain.");
    }

    [Fact]
    public void Evaluate_SketchStraightUpASteepSlope_ReportsAClimbAndHelpMeSwitchesBack()
    {
        // An 18% hillside at 10%: 45 m in 250 m of plan needs 450 m of route.
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, -100, 2.5, 161, 201, (x, y) => 0.18 * y);
        RouteRelaxSettings settings = Settings(fidelity: 1.0) with { MaxGrade = 0.10 };
        RouteRelaxer relaxer = Create(grid, new[] { (200.0, 30.0), (200.0, 280.0) }, settings);
        relaxer.Step(500);

        RouteRelaxEvaluation stuck = relaxer.Evaluate(fitCurve: true);
        // Too short for its climb, the route is graded at the grade it needs; its curve holds that grade
        // rather than spending knots on a limit it cannot meet (79 control points live).
        _output.WriteLine($"stuck curve: {stuck.Curve!.ControlPointCount} control points, grade {stuck.Curve.MaxGrade:P2}");
        Assert.True(stuck.Curve.ControlPointCount <= 8, $"{stuck.Curve.ControlPointCount} control points on a straight line");
        foreach (RouteRelaxIssue issue in stuck.Issues)
            _output.WriteLine($"{issue.Kind} {issue.StartStation:F0}-{issue.EndStation:F0} m, depth {issue.Depth:F1}, ground {issue.GroundGrade:P0}, extra {issue.ExtraLength:F0} m");
        Assert.Contains(stuck.Issues, issue => issue.Kind is RouteRelaxIssueKind.Climb or RouteRelaxIssueKind.TooShort);
        // Left alone, a sketch is never pushed to the length its climb needs: that snaked into 0.2 m bends live.
        Assert.True(stuck.Metrics.PlanLength < 1.1 * 250.0, $"The sketch snaked to {stuck.Metrics.PlanLength:F0} m.");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        RouteSuggestionResult help = relaxer.HelpMe();
        _output.WriteLine($"Help me in {watch.ElapsedMilliseconds} ms: {help.Failure}");
        Assert.True(help.Succeeded, help.Failure.ToString());
        relaxer.Step(500);

        RouteRelaxEvaluation helped = relaxer.Evaluate(fitCurve: true);
        _output.WriteLine(
            $"length {stuck.Metrics.PlanLength:F0} -> {helped.Metrics.PlanLength:F0}, max grade {helped.Metrics.MaxGrade:P1}, curve grade {helped.Curve!.MaxGrade:P2}, vertical {helped.Curve.MaxVerticalDeviation:F2}, " +
            $"cut {stuck.Metrics.CutVolume:F0} -> {helped.Metrics.CutVolume:F0}, curve {helped.Curve!.ControlPointCount} control points, issues {helped.Issues.Count}");
        Assert.True(helped.Metrics.PlanLength >= 450.0 * 0.98, $"{helped.Metrics.PlanLength:F0} m cannot climb 45 m at 10%.");
        foreach (RouteRelaxIssue issue in helped.Issues)
            _output.WriteLine($"  after: {issue.Kind} {issue.StartStation:F0}-{issue.EndStation:F0} m, depth {issue.Depth:F1}");
        Assert.DoesNotContain(helped.Issues, issue => issue.Kind is RouteRelaxIssueKind.Climb or RouteRelaxIssueKind.TooShort);
        // Climbing at the limit all the way, the smooth curve keeps within a part in two hundred of it.
        Assert.True(helped.Curve.ControlPointCount <= 60, $"{helped.Curve.ControlPointCount} control points");
        Assert.True(helped.Curve.MaxGrade <= 0.10 * 1.005, $"curve grade {helped.Curve.MaxGrade:P2}");
    }

    [Fact]
    public void Step_SameInput_IsDeterministic()
    {
        RouteHeightGrid grid = Hill();
        RouteRelaxer a = Create(grid, new[] { (30.0, 140.0), (150.0, 200.0), (270.0, 150.0) }, Settings());
        RouteRelaxer b = Create(grid, new[] { (30.0, 140.0), (150.0, 200.0), (270.0, 150.0) }, Settings());
        a.Step(300);
        b.Step(300);
        Assert.Equal(a.PlanXy(), b.PlanXy());
    }

    private static double MaxDistanceFromLine(double[] plan, double y)
    {
        double worst = 0.0;
        for (int i = 0; i < plan.Length / 2; i++)
            worst = Math.Max(worst, Math.Abs(plan[(i * 2) + 1] - y));
        return worst;
    }
    [Fact]
    public void SelfCrossings_FigureEight_CountsTheCrossingOnce()
    {
        // A sketch that loops back over itself: one crossing, however the segments fall on the grid.
        RouteRelaxer relaxer = Create(Hill(), new[] { (30.0, 30.0), (150.0, 150.0), (150.0, 30.0), (30.0, 150.0) }, Settings());
        Assert.Equal(1, relaxer.SelfCrossings());
        Assert.Equal(0, Create(Hill(), new[] { (30.0, 150.0), (270.0, 150.0) }, Settings()).SelfCrossings());
    }

    [Fact]
    public void Evaluate_BandCrossingItself_ReportsWhere()
    {
        RouteRelaxer relaxer = Create(Hill(), new[] { (30.0, 30.0), (150.0, 150.0), (150.0, 30.0), (30.0, 150.0) }, Settings());
        RouteRelaxIssue crossing = Assert.Single(relaxer.Evaluate(fitCurve: false).Issues, issue => issue.Kind == RouteRelaxIssueKind.Crossing);
        Assert.Equal(90.0, crossing.X, 6);
        Assert.Equal(90.0, crossing.Y, 6);
    }

    [Fact]
    public void Untangle_LoopAroundNoPin_IsCutOutAtTheCrossing()
    {
        // A route that doubles back over itself, as a drag left it live: the loop goes, the ends stay.
        var sketch = new[] { (30.0, 100.0), (150.0, 100.0), (150.0, 60.0), (103.3, 60.0), (103.3, 140.0), (270.0, 140.0) };
        RouteRelaxer relaxer = Create(Hill(), sketch, Settings());
        Assert.Equal(1, relaxer.SelfCrossings());
        Assert.Equal(1, relaxer.Untangle());
        Assert.Equal(0, relaxer.SelfCrossings());
        double[] plan = relaxer.PlanXy();
        Assert.Equal((30.0, 100.0), (plan[0], plan[1]));
        Assert.Equal((270.0, 140.0), (plan[^2], plan[^1]));
    }

    [Fact]
    public void Untangle_LoopHoldingAPin_IsLeftForTheUser()
    {
        var sketch = new[] { (30.0, 100.0), (150.0, 100.0), (150.0, 60.0), (103.3, 60.0), (103.3, 140.0), (270.0, 140.0) };
        RouteRelaxer relaxer = Create(Hill(), sketch, Settings());
        relaxer.AddPin(150.0, 70.0);
        Assert.Equal(0, relaxer.Untangle());
        Assert.Equal(1, relaxer.SelfCrossings());
    }

    [Fact]
    public void Step_NeverTiesTheBandIntoALoop()
    {
        // An invariant, not a reproduction: relaxing may untangle a band but never add a crossing. The live
        // crossing came from a drag; 48 relaxed hairpins without the guard never crossed.
        RouteRelaxer relaxer = Create(Hill(),
            new[] { (40.0, 150.0), (150.0, 210.0), (230.0, 150.0), (150.0, 175.0), (60.0, 140.0) }, Settings(fidelity: 0.0));
        int crossings = relaxer.SelfCrossings();
        for (int round = 0; round < 60 && !relaxer.IsSettled; round++)
        {
            relaxer.Step(10);
            Assert.True(relaxer.SelfCrossings() <= crossings, $"round {round}: {relaxer.SelfCrossings()} crossings, started with {crossings}");
        }
    }
}
