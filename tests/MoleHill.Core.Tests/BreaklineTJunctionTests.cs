using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A breakline drawn to end on another (snapped in CAD) leaves its end vertex on the other's segment to the last
/// bit, yet a few ulps to one side for Triangle's exact predicates. Triangle then fails the constrained build with
/// "Topological inconsistency after splitting a segment", and one such junction dropped every breakline and
/// contour of a real terrain to plain Delaunay. The coordinates below are that junction.
/// </summary>
public class BreaklineTJunctionTests
{
    // Segment 0 (vertices 0-1) is the through line; segment 1 (vertices 2-3) starts on it.
    private static readonly double[] JunctionXy =
    {
        9.981870644749284, -17.478287154703004,
        8.655837621216548, -16.84259227222746,
        9.595337486081245, -17.292984717650754,
        9.065892167230155, -17.713010879322134,
        5.0, -22.0,
        14.0, -22.0,
        14.0, -12.0,
        5.0, -12.0,
    };

    private static readonly double[] JunctionZ =
    {
        35.73123203617978, 35.72000000000001, 35.72795794394694, 35.71088056352849, 35.5, 35.5, 36.0, 36.0,
    };

    private static readonly int[] JunctionSegments = { 0, 1, 2, 3 };

    [Fact]
    public void Build_BreaklineEndingOnAnother_KeepsBothBreaklines()
    {
        TinResult? result = new TinEngine().Build(
            JunctionXy, JunctionZ, JunctionSegments, QualitySettings.None, out string? message, useConvexHull: true);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrEmpty(message), message);
        Assert.True(HasEdge(result!, 0, 2), "the through line's first half is an edge");
        Assert.True(HasEdge(result!, 2, 1), "the through line's second half is an edge");
        Assert.True(HasEdge(result!, 2, 3), "the ending line is an edge");
    }

    [Fact]
    public void SplitAtVertices_VertexOnSegmentInterior_SplitsThatSegmentInOrder()
    {
        double[] xy = { 0, 0, 10, 0, 7, 0, 3, 0, 5, 5 };
        int[] segments = { 0, 1, 4, 2 };

        int[] split = BreaklineTJunctions.SplitAtVertices(xy, segments, 1e-9, out int count);

        Assert.Equal(2, count);
        Assert.Equal(new[] { 0, 3, 3, 2, 2, 1, 4, 2 }, split);
    }

    [Fact]
    public void SplitAtVertices_VertexOffTheLineOrAtAnEnd_LeavesSegmentsAlone()
    {
        double[] xy = { 0, 0, 10, 0, 5, 0.001, 1e-12, 1e-12, 5, 5 };
        int[] segments = { 0, 1 };

        int[] split = BreaklineTJunctions.SplitAtVertices(xy, segments, 1e-9, out int count);

        Assert.Equal(0, count);
        Assert.Same(segments, split);
    }

    private static bool HasEdge(TinResult result, int a, int b)
    {
        int[] faces = result.Faces;
        for (int f = 0; f < faces.Length; f += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                int u = faces[f + k], v = faces[f + ((k + 1) % 3)];
                if ((u == a && v == b) || (u == b && v == a))
                    return true;
            }
        }

        return false;
    }
}
