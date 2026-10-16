using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Routing;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// The minimum radius on the site it was found failing on live (2026-10-16, <c>mhSuggestRoute</c> at the
/// default 10 m): before the plan was simplified for it, every route came back with corners of one to
/// three metres, where the straightened path kept short segments with small turns between them.
/// </summary>
public sealed class RouteMinimumRadiusTests
{
    private const double MinimumRadius = 10.0;
    private const double MaxGrade = 0.08;
    private const double SurveySpacing = 2.5;

    private readonly ITestOutputHelper _output;

    public RouteMinimumRadiusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Suggest_LiveSiteAtTenMetres_CornersTakeTheRadiusOrAreExplained()
    {
        (RouteHeightGrid grid, RouteRequest request) = LiveCase(request => request);

        var watch = Stopwatch.StartNew();
        RouteSuggestionResult result = RouteSuggester.Suggest(grid, request);
        watch.Stop();
        _output.WriteLine($"{grid.Columns} x {grid.Rows} nodes at {grid.CellSize:F3}, {result.Alternatives.Count} routes in {watch.ElapsedMilliseconds} ms");

        Assert.True(result.Succeeded, result.Failure.ToString());
        Assert.Equal(3, result.Alternatives.Count);
        int tightTotal = 0;
        foreach (RouteAlternative route in result.Alternatives)
        {
            List<Corner> corners = Corners(route.Spans, request.Points);
            List<Corner> tight = corners.Where(corner => corner.Radius < MinimumRadius - 1e-6).ToList();
            RouteFilletSummary fillet = route.Fillet;
            _output.WriteLine(
                $"  {route.Metrics.PlanLength:F1} m, max {route.Metrics.MaxGrade:P2}, {corners.Count} corners, " +
                $"min radius {corners.Min(corner => corner.Radius):F2}, {tight.Count} under " +
                $"({fillet.TerrainLimitedCorners} terrain, {fillet.ReversalCorners} reversal, {fillet.GradeLimitedCorners} grade)" +
                string.Concat(tight.Select(corner => $" [{corner.Radius:F2} m at ({corner.X:F1}, {corner.Y:F1})]")));

            // The readout counts exactly the corners the curve shows tight, and gives each a reason.
            Assert.Equal(tight.Count, fillet.ReducedCorners);
            Assert.True(fillet.TerrainLimitedCorners >= 0);
            Assert.Equal(0, fillet.ReversalCorners);
            Assert.Equal(0, fillet.GradeLimitedCorners);

            // This site's tight spots are its steps: the formula's factors switch at x = 110 (the valley)
            // and y = 30 and 170 (the ridge), making 0.5-2.4 m steps a survey cell wide. A route crosses
            // one obliquely, where it is gentle enough, and a 10 m curve there would cost more than the
            // tolerance allows. Anywhere else, every corner takes the radius.
            foreach (Corner corner in tight)
            {
                double step = DistanceToStep(corner.X, corner.Y);
                Assert.True(step <= MinimumRadius + SurveySpacing,
                    $"A {corner.Radius:F2} m corner at ({corner.X:F1}, {corner.Y:F1}) is {step:F1} m from any step.");
            }

            Assert.True(tight.Count <= 2, $"{tight.Count} corners under {MinimumRadius} m.");
            tightTotal += tight.Count;

            Assert.True(route.HasArcs);
            AssertContiguousAndTangentAtArcs(route.Spans);
            Assert.True(FineMaxGrade(route.Spans) <= MaxGrade * (1 + 1e-6), $"Measured finely, the curve must hold the grade: {FineMaxGrade(route.Spans):P6}.");
            Assert.True(route.Metrics.MaxGrade <= MaxGrade * (1 + 1e-9));
            Assert.Equal(20.0, route.PointsXyz[0], 9);
            Assert.Equal(180.0, route.PointsXyz[1], 9);
            Assert.Equal(180.0, route.PointsXyz[^3], 9);
            Assert.Equal(20.0, route.PointsXyz[^2], 9);
        }

        Assert.True(tightTotal <= 2, $"{tightTotal} corners under {MinimumRadius} m across the routes.");
    }

    [Fact]
    public void Suggest_LiveSiteAtTenMetres_IsDeterministic()
    {
        (RouteHeightGrid grid, RouteRequest request) = LiveCase(request => request);

        RouteSuggestionResult first = RouteSuggester.Suggest(grid, request);
        RouteSuggestionResult second = RouteSuggester.Suggest(grid, request);

        Assert.Equal(first.Alternatives.Count, second.Alternatives.Count);
        for (int i = 0; i < first.Alternatives.Count; i++)
        {
            Assert.Equal(first.Alternatives[i].Spans, second.Alternatives[i].Spans);
            Assert.Equal(first.Alternatives[i].PointsXyz, second.Alternatives[i].PointsXyz);
            Assert.Equal(first.Alternatives[i].Fillet, second.Alternatives[i].Fillet);
        }
    }

