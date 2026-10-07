using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class WallRailStationingTests
{
    [Fact]
    public void Synchronize_DifferentGradesAtMatchingBend_AlignsCornersAndPreservesZ()
    {
        double[] a = { 0, 0, 0, 10, 0, 0, 10, 10, 0 };
        double[] b = { 0, 1, 1, 9, 1, 20, 9, 10, 2 };
        var result = WallRailStationing.Synchronize(a, b, false, .005);

        Assert.Equal(a, result.First);
        Assert.Equal(b, result.Second);
    }

    [Fact]
    public void Synchronize_ExtraCollinearHeightVertex_PreservesVertexWithoutAddingCorner()
    {
        double[] a = { 0, 0, 0, 10, 0, 0, 10, 10, 0 };
        double[] b = { 0, 1, 1, 4.5, 1, 5, 9, 1, 2, 9, 10, 3 };
        var result = WallRailStationing.Synchronize(a, b, false, .005);

        Assert.Equal(new double[] { 0, 0, 0, 5, 0, 0, 10, 0, 0, 10, 10, 0 }, result.First);
        Assert.Equal(b, result.Second);
    }

    [Fact]
    public void Synchronize_ClosedRingWithSeamInsideSegment_AlignsWrapCorner()
    {
        double[] a = { 0, 0, 0, 10, 0, 0, 10, 8, 0, 0, 8, 0 };
        double[] b = { 0, -1, 3, 11, -1, 3, 11, 9, 3, -1, 9, 3, -1, -1, 3 };
        var result = WallRailStationing.Synchronize(a, b, true, .005);

        Assert.Equal(result.First.Length, result.Second.Length);
        double[] expectedPartners = { -1, -1, 3, 11, -1, 3, 11, 9, 3, -1, 9, 3 };
        for (int i = 0; i < a.Length; i += 3)
        {
            int station = -1;
            for (int j = 0; j < result.First.Length; j += 3)
                if (result.First[j] == a[i] && result.First[j + 1] == a[i + 1])
                    station = j;
            Assert.True(station >= 0);
            Assert.Equal(expectedPartners[i], result.Second[station], 8);
            Assert.Equal(expectedPartners[i + 1], result.Second[station + 1], 8);
        }
        AssertStripDoesNotFold(result.First, result.Second, true);
        AssertVerticesRetained(a, result.First);
        AssertVerticesRetained(b, result.Second);
    }

    [Fact]
    public void Synchronize_VerticalStepInTopRail_KeepsBothStepVertices()
    {
        // A stepped wall top: two vertices at one plan position. Plan-length stationing gave them the same
        // station, so one was dropped and the step became a ramp.
        double[] toe = { 0, 0, 0, 10, 0, 0 };
        double[] top = { 0, 1, 1, 5, 1, 1, 5, 1, 3, 10, 1, 3 };
        var result = WallRailStationing.Synchronize(toe, top, false, .005);

        Assert.Equal(result.First.Length, result.Second.Length);
        AssertVerticesRetained(top, result.Second);
        AssertStripDoesNotFold(result.First, result.Second);
    }

    [Fact]
    public void Synchronize_RoundOffsetCornerAgainstSharpCorner_PairsCornerWithArcMiddle()
    {
        // Rhino's Offset with round corners: the inside rail keeps a sharp corner, the outside one gets an
        // arc in 5-degree steps, none of which is a corner alone. Unanchored, the sharp corner paired with
        // a point before the arc on this short-legged hairpin and the arc's cells crossed the next leg.
        const double turn = 150 * Math.PI / 180;
        double[] inside = { 0, 0, 0, 2, 0, 1, 2 + 16 * Math.Cos(turn), 16 * Math.Sin(turn), 2 };
        var outside = new List<double> { 0, -0.5, 0 };
        for (int i = 0; i <= 30; i++)
        {
            double angle = -Math.PI / 2 + turn * i / 30;
            outside.AddRange(new[] { 2 + 0.5 * Math.Cos(angle), 0.5 * Math.Sin(angle), 1 });
        }
        outside.AddRange(new[] { 2 + 16 * Math.Cos(turn) + 0.5 * Math.Sin(turn), 16 * Math.Sin(turn) - 0.5 * Math.Cos(turn), 2 });

        var result = WallRailStationing.Synchronize(inside, outside.ToArray(), false, .005);

        AssertStripDoesNotFold(result.First, result.Second);
        AssertVerticesRetained(inside, result.First);
        AssertVerticesRetained(outside.ToArray(), result.Second);
        double middle = -Math.PI / 2 + turn / 2;
        AssertPartner(result, 2, 0, 2 + 0.5 * Math.Cos(middle), 0.5 * Math.Sin(middle));
    }

    [Fact]
    public void Synchronize_ChamferAgainstSharpCorner_PairsCornerWithChamferMiddle()
    {
        // Two 45-degree vertices are one 90-degree bend; neither matched the sharp corner on its own.
        double[] inside = { 0, 0, 0, 2, 0, 0, 2, 15, 0 };
        double[] outside = { 0, -1, 1, 2.6, -1, 1, 3, -0.6, 1, 3, 15, 1 };

        var result = WallRailStationing.Synchronize(inside, outside, false, .005);

        AssertStripDoesNotFold(result.First, result.Second);
        AssertVerticesRetained(outside, result.Second);
        AssertPartner(result, 2, 0, 2.8, -0.8);
    }

    [Fact]
    public void Synchronize_VertexJustBesidePartnerVertex_MergesSubToleranceSpan()
    {
        // Two micrometres apart, the rails' own vertices made a loft span far below the build tolerance.
        double[] a = { 0, 0, 0, 5, 0, 0, 10, 0, 0 };
        double[] b = { 0, 1, 1, 5.000002, 1, 1, 10, 1, 1 };

        var result = WallRailStationing.Synchronize(a, b, false, .005);

        Assert.Equal(a, result.First);
        Assert.Equal(b, result.Second);
    }

    [Fact]
    public void Synchronize_DoubledVertex_LeavesNoZeroLengthSpan()
    {
        // The same point drawn twice gave two stations at one place: a zero-length loft span.
        double[] a = { 0, 0, 0, 5, 0, 0, 5, 0, 0, 10, 0, 0 };
        double[] b = { 0, 1, 1, 10, 1, 1 };

        var result = WallRailStationing.Synchronize(a, b, false, .005);

        Assert.Equal(new double[] { 0, 0, 0, 5, 0, 0, 10, 0, 0 }, result.First);
        AssertStripDoesNotFold(result.First, result.Second);
    }

    [Fact]
    public void Synchronize_ClosedRingWithSeamInsideFillet_PairsEveryCornerWithArcMiddle()
    {
        // A rounded-corner ring whose seam falls mid-fillet, inside a sharp rectangle. Runs do not wrap a
        // seam, so that fillet was two partial bends that matched nothing.
        double[] inside = { 0, 0, 0, 10, 0, 0, 10, 6, 0, 0, 6, 0 };
        var outside = new List<double>();
        (double X, double Y, double From)[] corners = { (10, 0, -90), (10, 6, 0), (0, 6, 90), (0, 0, 180) };
        foreach (var corner in corners)
        {
            for (int i = 0; i <= 18; i++)
            {
                double angle = (corner.From + 5 * i) * Math.PI / 180;
                outside.AddRange(new[] { corner.X + Math.Cos(angle), corner.Y + Math.Sin(angle), 2 });
            }
        }
        double[] ring = outside.ToArray();
        // Start the ring at the middle of the last fillet.
        int startVertex = 3 * 19 + 9;
        double[] rotated = new double[ring.Length];
        for (int i = 0; i < ring.Length / 3; i++)
            Array.Copy(ring, ((startVertex + i) % (ring.Length / 3)) * 3, rotated, i * 3, 3);

        var result = WallRailStationing.Synchronize(inside, rotated, true, .005);

        AssertStripDoesNotFold(result.First, result.Second, true);
        AssertVerticesRetained(inside, result.First);
        foreach (var corner in corners)
        {
            double middle = (corner.From + 45) * Math.PI / 180;
            AssertPartner(result, corner.X, corner.Y, corner.X + Math.Cos(middle), corner.Y + Math.Sin(middle));
        }
    }

    [Fact]
    public void Synchronize_SmallKinkBeforeFillet_PairsTheArcsEndToEnd()
    {
        // Concentric quarter fillets (radius 10 and 9, centre (10, 10)) between straight legs. The outer rail
        // also turns 1 degree half a metre before its arc, which joins its bend. Anchoring that rail's arc at
        // the kink paired it with the inner rail's tangent point and sheared the leg.
        static double[] Rail(double radius, bool kink)
        {
            double y = 10 - radius;
            var points = new List<double> { 0, y, 0 };
            if (kink)
                points.AddRange(new[] { 9.5, y - 0.5 * Math.Tan(Math.PI / 180), 0 });
            for (int i = 0; i <= 18; i++)
            {
                double angle = (-90 + 5 * i) * Math.PI / 180;
                points.AddRange(new[] { 10 + radius * Math.Cos(angle), 10 + radius * Math.Sin(angle), 0 });
            }
            points.AddRange(new[] { 10 + radius, 20, 0 });
            return points.ToArray();
        }

        var result = WallRailStationing.Synchronize(Rail(10, kink: true), Rail(9, kink: false), false, .005);

        AssertStripDoesNotFold(result.First, result.Second);
        double middle = -Math.PI / 4;
        AssertPartner(result, 10 + 10 * Math.Cos(middle), 10 + 10 * Math.Sin(middle), 10 + 9 * Math.Cos(middle), 10 + 9 * Math.Sin(middle), within: 0.05);
        AssertPartner(result, 20, 10, 19, 10, within: 0.1);
        // The tangent point pairs within a few centimetres; anchored at the kink it was half a metre off.
        for (int j = 0; j < result.First.Length; j += 3)
            if (Math.Abs(result.First[j] - 10) < 1e-9 && Math.Abs(result.First[j + 1]) < 1e-9)
                Assert.InRange(result.Second[j], 9.9, 10.1);
    }

    [Fact]
    public void Synchronize_DenseCirclesWithOffsetSeams_StaysFastAndUnfolded()
    {
        // An evenly stepped ring has no gap to split at. Peeling an unmatched ring one vertex per round
        // was O(n^2) on a dense planter wall.
        static double[] Circle(double radius, int count, double phase)
        {
            var points = new double[count * 3];
            for (int i = 0; i < count; i++)
            {
                double angle = phase + 2 * Math.PI * i / count;
                points[i * 3] = radius * Math.Cos(angle);
                points[i * 3 + 1] = radius * Math.Sin(angle);
            }
            return points;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = WallRailStationing.Synchronize(Circle(20, 2000, 0), Circle(19, 1500, 0.3), true, .005);
        watch.Stop();

        AssertStripDoesNotFold(result.First, result.Second, true);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"Took {watch.ElapsedMilliseconds} ms.");
    }

    private static void AssertPartner(
        WallRailStationing.Result result, double x, double y, double partnerX, double partnerY, double within = 0)
    {
        for (int j = 0; j < result.First.Length; j += 3)
        {
            if (Math.Abs(result.First[j] - x) < 1e-9 && Math.Abs(result.First[j + 1] - y) < 1e-9)
            {
                if (within > 0)
                {
                    Assert.InRange(result.Second[j], partnerX - within, partnerX + within);
                    Assert.InRange(result.Second[j + 1], partnerY - within, partnerY + within);
                    return;
                }
                Assert.Equal(partnerX, result.Second[j], 6);
                Assert.Equal(partnerY, result.Second[j + 1], 6);
                return;
            }
        }
        Assert.Fail($"No station at ({x}, {y}).");
    }

    internal static void AssertVerticesRetained(double[] source, double[] sampled)
    {
        for (int i = 0; i < source.Length; i += 3)
        {
            bool found = false;
            for (int j = 0; j < sampled.Length; j += 3)
                if (Math.Abs(source[i] - sampled[j]) < 1e-8 &&
                    Math.Abs(source[i + 1] - sampled[j + 1]) < 1e-8 &&
                    Math.Abs(source[i + 2] - sampled[j + 2]) < 1e-8)
                    found = true;
            Assert.True(found, $"Missing authored vertex {i / 3}.");
        }
    }

    internal static void AssertStripDoesNotFold(double[] a, double[] b, bool closed = false)
    {
        int count = a.Length / 3;
        int winding = 0;
        for (int i = 0; i < count - (closed ? 0 : 1); i++)
        {
            int p = i * 3, q = (i + 1) * 3 % a.Length;
            double first = Cross(a[p], a[p + 1], a[q], a[q + 1], b[q], b[q + 1]);
            double second = Cross(a[p], a[p + 1], b[q], b[q + 1], b[p], b[p + 1]);
            foreach (double area in new[] { first, second })
            {
                if (Math.Abs(area) < 1e-10)
                    continue;
                if (winding == 0)
                    winding = Math.Sign(area);
                Assert.True(winding == Math.Sign(area), $"Folded wall strip at station {i}.");
            }
        }
    }

    private static double Cross(double ax, double ay, double bx, double by, double cx, double cy) =>
        (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
}
