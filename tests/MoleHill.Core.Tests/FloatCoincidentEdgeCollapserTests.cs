using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The Rhino hand-off welds vertices that share a float position. Far from the origin a remesh can leave such
/// pairs a micron apart, and the blind weld folded a 566k-face terrain (edges shared by four faces). Collapsing
/// the edge, or separating a pair no edge joins, keeps the mesh manifold through that weld.
/// </summary>
public class FloatCoincidentEdgeCollapserTests
{
    private const double Far = 160.0;

    // A fan of four triangles around vertex 4 at the centre of a square 160 m from the origin; vertex 5 sits a
    // tenth of a micron from 4 and splits the triangle 4-0-1 into two.
    private static (double[] V, int[] F) FanWithShortEdge()
    {
        double[] v =
        {
            Far, Far, 0, Far + 2, Far, 0, Far + 2, Far + 2, 0, Far, Far + 2, 0,
            Far + 1, Far + 1, 0,
            Far + 1 + 1e-7, Far + 1 - 1e-7, 0,
        };
        int[] f = { 4, 0, 5, 5, 0, 1, 5, 1, 4, 4, 1, 2, 4, 2, 3, 4, 3, 0 };
        return (v, f);
    }

    [Fact]
    public void Collapse_EdgeShorterThanFloatPrecision_CollapsesItAndStaysManifold()
    {
        (double[] v, int[] f) = FanWithShortEdge();

        (double[] outV, int[] outF) = FloatCoincidentEdgeCollapser.Collapse(v, f, out int collapsed, out int separated);

        Assert.Equal(1, collapsed);
        Assert.Equal(0, separated);
        Assert.Equal(5, outV.Length / 3);
        Assert.Equal(4, outF.Length / 3);
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(outF, outF.Length / 3);
        Assert.Equal(1, topology.BoundaryComponentCount);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.True(MeshArrayNormalizer.TryNormalize(outV, 5, outF, 4, out _, out int vc, out _, out int fc));
        Assert.Equal((5, 4), (vc, fc));
    }

    [Fact]
    public void Collapse_CoincidentPairNotJoinedByAnEdge_SeparatesItAndKeepsTheFaces()
    {
        // Two triangles that touch nowhere, with one corner each a tenth of a micron apart.
        double[] v =
        {
            Far, Far, 0, Far - 1, Far, 0, Far, Far - 1, 0,
            Far + 1e-7, Far, 0, Far + 1, Far, 0, Far, Far + 1, 0,
        };
        int[] f = { 0, 1, 2, 3, 4, 5 };

        (double[] outV, int[] outF) = FloatCoincidentEdgeCollapser.Collapse(v, f, out int collapsed, out int separated);

        Assert.Equal(0, collapsed);
        Assert.Equal(1, separated);
        Assert.Equal(f, outF);
        Assert.NotEqual((float)outV[0], (float)outV[9]);
        Assert.True(Math.Abs(outV[9] - Far) < 1e-4);
    }

    [Fact]
    public void Collapse_NoSharedFloatPositions_ReturnsTheInputs()
    {
        double[] v = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        int[] f = { 0, 1, 2 };

        (double[] outV, int[] outF) = FloatCoincidentEdgeCollapser.Collapse(v, f, out int collapsed, out int separated);

        Assert.Same(v, outV);
        Assert.Same(f, outF);
        Assert.Equal(0, collapsed + separated);
    }
}
