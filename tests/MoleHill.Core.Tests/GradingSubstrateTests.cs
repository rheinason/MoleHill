using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class GradingSubstrateTests
{
    [Fact]
    public void TerrainSpatialIndex_BuildsBoundaryLoop_AndInterpolatesZ()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 1.0,
            1.0, 1.0, 2.0,
            0.0, 1.0, 1.0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        var index = new TerrainSpatialIndex(vertices, 4, faces, 2);

        Assert.True(index.HasBoundaryLoop);
        Assert.True(index.BoundaryVertexCount >= 4);
        Assert.Equal(1.0, index.InterpolateZ(0.5, 0.5), 6);
        Assert.True(index.Bounds.Intersects(new Bounds2D(0.25, 0.75, 0.25, 0.75)));
    }

    [Fact]
    public void SeamGraph_Build_ReturnsFullMatches_ForIdenticalSeamSegmentation()
    {
        double[] seamLoop =
        {
            0.0, 0.0,
            1.0, 0.0,
            1.0, 1.0,
            0.0, 1.0
        };
        double[] meshVertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0
        };
        int[] meshFaces = { 0, 1, 2, 0, 2, 3 };

        SeamGraph graph = SeamGraph.Build(seamLoop, meshVertices, meshFaces, 2, meshVertices, meshFaces, 2, 1e-6);

        Assert.True(graph.PatchHasFullSegmentMatch);
        Assert.True(graph.TerrainHasFullSegmentMatch);
        Assert.Equal(4, graph.PatchMatchedSegments);
        Assert.Equal(4, graph.TerrainMatchedSegments);
    }

    [Fact]
    public void SeamGraph_Build_DetectsMissingSegmentBreaks()
    {
        double[] seamLoop =
        {
            0.0, 0.0,
            0.5, 0.0,
            1.0, 0.0,
            1.0, 1.0,
            0.0, 1.0
        };
        double[] coarseVertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0
        };
        int[] coarseFaces = { 0, 1, 2, 0, 2, 3 };

        SeamGraph graph = SeamGraph.Build(seamLoop, coarseVertices, coarseFaces, 2, coarseVertices, coarseFaces, 2, 1e-6);

        Assert.False(graph.PatchHasFullSegmentMatch);
        Assert.False(graph.TerrainHasFullSegmentMatch);
        Assert.True(graph.PatchMatchedSegments < seamLoop.Length / 2);
    }

    [Fact]
    public void SeamGraph_Build_AcceptsBoundarySubsegmentsCoveringSeam()
    {
        double[] seamLoop =
        {
            0.0, 0.0,
            1.0, 0.0,
            1.0, 1.0,
            0.0, 1.0
        };
        double[] splitVertices =
        {
            0.0, 0.0, 0.0,
            0.5, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0
        };
        int[] splitFaces = { 0, 1, 4, 1, 2, 3, 1, 3, 4 };

        SeamGraph graph = SeamGraph.Build(seamLoop, splitVertices, splitFaces, 3, splitVertices, splitFaces, 3, 1e-6);

        Assert.True(graph.PatchHasFullSegmentMatch);
        Assert.True(graph.TerrainHasFullSegmentMatch);
        Assert.Equal(4, graph.PatchMatchedSegments);
        Assert.Equal(4, graph.TerrainMatchedSegments);
    }

    [Fact]
    public void DirtyRegionPlanner_Build_ReturnsChangedAndIntersectingOwners()
    {
        var patches = new[]
        {
            new GradingPatch
            {
                OwnerKey = "pad:0",
                Kind = GradingPatchKind.Pad,
                Priority = 1.0,
                OwnedRegionLoopXy = new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 2.0, 0.0, 2.0 },
                DirtyBounds = new Bounds2D(0.0, 2.0, 0.0, 2.0)
            },
            new GradingPatch
            {
                OwnerKey = "pad:1",
                Kind = GradingPatchKind.Pad,
                Priority = 2.0,
                OwnedRegionLoopXy = new[] { 1.5, 1.5, 3.0, 1.5, 3.0, 3.0, 1.5, 3.0 },
                DirtyBounds = new Bounds2D(1.5, 3.0, 1.5, 3.0)
            },
            new GradingPatch
            {
                OwnerKey = "path:0",
                Kind = GradingPatchKind.Path,
                Priority = 0.0,
                OwnedRegionLoopXy = new[] { 5.0, 5.0, 6.0, 5.0, 6.0, 6.0, 5.0, 6.0 },
                DirtyBounds = new Bounds2D(5.0, 6.0, 5.0, 6.0)
            }
        };

        DirtyRegionPlanner.Plan plan = DirtyRegionPlanner.Build(patches, "pad:0", 0.1);

        Assert.Contains("pad:0", plan.AffectedOwnerKeys);
        Assert.Contains("pad:1", plan.AffectedOwnerKeys);
        Assert.DoesNotContain("path:0", plan.AffectedOwnerKeys);
        Assert.True(plan.DirtyBounds.Intersects(new Bounds2D(1.6, 1.7, 1.6, 1.7)));
    }

    [Fact]
    public void MeshArtifactCleaner_Clean_RemovesDetachedTinyComponents()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0,
            10.0, 10.0, 0.0,
            10.1, 10.0, 0.0,
            10.0, 10.1, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 2, 3,
            4, 5, 6
        };

        MeshArtifactCleaner.CleanupResult result = MeshArtifactCleaner.Clean(
            vertices,
            7,
            faces,
            3,
            new MeshArtifactCleaner.Options(
                MinComponentFaceCount: 2,
                MinComponentAreaRatio: 0.01,
                KeepLargestComponentOnly: false));

        Assert.Equal(2, result.Before.ComponentCount);
        Assert.Equal(1, result.After.ComponentCount);
        Assert.Equal(1, result.RemovedComponentCount);
        Assert.Equal(1, result.RemovedFaceCount);
        Assert.Equal(2, result.FaceCount);
        Assert.Equal(4, result.VertexCount);
    }

    [Fact]
    public void MeshArtifactCleaner_Clean_PreservesComparableLargeComponents()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0,
            3.0, 0.0, 0.0,
            4.0, 0.0, 0.0,
            4.0, 1.0, 0.0,
            3.0, 1.0, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 2, 3,
            4, 5, 6,
            4, 6, 7
        };

        MeshArtifactCleaner.CleanupResult result = MeshArtifactCleaner.Clean(
            vertices,
            8,
            faces,
            4,
            new MeshArtifactCleaner.Options(
                MinComponentFaceCount: 2,
                MinComponentAreaRatio: 0.5,
                KeepLargestComponentOnly: false));

        Assert.Equal(2, result.Before.ComponentCount);
        Assert.Equal(2, result.After.ComponentCount);
        Assert.Equal(0, result.RemovedComponentCount);
        Assert.Equal(0, result.RemovedFaceCount);
        Assert.Equal(4, result.FaceCount);
        Assert.Equal(8, result.VertexCount);
    }
}
