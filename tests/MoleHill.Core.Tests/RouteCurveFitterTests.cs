using MoleHill.Core.Engine;
using MoleHill.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// The editable route curve: the fewest cubic control points within tolerance of the exact route, which
/// still keep its grade limit, its minimum radius, its waypoints and its sharp corners. Asked for after a
/// user found suggested routes (about a hundred control points) hard to edit next to a hand-drawn road
/// (eleven), 2026-10-17.
/// </summary>
public sealed class RouteCurveFitterTests
{
    private readonly ITestOutputHelper _output;

    public RouteCurveFitterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Fit_StraightGrade_IsOneBezier()
    {
        double[] line = { 0, 0, 0, 100, 0, 5 };

        FittedRouteCurve curve = RouteCurveFitter.Fit(new[] { line }, 0.1, 0.1, 0.08, 0.0, 10.0);

        Assert.Single(curve.Pieces);
        Assert.Equal(4, curve.ControlPointCount);
        Assert.True(curve.MaxPlanDeviation < 1e-6);
        Assert.True(curve.MaxVerticalDeviation < 1e-6);
        Assert.Equal(0.05, curve.MaxGrade, 6);
    }

    [Fact]
    public void Fit_StraightArcStraight_FewPointsWithinToleranceAndRadius()
    {
        // 50 m straight, a 90-degree arc at 20 m sampled every degree, 50 m straight; climbing gently.
        const double radius = 20.0;
        var points = new List<double>();
        void Add(double x, double y) => points.AddRange(new[] { x, y, 0.0 });
        Add(0, 0);
        Add(50, 0);
        for (int degree = 1; degree <= 90; degree++)
        {
            double angle = (-90.0 + degree) * Math.PI / 180.0;
            Add(50 + (radius * Math.Cos(angle)), radius + (radius * Math.Sin(angle)));
        }

        Add(70, 70);
        AssignProfile(points, 0.04);

        FittedRouteCurve curve = RouteCurveFitter.Fit(new[] { points.ToArray() }, 0.1, 0.1, 0.08, radius, radius);

        _output.WriteLine($"{curve.ControlPointCount} control points, plan {curve.MaxPlanDeviation:F3}, vertical {curve.MaxVerticalDeviation:F3}, radius {curve.SmallestRadius:F2}, grade {curve.MaxGrade:P3}");
        Assert.Single(curve.Pieces);
        Assert.True(curve.ControlPointCount <= 16, $"{curve.ControlPointCount} control points");
        Assert.True(curve.MaxPlanDeviation <= 0.1 + 1e-9);
        Assert.True(curve.MaxVerticalDeviation <= 0.1 + 1e-9);
        Assert.True(curve.SmallestRadius >= radius * (1 - RouteCurveFitter.RadiusSlack) - 1e-6, $"radius {curve.SmallestRadius:F2}");
        Assert.True(curve.MaxGrade <= 0.08);
        AssertEndsExact(curve, points);
    }

    [Fact]
    public void Fit_SharpCorner_StaysACornerWithTheCornerPointExact()
    {
        double[] corner = { 0, 0, 0, 60, 0, 1, 60, 60, 2 };

        FittedRouteCurve curve = RouteCurveFitter.Fit(new[] { corner }, 0.1, 0.1, 0.08, 0.0, 10.0);

        Assert.Equal(2, curve.Pieces.Count);
        Assert.Equal(7, curve.ControlPointCount);
        (double x, double y, double z) = curve.Pieces[0].End;
        Assert.Equal((60.0, 0.0, 1.0), (x, y, z));
        Assert.Equal(curve.Pieces[0].End, curve.Pieces[1].Start);
    }

    [Fact]
    public void Fit_TwoLegs_PassTheWaypointExactly()
    {
        double[] first = { 0, 0, 0, 40, 10, 1 };
        double[] second = { 40, 10, 1, 80, 0, 2 };

        FittedRouteCurve curve = RouteCurveFitter.Fit(new[] { first, second }, 0.1, 0.1, 0.08, 0.0, 10.0);

        Assert.Equal(2, curve.Pieces.Count);
        Assert.Equal((40.0, 10.0, 1.0), curve.Pieces[0].End);
        Assert.Equal((40.0, 10.0, 1.0), curve.Pieces[1].Start);
    }

    // The suggestion sites at the command's default Simplicity (2), which widens both tolerances threefold:
    // what a user gets. The hand-drawn road the user compared against had 11 control points.
    [Fact]
    public void Suggest_HillySite_CurvesAreFewPointsAndKeepTheRules()
    {
        (RouteHeightGrid grid, RouteRequest request) = RouteHillySiteTests.Site();
        AssertSuggestedCurves(grid, AtDefaultSimplicity(request), maxControlPoints: 30);
    }

    [Fact]
    public void Suggest_MinimumRadiusSite_CurvesAreFewPointsAndKeepTheRules()
    {
        (RouteHeightGrid grid, RouteRequest request) = RouteMinimumRadiusTests.LiveCase(request => request);
        AssertSuggestedCurves(grid, AtDefaultSimplicity(request), maxControlPoints: 40);
    }

    [Fact]
    public void Suggest_Switchbacks_CurvesAreFewPointsAndKeepTheRules()
    {
        (RouteHeightGrid grid, RouteRequest request) = Hillside();
        AssertSuggestedCurves(grid, AtDefaultSimplicity(request), maxControlPoints: 60);
    }

