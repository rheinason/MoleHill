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
    public void Suggest_RoundingBreaksTheGrade_RoundsOnlyAsFarAsTheGradeAllowsAndSaysWhy()
    {
        // An L-shaped strip of ground climbing 4.8% along its length: just inside 5% as drawn, but a
        // 10 m curve at the bend cuts 4.3 m off the 100 m leg, which would need over 5%. Held to the
        // length its climb needs, the leg keeps the bend tighter than 10 m — rounded as far as the
        // grade allows, no further — and says the grade is why.
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(0, 0, 0.5, 121, 121, (x, y) =>
            (Math.Abs(y - 5.0) <= 1.5 && x <= 57.0) || (Math.Abs(x - 55.0) <= 1.5 && y <= 57.0)
                ? 0.048 * ((x - 5.0) + (y - 5.0))
                : double.NaN);
        var request = new RouteRequest
        {
            Points = new[] { (5.0, 5.0), (55.0, 55.0) },
            MaxGrade = 0.05,
            CorridorWidth = 2.0,
            MinimumRadius = 10.0,
            AlternativeCount = 1,
        };

        RouteSuggestionResult result = RouteSuggester.Suggest(grid, request);

        Assert.True(result.Succeeded, result.Failure.ToString());
        RouteAlternative route = Assert.Single(result.Alternatives);
        Assert.True(route.Fillet.GradeLimitedCorners > 0);
        Assert.Equal(route.Fillet.GradeLimitedCorners, route.Fillet.ReducedCorners);
        Assert.Equal(0, route.Fillet.TerrainLimitedCorners);
        Assert.True(route.Fillet.SmallestRadius < 10.0);
        Assert.True(route.Metrics.MaxGrade <= 0.05 * (1 + 1e-9));
    }

    [Fact]
    public void MakeRoom_BendOfShortSegments_BecomesOneCornerThatTakesTheRadius()
    {
        // A right-angle bend round the corner of a hole, drawn as six 15-degree turns 2 apart: each
        // corner wants 1.3 of a 2 segment at radius 10, so as drawn they round to under 8. Merged, the
        // bend is one 10 m curve, which still clears the hole.
        var plan = new List<(double X, double Y)> { (0, 0) };
        (double x, double y, double heading) = (60.0, 0.0, 0.0);
        plan.Add((x, y));
        for (int k = 0; k < 6; k++)
        {
            heading += Math.PI / 12;
            x += 2.0 * Math.Cos(heading);
            y += 2.0 * Math.Sin(heading);
            plan.Add((x, y));
        }

        plan.Add((x, y + 60.0));
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(-10, -10, 1.0, 101, 101, (px, py) => px < 55.0 && py > 5.0 ? double.NaN : 0.0);
        var price = new RouteCost(new RouteRequest { Points = new[] { plan[0], plan[^1] }, MaxGrade = 0.05, CorridorWidth = 2.0 });
        PlanFillet.Fillet(plan, 10.0, out _, out int before);

        List<(double X, double Y)> roomy = PlanRadiusSimplifier.MakeRoom(grid, price, plan, 10.0);
        List<PlanPiece> pieces = PlanFillet.Fillet(roomy, 10.0, out double? smallest, out int after);

        Assert.True(before > 0);
        Assert.Equal(0, after);
        Assert.Equal(10.0, smallest!.Value, 9);
        Assert.Equal(plan[0], roomy[0]);
        Assert.Equal(plan[^1], roomy[^1]);
        AssertContiguousAndTangent(pieces);
        double drawn = pieces.Sum(piece => piece.Length);
        double sharp = PlanFillet.Fillet(plan, 0.0, out _, out _).Sum(piece => piece.Length);
        // On level ground cost is length, and the cheapest way to give the bend room is the merged curve:
        // no longer than a 16-connected grid path's own slack (2.7%) over the line it was drawn from.
        Assert.True(drawn <= sharp * 1.027, "On level ground the merged curve may not run longer than the search's resolution.");
    }

    [Fact]
    public void MakeRoom_ChordOverAHole_IsNotTaken()
    {
        // A zig-zag round the corner of a hole: every way of giving it room cuts across the hole, so it
        // stays as drawn and its corners stay tight.
        var plan = new List<(double X, double Y)> { (0, 0), (50, 0), (50.5, 0.5), (51, 0.5), (51, 50) };
        RouteHeightGrid grid = RouteHeightGrid.FromFunction(-10, -10, 0.5, 141, 141, (px, py) =>
            px > 30.0 && px < 50.0 && py > 1.0 ? double.NaN : px > 52.0 || (py < -1.0 && px > 30.0) ? double.NaN : 0.0);
        var price = new RouteCost(new RouteRequest { Points = new[] { plan[0], plan[^1] }, MaxGrade = 0.05, CorridorWidth = 2.0 });

        List<(double X, double Y)> roomy = PlanRadiusSimplifier.MakeRoom(grid, price, plan, 10.0);

        foreach (PlanPiece piece in PlanFillet.Fillet(roomy, 10.0, out _, out _))
        {
            for (int k = 0; k <= 20; k++)
            {
                (double px, double py) = piece.PointAt(k / 20.0);
                Assert.True(grid.TrySample(px, py, 0.75, out _), $"The rounded plan leaves the ground at ({px:F2}, {py:F2}).");
            }
        }
    }

    [Fact]
    public void Fillet_TwoCornersOnOneCircle_CountAsOneTightCorner()
    {
        // Two 45-degree left turns 2 apart: at radius 20 they share the segment and meet on one circle.
        var plan = new List<(double X, double Y)> { (0, 0), (100, 0), (100 + Math.Sqrt(2), Math.Sqrt(2)), (100 + Math.Sqrt(2), 100) };

        List<PlanPiece> pieces = PlanFillet.Fillet(plan, 20.0, out double? smallest, out int reduced);

        Assert.Equal(1, reduced);
        Assert.True(smallest < 20.0);
        AssertContiguousAndTangent(pieces);
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
