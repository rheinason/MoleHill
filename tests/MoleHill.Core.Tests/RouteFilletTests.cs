using MoleHill.Core.Routing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class RouteFilletTests
{
    [Fact]
    public void Fillet_RightAngle_TakesTheRequestedRadiusTangentially()
    {
        var plan = new List<(double X, double Y)> { (0, 0), (100, 0), (100, 100) };

        List<PlanPiece> pieces = PlanFillet.Fillet(plan, 20.0, out double? smallest, out int reduced);

        Assert.Equal(3, pieces.Count);
        PlanPiece arc = Assert.Single(pieces, piece => piece.IsArc);
        Assert.Equal(20.0, arc.Radius, 9);
        Assert.Equal(20.0, smallest!.Value, 9);
        Assert.Equal(0, reduced);
        Assert.Equal(Math.PI / 2, arc.Sweep, 9);
        AssertContiguousAndTangent(pieces);
        Assert.Equal((0.0, 0.0), (pieces[0].StartX, pieces[0].StartY));
        Assert.Equal((100.0, 100.0), (pieces[^1].EndX, pieces[^1].EndY));
    }

    [Fact]
    public void Fillet_SegmentsTooShort_UseTheLargestRadiusThatFits()
    {
        // Two right-angle corners 10 apart: each wants a 20 tangent, the shared segment has 10 for both.
        var plan = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (20, 10) };

        List<PlanPiece> pieces = PlanFillet.Fillet(plan, 20.0, out double? smallest, out int reduced);

        Assert.Equal(2, reduced);
        Assert.Equal(5.0, smallest!.Value, 9);
        Assert.All(pieces.Where(piece => piece.IsArc), arc => Assert.Equal(5.0, arc.Radius, 9));
        AssertContiguousAndTangent(pieces);
    }

    [Fact]
    public void Fillet_ZeroRadiusOrStraight_KeepsThePolyline()
    {
        var corner = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10) };
        Assert.DoesNotContain(PlanFillet.Fillet(corner, 0.0, out _, out _), piece => piece.IsArc);

        var straight = new List<(double X, double Y)> { (0, 0), (10, 0), (20, 0) };
        List<PlanPiece> pieces = PlanFillet.Fillet(straight, 5.0, out double? smallest, out int reduced);
        Assert.DoesNotContain(pieces, piece => piece.IsArc);
        Assert.Null(smallest);
        Assert.Equal(0, reduced);
    }

    [Fact]
    public void Suggest_MinimumRadius_RoundsCornersAndHoldsTheGrade()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 121, 121, Hill);
        var request = new RouteRequest
        {
            Points = new[] { (10.0, 60.0), (110.0, 60.0) },
            MaxGrade = 0.05,
            CorridorWidth = 4.0,
            MinimumRadius = 15.0,
        };

        RouteSuggestionResult result = RouteSuggester.Suggest(grid, request);

        Assert.True(result.Succeeded);
        foreach (RouteAlternative route in result.Alternatives)
        {
            Assert.True(route.HasArcs, "Going round the hill turns, so a rounded route has arcs.");
            Assert.NotNull(route.Fillet.SmallestRadius);
            foreach (RouteSpan arc in route.Spans.Where(span => span.IsArc))
            {
                double radius = SpanRadius(arc);
                Assert.True(radius >= route.Fillet.SmallestRadius!.Value - 1e-6);
                if (route.Fillet.ReducedCorners == 0)
                    Assert.True(radius >= 15.0 - 1e-6, $"Arc radius {radius:F3} is under the minimum.");
            }

            AssertSpansTangent(route.Spans);
            Assert.True(route.Metrics.MaxGrade <= 0.05 * (1 + 1e-9));
            Assert.True(FineMaxGrade(route.Spans) <= 0.05 * (1 + 1e-6), "Measured finely, the arcs must hold the grade too.");
            Assert.Equal(10.0, route.PointsXyz[0], 9);
            Assert.Equal(110.0, route.PointsXyz[^3], 9);
        }
    }

    [Fact]
    public void Suggest_NoMinimumRadius_IsStraightSpansOnly()
    {
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 1.0, 121, 121, Hill);
        var request = new RouteRequest { Points = new[] { (10.0, 60.0), (110.0, 60.0) }, MaxGrade = 0.05, CorridorWidth = 4.0 };

        RouteAlternative route = RouteSuggester.Suggest(grid, request).Alternatives[0];

        Assert.False(route.HasArcs);
        Assert.Equal(route.PointCount - 1, route.Spans.Count);
        Assert.Null(route.Fillet.SmallestRadius);
    }

    private static double Hill(double x, double y)
    {
        double dx = x - 60.0, dy = y - 60.0;
        return 10.0 * Math.Exp(-((dx * dx) + (dy * dy)) / (2.0 * 8.0 * 8.0));
    }

    private static double SpanRadius(RouteSpan arc)
    {
        double half = Math.Acos(arc.Weight);
        double chord = Math.Sqrt(Math.Pow(arc.EndX - arc.StartX, 2) + Math.Pow(arc.EndY - arc.StartY, 2));
        return 0.5 * chord / Math.Sin(half);
    }

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
                double plan = Math.Sqrt(Math.Pow(point.X - previous.X, 2) + Math.Pow(point.Y - previous.Y, 2));
                if (plan > 1e-9)
                    worst = Math.Max(worst, Math.Abs(point.Z - previous.Z) / plan);
                previous = point;
            }
        }

        return worst;
    }

    private static void AssertSpansTangent(IReadOnlyList<RouteSpan> spans)
    {
        for (int i = 1; i < spans.Count; i++)
        {
            RouteSpan a = spans[i - 1], b = spans[i];
            Assert.Equal(a.EndX, b.StartX, 9);
            Assert.Equal(a.EndY, b.StartY, 9);
            Assert.Equal(a.EndZ, b.StartZ, 9);
            if (!a.IsArc && !b.IsArc)
                continue; // A straight-to-straight joint is a profile break or a sharp corner by design.

            (double ox, double oy) = Unit(a.EndX - a.ControlX, a.EndY - a.ControlY);
            (double ix, double iy) = Unit(b.ControlX - b.StartX, b.ControlY - b.StartY);
            Assert.True((ox * ix) + (oy * iy) > 1 - 1e-6, $"Spans {i} and {i + 1} meet with a kink in plan.");
        }
    }

    private static void AssertContiguousAndTangent(List<PlanPiece> pieces)
    {
        for (int i = 1; i < pieces.Count; i++)
        {
            Assert.Equal(pieces[i - 1].EndX, pieces[i].StartX, 9);
            Assert.Equal(pieces[i - 1].EndY, pieces[i].StartY, 9);
            Assert.Equal(pieces[i - 1].PointAt(1.0).X, pieces[i].StartX, 9);
            Assert.Equal(pieces[i - 1].PointAt(1.0).Y, pieces[i].StartY, 9);
            (double ox, double oy) = pieces[i - 1].TangentAt(1.0);
            (double ix, double iy) = pieces[i].TangentAt(0.0);
            Assert.True((ox * ix) + (oy * iy) > 1 - 1e-9, $"Pieces {i} and {i + 1} meet with a kink.");
        }
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        double length = Math.Sqrt((x * x) + (y * y));
        return (x / length, y / length);
    }
}