    [Fact]
    public void Suggest_LiveSiteWithWaypoint_PassesItExactlyAndRoundsTheRest()
    {
        (RouteHeightGrid grid, RouteRequest request) = LiveCase(request => new RouteRequest
        {
            Points = new[] { (20.0, 180.0), (100.0, 100.0), (180.0, 20.0) },
            MaxGrade = request.MaxGrade,
            CorridorWidth = request.CorridorWidth,
            MinimumRadius = request.MinimumRadius,
            AlternativeCount = 1,
        });

        RouteSuggestionResult result = RouteSuggester.Suggest(grid, request);

        Assert.True(result.Succeeded, result.Failure.ToString());
        RouteAlternative route = result.Alternatives[0];
        int waypoint = Enumerable.Range(0, route.PointCount)
            .FirstOrDefault(i => route.PointsXyz[i * 3] == 100.0 && route.PointsXyz[(i * 3) + 1] == 100.0, -1);
        Assert.True(waypoint > 0 && waypoint < route.PointCount - 1, "The waypoint must be passed exactly.");

        List<Corner> tight = Corners(route.Spans, request.Points).Where(corner => corner.Radius < MinimumRadius - 1e-6).ToList();
        Assert.Equal(tight.Count, route.Fillet.ReducedCorners);
        // Besides the steps, the one place a corner may stay tight is beside the waypoint: it sits on a
        // 15% hillside, never moves, and the route leaving it has to turn onto the contour within metres.
        Assert.True(tight.Count <= 2, $"{tight.Count} corners under {MinimumRadius} m.");
        Assert.All(tight, corner => Assert.True(
            Math.Min(DistanceToStep(corner.X, corner.Y), Distance((corner.X, corner.Y), (100.0, 100.0))) <= MinimumRadius + SurveySpacing,
            $"A {corner.Radius:F2} m corner at ({corner.X:F1}, {corner.Y:F1}) is near neither a step nor the waypoint."));
        AssertContiguousAndTangentAtArcs(route.Spans);
        Assert.True(FineMaxGrade(route.Spans) <= MaxGrade * (1 + 1e-6));
    }

    /// <summary>The live site: a 200 m square surveyed at 2.5 m, searched at the command's automatic cell size.</summary>
    private static (RouteHeightGrid Grid, RouteRequest Request) LiveCase(Func<RouteRequest, RouteRequest> adjust)
    {
        const int side = 81;
        var vertices = new double[side * side * 3];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                int v = (r * side) + c;
                double x = c * SurveySpacing, y = r * SurveySpacing;
                vertices[v * 3] = x;
                vertices[(v * 3) + 1] = y;
                vertices[(v * 3) + 2] = Site(x, y);
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

        RouteRequest request = adjust(new RouteRequest
        {
            Points = new[] { (20.0, 180.0), (180.0, 20.0) },
            MaxGrade = MaxGrade,
            CorridorWidth = 4.0,
            MinimumRadius = MinimumRadius,
            AlternativeCount = 3,
        });
        (double minX, double minY, double maxX, double maxY) = RouteSuggester.SearchDomain(request.Points);
        double cell = RouteHeightGrid.AutomaticCellSize(maxX - minX, maxY - minY, RouteSuggester.DirectSpan(request.Points));
        RouteHeightGrid grid = RouteHeightGrid.FromMesh(IndexedTriMesh.FromArrays(vertices, faces), minX, minY, maxX, maxY, cell);
        return (grid, request);
    }

    private static double Site(double x, double y) =>
        20.0
        + (8.0 * Math.Exp(-(((x - 70.0) * (x - 70.0)) + ((y - 120.0) * (y - 120.0))) / (2.0 * 30.0 * 30.0)))
        + (3.0 * Math.Exp(-((x - 140.0) * (x - 140.0)) / (2.0 * 6.0 * 6.0)) * (y > 30.0 && y < 170.0 ? 1.0 : 0.3))
        - (4.0 * Math.Exp(-((y - 40.0) * (y - 40.0)) / (2.0 * 12.0 * 12.0)) * (x < 110.0 ? 1.0 : 0.4))
        + (0.02 * x);

