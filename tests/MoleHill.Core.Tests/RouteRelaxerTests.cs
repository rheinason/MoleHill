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

        RouteRelaxEvaluation stuck = relaxer.Evaluate(fitCurve: false);
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
}