    private static RouteRequest AtDefaultSimplicity(RouteRequest request) => new()
    {
        Points = request.Points,
        MaxGrade = request.MaxGrade,
        CorridorWidth = request.CorridorWidth,
        MinimumRadius = request.MinimumRadius,
        AlternativeCount = request.AlternativeCount,
        ProfileTolerance = request.ProfileTolerance,
        Simplicity = 2.0,
    };

    private void AssertSuggestedCurves(RouteHeightGrid grid, RouteRequest request, int maxControlPoints)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, request);
        _output.WriteLine($"suggested in {watch.ElapsedMilliseconds} ms");
        Assert.True(result.Succeeded, result.Failure.ToString());
        double planTolerance = request.EffectiveCurveTolerance;
        double verticalTolerance = (request.ProfileTolerance > 0.0 ? request.ProfileTolerance : 0.01 * grid.CellSize) * request.SimplicityScale;
        int fitted = 0, exact = 0;
        foreach (RouteAlternative route in result.Alternatives)
        {
            FittedRouteCurve curve = route.Curve;
            // The exact curve as the command drew it: one degree-2 NURBS, two control points per span.
            int exactPoints = (2 * route.Spans.Count) + 1;
            fitted += curve.ControlPointCount;
            exact += exactPoints;
            _output.WriteLine(
                $"  {exactPoints} exact control points -> {curve.ControlPointCount} in {curve.Pieces.Count} pieces; " +
                $"plan {curve.MaxPlanDeviation:F3} / {planTolerance:F3}, vertical {curve.MaxVerticalDeviation:F3} / {verticalTolerance:F3}; " +
                $"grade {curve.MaxGrade:P3} (exact {route.Metrics.MaxGrade:P3}); radius {curve.SmallestRadius:F2} (exact {route.Fillet.SmallestRadius:F2}); " +
                $"complexity {curve.Complexity.Total:F1} (exact {route.Complexity.Total:F1}); cut {route.CurveMetrics.CutVolume:F0} (exact {route.Metrics.CutVolume:F0})");

            Assert.True(curve.ControlPointCount <= maxControlPoints, $"{curve.ControlPointCount} control points");
            Assert.True(curve.MaxPlanDeviation <= planTolerance * 1.0001, $"plan deviation {curve.MaxPlanDeviation:F3}");
            // Heights give way to the grade when a gentler profile is the only way to hold it; a few
            // decimetres at most (the minimum-radius site fits to 3 cm, and one route moves 0.26 m).
            Assert.True(curve.MaxVerticalDeviation <= Math.Max(2 * verticalTolerance, 0.3), $"vertical {curve.MaxVerticalDeviation:F3}");
            Assert.True(curve.MaxGrade <= request.MaxGrade * (1 + RouteCurveFitter.GradeTolerance) + 1e-9, $"grade {curve.MaxGrade:P4}");
            if (route.Fillet.SmallestRadius is double exactRadius && curve.SmallestRadius is double fittedRadius)
            {
                double required = Math.Min(request.MinimumRadius, exactRadius) * (1 - RouteCurveFitter.RadiusSlack);
                Assert.True(fittedRadius >= required - 1e-6, $"radius {fittedRadius:F2} under {required:F2}");
            }

            Assert.Equal(route.PointsXyz[0], curve.PointsXyz[0], 9);
            Assert.Equal(route.PointsXyz[1], curve.PointsXyz[1], 9);
            Assert.Equal(route.PointsXyz[^3], curve.PointsXyz[^3], 9);
            Assert.Equal(route.PointsXyz[^2], curve.PointsXyz[^2], 9);
        }

        Assert.True(fitted * 2 <= exact, $"{fitted} control points against {exact} exact: the curves must be at most half as heavy.");
    }

    internal static (RouteHeightGrid Grid, RouteRequest Request) Hillside()
    {
        const int side = 161;
        const double spacing = 2.0;
        var vertices = new double[side * side * 3];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                int v = (r * side) + c;
                vertices[v * 3] = c * spacing;
                vertices[(v * 3) + 1] = r * spacing;
                vertices[(v * 3) + 2] = 0.18 * r * spacing;
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

        var request = new RouteRequest
        {
            Points = new[] { (160.0, 30.0), (160.0, 280.0) },
            MaxGrade = 0.10,
            CorridorWidth = 4.0,
            MinimumRadius = 10.0,
            AlternativeCount = 3,
            ProfileTolerance = 0.1,
        };
        (double minX, double minY, double maxX, double maxY) = RouteSuggester.SearchDomain(request.Points);
        double cell = RouteHeightGrid.AutomaticCellSize(maxX - minX, maxY - minY, RouteSuggester.DirectSpan(request.Points));
        return (RouteHeightGrid.FromMesh(IndexedTriMesh.FromArrays(vertices, faces), minX, minY, maxX, maxY, cell), request);
    }

    private static void AssignProfile(List<double> points, double grade)
    {
        double along = 0.0;
        for (int i = 1; i < points.Count / 3; i++)
        {
            along += Math.Sqrt(Math.Pow(points[i * 3] - points[(i - 1) * 3], 2) + Math.Pow(points[(i * 3) + 1] - points[((i - 1) * 3) + 1], 2));
            points[(i * 3) + 2] = grade * along;
        }
    }

    private static void AssertEndsExact(FittedRouteCurve curve, List<double> points)
    {
        Assert.Equal((points[0], points[1], points[2]), curve.Pieces[0].Start);
        Assert.Equal((points[^3], points[^2], points[^1]), curve.Pieces[^1].End);
    }
}
