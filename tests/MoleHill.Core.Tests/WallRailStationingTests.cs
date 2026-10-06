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
