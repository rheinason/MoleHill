using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshTopologyValidatorTests
{
    [Fact]
    public void AnalyzeBoundaryGraph_BranchedBoundary_ReportsOpenChain()
    {
        int[] faces =
        {
            0, 1, 2,
            0, 3, 4
        };

        var analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount: 2);

        Assert.Equal(1, analysis.BoundaryComponentCount);
        Assert.True(analysis.HasOpenBoundaryChains);
        Assert.False(analysis.HasSingleClosedBoundaryLoop);
    }

    [Fact]
    public void AnalyzeBoundaryGraph_NonManifoldInternalEdge_ReportsInvalidTopology()
    {
        int[] faces =
        {
            0, 1, 2,
            1, 0, 3,
            0, 1, 4
        };

        var analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount: 3);

        Assert.Equal(1, analysis.NonManifoldEdgeCount);
        Assert.False(analysis.HasSingleClosedBoundaryLoop);
    }
}
