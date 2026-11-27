using MoleHill.Core.Engine;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The graded-rail adoption check. Near a kerb's pointed end the top rail traced along the toe's own
/// vertices; pinning them to the top's height lifted the toe onto the wall top.
/// </summary>
public class RetainingWallRailAdoptionTests
{
    private static ConstraintPolyline Rail(params double[] xyz) => new(xyz, xyz.Length / 3, false, PreserveInputElevation: true);

    [Fact]
    public void FindSharedRailVertex_TwoRailsThroughOneVertexAtTwoHeights_ReportsIt()
    {
        var toe = Rail(0, 0, 0, 1, 0, 0, 2, 0, 0);
        var top = Rail(0, 0.05, 0.15, 1, 0, 0.15, 2, 0.05, 0.15);

        Assert.Equal((1.0, 0.0), RetainingWallStage.FindSharedRailVertex(new[] { toe, top }, 0.001));
    }

    [Fact]
    public void FindSharedRailVertex_RailsMeetingAtOneHeight_IsFine()
    {
        // Two walls joined at a corner share the corner vertex at the same height.
        var a = Rail(0, 0, 1, 1, 0, 1);
        var b = Rail(1, 0, 1, 1, 1, 1);

        Assert.Null(RetainingWallStage.FindSharedRailVertex(new[] { a, b }, 0.001));
    }

    [Fact]
    public void FindSharedRailVertex_SeparateRails_IsFine()
    {
        var toe = Rail(0, 0, 0, 1, 0, 0);
        var top = Rail(0, 0.05, 0.15, 1, 0.05, 0.15);

        Assert.Null(RetainingWallStage.FindSharedRailVertex(new[] { toe, top }, 0.001));
    }

    // The conform routed the rail through a terrain corner 30 mm off the drawn line; across a wall face that
    // put the terrain centimetres off the wall. The chain vertex moves onto the drawn rail, at its height.
    [Fact]
    public void TrySnapTracedRailsOntoDrawn_OffsetChainVertex_MovesOntoDrawnRail()
    {
        // A(0,0) B(1,0.03) C(2,0) on the chain; D above, E below.
        double[] vertices = { 0, 0, 1, 1, 0.03, 0.9, 2, 0, 1, 1, 1, 0.5, 1, -0.5, 0.5 };
        int[] faces = { 0, 1, 3, 1, 2, 3, 0, 4, 1, 1, 4, 2 };
        var traced = Rail(0, 0, 1, 1, 0.03, 0.9, 2, 0, 1);
        var drawn = Rail(0, 0, 1, 2, 0, 1.2);

        Assert.True(RetainingWallStage.TrySnapTracedRailsOntoDrawn(vertices, faces, 4, new[] { traced }, new[] { drawn }, out var snapped));

        Assert.Equal(1.0, vertices[3], 9);
        Assert.Equal(0.0, vertices[4], 9);
        Assert.Equal(1.1, vertices[5], 9);
        Assert.Equal(0.0, Assert.Single(snapped).Points[4], 9);
    }

    // The chain A-B-C zig-zags out to B, so A-C-B is a thin triangle on the rail side. Moving B onto the
    // rail flattens it; flipping edge A-C turns it and A-E-C into A-B-E and B-C-E.
    [Fact]
    public void TrySnapTracedRailsOntoDrawn_ZigZagEar_FlipsTheLongEdge()
    {
        double[] vertices = { 0, 0, 1, 1, 0.03, 1, 2, 0, 1, 1, 1, 0.5, 1, -1, 0.5 };
        int[] faces = { 0, 1, 3, 1, 2, 3, 0, 2, 1, 0, 4, 2 };
        var traced = Rail(0, 0, 1, 1, 0.03, 1, 2, 0, 1);
        var drawn = Rail(0, 0, 1, 2, 0, 1);

        Assert.True(RetainingWallStage.TrySnapTracedRailsOntoDrawn(vertices, faces, 4, new[] { traced }, new[] { drawn }, out _));

        Assert.Equal(0.0, vertices[4], 9);
        var triangles = Enumerable.Range(0, 4).Select(f => new[] { faces[f * 3], faces[f * 3 + 1], faces[f * 3 + 2] }).ToList();
        Assert.DoesNotContain(triangles, t => t.Contains(0) && t.Contains(2));
        Assert.Contains(triangles, t => t.Contains(0) && t.Contains(1) && t.Contains(4));
        Assert.Contains(triangles, t => t.Contains(1) && t.Contains(2) && t.Contains(4));
        foreach (int[] t in triangles)
        {
            double area = ((vertices[t[1] * 3] - vertices[t[0] * 3]) * (vertices[t[2] * 3 + 1] - vertices[t[0] * 3 + 1])) -
                          ((vertices[t[2] * 3] - vertices[t[0] * 3]) * (vertices[t[1] * 3 + 1] - vertices[t[0] * 3 + 1]));
            Assert.True(area > 0, "every triangle keeps its orientation");
        }
    }

    [Fact]
    public void TrySnapTracedRailsOntoDrawn_MoveWouldFoldATriangle_RefusesAndLeavesTheMesh()
    {
        // E sits between B and the drawn line, so moving B onto the line turns triangle A-E-B over.
        double[] vertices = { 0, 0, 1, 1, 0.03, 1, 2, 0, 1, 1, 1, 0.5, 1, 0.01, 0.5 };
        double[] original = (double[])vertices.Clone();
        int[] faces = { 0, 1, 3, 1, 2, 3, 0, 4, 1, 1, 4, 2 };
        var traced = Rail(0, 0, 1, 1, 0.03, 1, 2, 0, 1);
        var drawn = Rail(0, 0, 1, 2, 0, 1);

        Assert.False(RetainingWallStage.TrySnapTracedRailsOntoDrawn(vertices, faces, 4, new[] { traced }, new[] { drawn }, out _));
        Assert.Equal(original, vertices);
    }
}
