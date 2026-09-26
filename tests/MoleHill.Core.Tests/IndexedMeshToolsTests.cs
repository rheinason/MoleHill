using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class IndexedMeshToolsTests
{
    [Fact]
    public void CountFaceEdges_TwoTrianglesSharingAnEdge_CountsSharedEdgeTwice()
    {
        // 0-1-2 and 2-1-3 share edge 1-2.
        int[] faces = { 0, 1, 2, 2, 1, 3 };

        Dictionary<long, int> counts = IndexedMeshTools.CountFaceEdges(faces, 2);

        Assert.Equal(5, counts.Count);
        Assert.Equal(2, counts[IndexedMeshTools.GetEdgeKey(1, 2)]);
        Assert.Equal(1, counts[IndexedMeshTools.GetEdgeKey(0, 1)]);
        Assert.Equal(1, counts[IndexedMeshTools.GetEdgeKey(3, 2)]);
    }

    [Fact]
    public void CountFaceEdges_Enumeration_FollowsFirstUseOrder()
    {
        // Outline chaining walks the naked edges in this order, so it is part of the contract.
        int[] faces = { 5, 9, 2, 2, 9, 7 };

        long[] order = IndexedMeshTools.CountFaceEdges(faces, 2).Keys.ToArray();

        Assert.Equal(
            new[]
            {
                IndexedMeshTools.GetEdgeKey(5, 9),
                IndexedMeshTools.GetEdgeKey(9, 2),
                IndexedMeshTools.GetEdgeKey(2, 5),
                IndexedMeshTools.GetEdgeKey(9, 7),
                IndexedMeshTools.GetEdgeKey(7, 2),
            },
            order);
    }

    [Fact]
    public void CountFaceEdges_NoFaces_ReturnsEmpty()
    {
        Assert.Empty(IndexedMeshTools.CountFaceEdges(Array.Empty<int>(), 0));
    }
}