    /// <summary>
    /// Plan distance to the nearest of the site's steps, where they are more than 0.1 m high: x = 110 for
    /// 10 &lt; y &lt; 70 (the valley's 2.4 m step), and y = 30 and y = 170 for 125 &lt; x &lt; 155 (the ridge's 2.1 m).
    /// </summary>
    private static double DistanceToStep(double x, double y)
    {
        double valley = Distance((x, y), (110.0, Math.Clamp(y, 10.0, 70.0)));
        double ridgeSouth = Distance((x, y), (Math.Clamp(x, 125.0, 155.0), 30.0));
        double ridgeNorth = Distance((x, y), (Math.Clamp(x, 125.0, 155.0), 170.0));
        return Math.Min(valley, Math.Min(ridgeSouth, ridgeNorth));
    }

    /// <summary>A plan corner of the spans: an arc by its radius and middle, a sharp joint as radius 0.</summary>
    private readonly record struct Corner(double Radius, double X, double Y);

    /// <summary>
    /// Every plan corner of the spans that is not at one of <paramref name="points"/>: an arc, however
    /// many spans carry it, by its radius; a joint where the plan direction changes without one as 0.
    /// </summary>
    private static List<Corner> Corners(IReadOnlyList<RouteSpan> spans, IReadOnlyList<(double X, double Y)> points)
    {
        var corners = new List<Corner>();
        RouteSpan? arc = null;
        for (int i = 0; i < spans.Count; i++)
        {
            RouteSpan span = spans[i];
            if (span.IsArc)
            {
                // Spans on one circle are one corner; two rounded corners can meet with no straight between.
                if (arc is not RouteSpan previous || Distance(Centre(previous), Centre(span)) > 1e-6 * SpanRadius(span))
                    corners.Add(new Corner(SpanRadius(span), span.ControlX, span.ControlY));

                arc = span;
                continue;
            }

            arc = null;
            if (i == 0 || spans[i - 1].IsArc || points.Any(point => point.X == span.StartX && point.Y == span.StartY))
                continue;

            (double ox, double oy) = Unit(spans[i - 1].EndX - spans[i - 1].StartX, spans[i - 1].EndY - spans[i - 1].StartY);
            (double ix, double iy) = Unit(span.EndX - span.StartX, span.EndY - span.StartY);
            if ((ox * ix) + (oy * iy) < 1 - 1e-9)
                corners.Add(new Corner(0.0, span.StartX, span.StartY));
        }

        return corners;
    }

    /// <summary>The arc's centre: a radius from its start, square to its start tangent, on the side it turns to.</summary>
    private static (double X, double Y) Centre(RouteSpan arc)
    {
        (double tx, double ty) = Unit(arc.ControlX - arc.StartX, arc.ControlY - arc.StartY);
        double turn = Math.Sign((tx * (arc.EndY - arc.ControlY)) - (ty * (arc.EndX - arc.ControlX)));
        double radius = SpanRadius(arc);
        return (arc.StartX - (ty * radius * turn), arc.StartY + (tx * radius * turn));
    }

    private static double SpanRadius(RouteSpan arc)
    {
        double half = Math.Acos(arc.Weight);
        double chord = Distance((arc.StartX, arc.StartY), (arc.EndX, arc.EndY));
        return 0.5 * chord / Math.Sin(half);
    }

    /// <summary>The steepest grade along the spans, each arc sampled 400 times (a straight is linear).</summary>
    private static double FineMaxGrade(IReadOnlyList<RouteSpan> spans)
    {
        double worst = 0.0;
        foreach (RouteSpan span in spans)
        {
            int samples = span.IsArc ? 400 : 1;
            var previous = span.Evaluate(0.0);
            for (int k = 1; k <= samples; k++)
            {
                var point = span.Evaluate((double)k / samples);
                double plan = Distance((point.X, point.Y), (previous.X, previous.Y));
                if (plan > 1e-9)
                    worst = Math.Max(worst, Math.Abs(point.Z - previous.Z) / plan);
                previous = point;
            }
        }

        return worst;
    }

    private static void AssertContiguousAndTangentAtArcs(IReadOnlyList<RouteSpan> spans)
    {
        for (int i = 1; i < spans.Count; i++)
        {
            RouteSpan a = spans[i - 1], b = spans[i];
            Assert.Equal(a.EndX, b.StartX, 9);
            Assert.Equal(a.EndY, b.StartY, 9);
            Assert.Equal(a.EndZ, b.StartZ, 9);
            if (!a.IsArc && !b.IsArc)
                continue;

            (double ox, double oy) = Unit(a.EndX - a.ControlX, a.EndY - a.ControlY);
            (double ix, double iy) = Unit(b.ControlX - b.StartX, b.ControlY - b.StartY);
            Assert.True((ox * ix) + (oy * iy) > 1 - 1e-6, $"Spans {i} and {i + 1} meet with a kink in plan.");
        }
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static (double X, double Y) Unit(double x, double y)
    {
        double length = Math.Sqrt((x * x) + (y * y));
        return length > 0 ? (x / length, y / length) : (0, 0);
    }
}
