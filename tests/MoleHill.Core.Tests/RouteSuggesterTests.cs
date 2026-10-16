using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

public sealed class RouteSuggesterTests
{
    private readonly ITestOutputHelper _output;

    public RouteSuggesterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Suggest_FlatPlane_ReturnsStraightLine()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 101, 101, (_, _) => 5.0);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, Request(0.05, (10, 10), (90, 60)));

        Assert.True(result.Succeeded);
        RouteAlternative best = result.Alternatives[0];
        double direct = Math.Sqrt((80 * 80) + (50 * 50));
        Assert.InRange(best.Metrics.PlanLength, direct, direct * 1.01);
        Assert.Equal(0.0, best.Metrics.MaxGrade, 9);
        Assert.Equal(0.0, best.Metrics.CutVolume + best.Metrics.FillVolume, 9);
        Assert.True(MaxDistanceFromSegment(best, (10, 10), (90, 60)) <= 0.75 + 1e-9);
        Assert.True(best.PointCount <= 6, $"A flat route should simplify to a few vertices, got {best.PointCount}.");
        AssertEndsAt(best, (10, 10, 5.0), (90, 60, 5.0));
    }

    [Fact]
    public void Suggest_HillTooSteepToCross_GoesAroundIt()
    {
        RouteHeightGrid grid = HillGrid(out (double X, double Y) centre);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, Request(0.05, (10, 60), (110, 60), alternatives: 1));

        Assert.True(result.Succeeded);
        RouteAlternative route = result.Alternatives[0];
        AssertWithinGrade(route, 0.05);

        // The direct line climbs a 10-unit summit at up to ~45%; around it the ground is gentle.
        double closest = ClosestApproach(route, centre);
        Assert.True(closest > 15.0, $"Route passed within {closest:F1} of the summit.");
        Assert.True(route.Metrics.PlanLength > 100.0);
        Assert.True(route.Metrics.CutVolume + route.Metrics.FillVolume < 0.05 * route.Metrics.PlanLength * 4.0,
            "Going round should need almost no earthworks.");
    }

    [Fact]
    public void Suggest_NoEarthworksWeight_CutsStraightThroughButStillHoldsTheGrade()
    {
        RouteHeightGrid grid = HillGrid(out (double X, double Y) centre);
        RouteRequest request = Request(0.05, (10, 60), (110, 60), alternatives: 1, earthworks: 0.0, steepness: 0.0);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, request);

        Assert.True(result.Succeeded);
        RouteAlternative route = result.Alternatives[0];
        AssertWithinGrade(route, 0.05);
        Assert.True(ClosestApproach(route, centre) < 2.0);
        Assert.True(route.Metrics.CutVolume > 0.0 && route.Metrics.FillVolume > 0.0,
            "Holding 5% over the summit takes both cut and fill.");
    }

    [Fact]
    public void Suggest_LegTooSteepForAnyRoute_FailsWithRequiredGrade()
    {
        // A two-node-wide strip rising at 20%: too narrow to zig-zag down to 5%, so the leg fails and
        // says what it would need (the search does weave between the rows, so a little under 20%).
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 101, 2, (x, _) => 0.2 * x);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, Request(0.05, (5, 0), (95, 1)));

        Assert.False(result.Succeeded);
        Assert.Equal(RouteFailure.ExceedsMaxGrade, result.Failure);
        Assert.Equal(0, result.FailedLeg);
        Assert.NotNull(result.RequiredGrade);
        Assert.InRange(result.RequiredGrade!.Value, 0.1, 0.2);
        Assert.Empty(result.Alternatives);
    }

    [Fact]
    public void Suggest_EndsSeparatedByAHole_FailsNotConnected()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 61, 41, (x, _) => x is >= 29 and <= 31 ? double.NaN : 0.0);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, Request(0.05, (5, 20), (55, 20)));

        Assert.Equal(RouteFailure.NotConnected, result.Failure);
        Assert.Equal(0, result.FailedLeg);
    }

    [Fact]
    public void Suggest_PointOffTerrain_ReportsWhichPoint()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 51, 51, (_, _) => 0.0);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, Request(0.05, (5, 5), (25, 25), (200, 200)));

        Assert.Equal(RouteFailure.PointOffTerrain, result.Failure);
        Assert.Equal(2, result.FailedPoint);
    }

    [Fact]
    public void Suggest_RouteAvoidsHoleWithoutCrossingIt()
    {
        // A hole in the middle; the straight line would cross it.
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 81, 61, (x, y) =>
            Math.Abs(x - 40) < 8 && Math.Abs(y - 30) < 15 ? double.NaN : 0.0);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, Request(0.05, (5, 30), (75, 30), alternatives: 1));

        Assert.True(result.Succeeded);
        RouteAlternative route = result.Alternatives[0];
        for (int i = 1; i < route.PointCount; i++)
        {
            for (int k = 0; k <= 40; k++)
            {
                double t = k / 40.0;
                double x = Lerp(route.PointsXyz[(i - 1) * 3], route.PointsXyz[i * 3], t);
                double y = Lerp(route.PointsXyz[((i - 1) * 3) + 1], route.PointsXyz[(i * 3) + 1], t);
                Assert.False(Math.Abs(x - 40) < 7.5 && Math.Abs(y - 30) < 14.5, $"Route crosses the hole at ({x:F1}, {y:F1}).");
            }
        }
    }

    [Fact]
    public void Suggest_Waypoints_AreHonouredInOrder()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 101, 101, (x, y) => 0.01 * x);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, Request(0.05, (10, 10), (50, 80), (90, 10)));

        Assert.True(result.Succeeded);
        foreach (RouteAlternative route in result.Alternatives)
        {
            int waypoint = IndexOfVertex(route, (50, 80));
            Assert.True(waypoint > 0 && waypoint < route.PointCount - 1, "The waypoint must be an interior vertex.");
            Assert.Equal(0.5, route.PointsXyz[(waypoint * 3) + 2], 9);
            AssertEndsAt(route, (10, 10, 0.1), (90, 10, 0.9));
        }
    }

    [Fact]
    public void Suggest_Alternatives_AreDistinctCorridors()
    {
        RouteHeightGrid grid = HillGrid(out _);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, Request(0.05, (10, 60), (110, 60)));

        Assert.True(result.Succeeded);
        Assert.True(result.Alternatives.Count >= 2, $"Expected at least two routes, got {result.Alternatives.Count}.");
        for (int a = 0; a < result.Alternatives.Count; a++)
        {
            AssertWithinGrade(result.Alternatives[a], 0.05);
            for (int b = 0; b < a; b++)
            {
                double shared = SharedShare(result.Alternatives[a], result.Alternatives[b], 4.0);
                Assert.True(shared < RouteSuggester.NearDuplicateShare + 0.1,
                    $"Routes {b + 1} and {a + 1} share {shared:P0} of their length.");
            }
        }

        for (int i = 1; i < result.Alternatives.Count; i++)
            Assert.True(result.Alternatives[i].SearchCost >= result.Alternatives[i - 1].SearchCost);
    }

    [Fact]
    public void Suggest_RoughTerrain_SimplifiedOutputHoldsTheGradeLimit()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 151, 121, Rolling);
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, Request(0.06, (5, 5), (145, 115), (20, 110)));

        Assert.True(result.Succeeded, result.Failure.ToString());
        foreach (RouteAlternative route in result.Alternatives)
        {
            AssertWithinGrade(route, 0.06);
            Assert.True(route.Metrics.MaxGrade <= 0.06 * (1 + 1e-9));
            Assert.True(route.PointCount < route.Metrics.PlanLength, "The output should be thinned, not one vertex per cell.");
        }
    }

    [Fact]
    public void Suggest_SameInput_IsDeterministic()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 151, 121, Rolling);
        RouteRequest request = Request(0.06, (5, 5), (145, 115));

        RouteSuggestionResult first = RouteSuggester.Suggest(grid, request);
        RouteSuggestionResult second = RouteSuggester.Suggest(grid, request);

        Assert.Equal(first.Alternatives.Count, second.Alternatives.Count);
        for (int i = 0; i < first.Alternatives.Count; i++)
        {
            Assert.Equal(first.Alternatives[i].PointsXyz, second.Alternatives[i].PointsXyz);
            Assert.Equal(first.Alternatives[i].Metrics, second.Alternatives[i].Metrics);
        }
    }

    [Fact]
    public void Suggest_MillimetreModel_IsTheMetreRouteScaled()
    {
        // The solver is unit-free: the same site drawn in millimetres must give the same route, with
        // lengths x1000 and volumes x1e9. Grades are ratios and must not change at all.
        const double scale = 1000.0;
        RouteHeightGrid metres = RouteHeightGrid.FromFunction(0, 0, 1.0, 121, 121, (x, y) => Hill(x, y));
        RouteHeightGrid millimetres = RouteHeightGrid.FromFunction(0, 0, scale, 121, 121, (x, y) => scale * Hill(x / scale, y / scale));

        RouteSuggestionResult inMetres = RouteSuggester.Suggest(metres, Request(0.05, (10, 60), (110, 60), width: 4.0));
        RouteSuggestionResult inMillimetres = RouteSuggester.Suggest(
            millimetres, Request(0.05, (10 * scale, 60 * scale), (110 * scale, 60 * scale), width: 4.0 * scale));

        Assert.Equal(inMetres.Alternatives.Count, inMillimetres.Alternatives.Count);
        RouteMetrics m = inMetres.Alternatives[0].Metrics;
        RouteMetrics mm = inMillimetres.Alternatives[0].Metrics;
        Assert.Equal(m.PlanLength * scale, mm.PlanLength, 3);
        Assert.Equal(m.MaxGrade, mm.MaxGrade, 9);
        Assert.Equal(m.CutVolume * scale * scale * scale, mm.CutVolume, m.CutVolume * 1e6 + 1e-3);
        Assert.Equal(inMetres.Alternatives[0].PointCount, inMillimetres.Alternatives[0].PointCount);
    }

    [Fact]
    public void Suggest_InvalidSettings_Fail()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 11, 11, (_, _) => 0.0);
        Assert.Equal(RouteFailure.InvalidSettings, RouteSuggester.Suggest(grid, Request(0.0, (1, 1), (9, 9))).Failure);
        Assert.Equal(RouteFailure.TooFewPoints, RouteSuggester.Suggest(grid, Request(0.05, (1, 1))).Failure);
        Assert.Equal(
            RouteFailure.InvalidSettings,
            RouteSuggester.Suggest(grid, Request(0.05, (1, 1), (9, 9), earthworks: 0, steepness: 0, distance: 0)).Failure);
    }

    [Fact]
    public void Suggest_Cancelled_Throws()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 301, 301, Rolling);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            RouteSuggester.Suggest(grid, Request(0.06, (5, 5), (295, 295)), cancelled.Token));
    }

    [Fact]
    public void FromMesh_RasterisesTopSurfaceAndLeavesHolesEmpty()
    {
        // 2x1 squares of plane z = x + y, with the second square's upper triangle missing.
        double[] vertices =
        {
            0, 0, 0,   10, 0, 10,   20, 0, 20,
            0, 10, 10, 10, 10, 20,  20, 10, 30,
        };
        int[] faces = { 0, 1, 4, 0, 4, 3, 1, 2, 5 };
        RouteHeightGrid grid = RouteHeightGrid.FromMesh(IndexedTriMesh.FromArrays(vertices, faces), -5, -5, 25, 15, 1.0);

        Assert.Equal(21, grid.Columns);
        Assert.Equal(11, grid.Rows);
        Assert.Equal(7.0, grid.Height(4, 3), 9);
        Assert.Equal(19.0, grid.Height(17, 2), 9);
        Assert.True(double.IsNaN(grid.Height(12, 8)), "The missing triangle must read as no ground.");
        Assert.True(grid.TrySample(3.5, 2.5, out double z));
        Assert.Equal(6.0, z, 9);
    }

    [Fact]
    public void AutomaticCellSize_KeepsNodeCountBounded()
    {
        double cell = RouteHeightGrid.AutomaticCellSize(2000, 2000, 1500);
        Assert.InRange(cell, 1.5, 2.5);
        Assert.InRange((2000 / cell) * (2000 / cell), 0, RouteHeightGrid.TargetNodeCount * 1.01);
        Assert.Equal(0.06, RouteHeightGrid.AutomaticCellSize(60, 60, 60), 9);
    }

    [Fact]
    public void Suggest_KilometreSite_IsInteractive()
    {
        if (!PerformanceLane.ShouldRun(_output, "route suggestion on a 1.5 km site at 1.5 m"))
            return;

        // 1.5 km square at 1.5 m: ~1M nodes, three alternatives across the diagonal.
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.5, 1001, 1001, (x, y) => 8.0 * Rolling(x / 8.0, y / 8.0));
        RouteRequest request = Request(0.06, (50, 50), (1450, 1400), (700, 1100));
        RouteSuggester.Suggest(grid, Request(0.06, (50, 50), (300, 300)));

        var watch = Stopwatch.StartNew();
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, request);
        watch.Stop();

        _output.WriteLine($"{result.Alternatives.Count} routes, {result.DroppedNearDuplicates} dropped, {watch.ElapsedMilliseconds} ms");
        foreach (RouteAlternative route in result.Alternatives)
            _output.WriteLine($"  {route.Metrics.PlanLength:F0} m, max {route.Metrics.MaxGrade:P1}, {route.PointCount} vertices");
        Assert.True(result.Succeeded);
        Assert.True(watch.ElapsedMilliseconds < 10_000, $"Took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void FromMesh_HalfMillionFaces_RasterisesQuickly()
    {
        if (!PerformanceLane.ShouldRun(_output, "route height grid from a 500k-face mesh"))
            return;

        const int side = 501;
        const double spacing = 3.0;
        var vertices = new double[side * side * 3];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                int v = (r * side) + c;
                vertices[v * 3] = c * spacing;
                vertices[(v * 3) + 1] = r * spacing;
                vertices[(v * 3) + 2] = 8.0 * Rolling(c * spacing / 8.0, r * spacing / 8.0);
            }
        }

        var faces = new int[(side - 1) * (side - 1) * 6];
        int f = 0;
        for (int r = 0; r < side - 1; r++)
        {
            for (int c = 0; c < side - 1; c++)
            {
                int a = (r * side) + c;
                faces[f++] = a; faces[f++] = a + 1; faces[f++] = a + side + 1;
                faces[f++] = a; faces[f++] = a + side + 1; faces[f++] = a + side;
            }
        }

        var mesh = IndexedTriMesh.FromArrays(vertices, faces);
        var watch = Stopwatch.StartNew();
        RouteHeightGrid grid = RouteHeightGrid.FromMesh(mesh, 0, 0, 1500, 1500, 1.5);
        watch.Stop();

        _output.WriteLine($"{grid.Columns} x {grid.Rows} nodes from {faces.Length / 3} faces in {watch.ElapsedMilliseconds} ms");
        Assert.False(double.IsNaN(grid.Height(500, 500)));
        Assert.True(watch.ElapsedMilliseconds < 2_000);
    }

    private static RouteRequest Request(
        double maxGrade,
        params (double X, double Y)[] points) =>
        new() { Points = points, MaxGrade = maxGrade, CorridorWidth = 4.0 };

    private static RouteRequest Request(
        double maxGrade,
        (double X, double Y) start,
        (double X, double Y) end,
        int alternatives = 3,
        double distance = 1.0,
        double steepness = 1.0,
        double earthworks = 1.0,
        double width = 4.0) =>
        new()
        {
            Points = new[] { start, end },
            MaxGrade = maxGrade,
            CorridorWidth = width,
            AlternativeCount = alternatives,
            DistanceWeight = distance,
            SteepnessWeight = steepness,
            EarthworksWeight = earthworks,
        };

    private static double Hill(double x, double y)
    {
        double dx = x - 60.0, dy = y - 60.0;
        return 10.0 * Math.Exp(-((dx * dx) + (dy * dy)) / (2.0 * 8.0 * 8.0));
    }

    private static RouteHeightGrid HillGrid(out (double X, double Y) centre)
    {
        centre = (60, 60);
        return RouteHeightGrid.FromFunction(0, 0, 1.0, 121, 121, Hill);
    }

    private static double Rolling(double x, double y) =>
        (3.0 * Math.Sin(x / 17.0) * Math.Cos(y / 23.0)) + (1.5 * Math.Sin((x + y) / 9.0)) + (0.02 * x);

    private static void AssertWithinGrade(RouteAlternative route, double maxGrade)
    {
        double[] p = route.PointsXyz;
        for (int i = 1; i < route.PointCount; i++)
        {
            double plan = Math.Sqrt(Math.Pow(p[i * 3] - p[(i - 1) * 3], 2) + Math.Pow(p[(i * 3) + 1] - p[((i - 1) * 3) + 1], 2));
            double rise = Math.Abs(p[(i * 3) + 2] - p[((i - 1) * 3) + 2]);
            Assert.True(rise <= (maxGrade * plan * (1 + 1e-9)) + 1e-9, $"Segment {i} is at {rise / plan:P2}.");
        }
    }

    private static void AssertEndsAt(RouteAlternative route, (double X, double Y, double Z) start, (double X, double Y, double Z) end)
    {
        double[] p = route.PointsXyz;
        Assert.Equal(start.X, p[0], 9);
        Assert.Equal(start.Y, p[1], 9);
        Assert.Equal(start.Z, p[2], 6);
        Assert.Equal(end.X, p[^3], 9);
        Assert.Equal(end.Y, p[^2], 9);
        Assert.Equal(end.Z, p[^1], 6);
    }

    private static int IndexOfVertex(RouteAlternative route, (double X, double Y) point)
    {
        for (int i = 0; i < route.PointCount; i++)
        {
            if (Math.Abs(route.PointsXyz[i * 3] - point.X) < 1e-9 && Math.Abs(route.PointsXyz[(i * 3) + 1] - point.Y) < 1e-9)
                return i;
        }

        return -1;
    }

    private static double ClosestApproach(RouteAlternative route, (double X, double Y) point)
    {
        double best = double.MaxValue;
        foreach ((double x, double y) in Samples(route, 0.5))
            best = Math.Min(best, Math.Sqrt(((x - point.X) * (x - point.X)) + ((y - point.Y) * (y - point.Y))));
        return best;
    }

    private static double MaxDistanceFromSegment(RouteAlternative route, (double X, double Y) a, (double X, double Y) b)
    {
        double worst = 0.0;
        foreach ((double x, double y) in Samples(route, 0.5))
            worst = Math.Max(worst, DistanceToSegment(x, y, a, b));
        return worst;
    }

    private static double SharedShare(RouteAlternative candidate, RouteAlternative other, double band)
    {
        List<(double X, double Y)> samples = Samples(candidate, 0.5).ToList();
        int near = 0;
        foreach ((double x, double y) in samples)
        {
            double best = double.MaxValue;
            for (int i = 1; i < other.PointCount; i++)
            {
                best = Math.Min(best, DistanceToSegment(
                    x, y,
                    (other.PointsXyz[(i - 1) * 3], other.PointsXyz[((i - 1) * 3) + 1]),
                    (other.PointsXyz[i * 3], other.PointsXyz[(i * 3) + 1])));
            }

            if (best <= band)
                near++;
        }

        return (double)near / samples.Count;
    }

    private static IEnumerable<(double X, double Y)> Samples(RouteAlternative route, double spacing)
    {
        double[] p = route.PointsXyz;
        for (int i = 1; i < route.PointCount; i++)
        {
            double length = Math.Sqrt(Math.Pow(p[i * 3] - p[(i - 1) * 3], 2) + Math.Pow(p[(i * 3) + 1] - p[((i - 1) * 3) + 1], 2));
            int pieces = Math.Max(1, (int)Math.Ceiling(length / spacing));
            for (int k = 0; k < pieces; k++)
            {
                double t = (double)k / pieces;
                yield return (Lerp(p[(i - 1) * 3], p[i * 3], t), Lerp(p[((i - 1) * 3) + 1], p[(i * 3) + 1], t));
            }
        }
    }

    private static double DistanceToSegment(double x, double y, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared > 0 ? Math.Clamp((((x - a.X) * dx) + ((y - a.Y) * dy)) / lengthSquared, 0, 1) : 0;
        double px = a.X + (t * dx) - x, py = a.Y + (t * dy) - y;
        return Math.Sqrt((px * px) + (py * py));
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
