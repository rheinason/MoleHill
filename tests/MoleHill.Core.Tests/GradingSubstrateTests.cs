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
    public void GradingResultBuilder_BuildFromComponents_ComputesVolumeAndDaylight()
    {
        double[] xy =
        {
            0.0, 0.0,
            1.0, 0.0,
            1.0, 1.0,
            0.0, 1.0
        };
        double[] originalZ = { 0.0, 0.0, 0.0, 0.0 };
        double[] gradedZ = { 0.0, 1.0, 1.0, 0.0 };
        double[] gradedVertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 1.0,
            1.0, 1.0, 1.0,
            0.0, 1.0, 0.0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        GradingResult result = GradingResultBuilder.BuildFromComponents(
            xy,
            originalZ,
            gradedZ,
            gradedVertices,
            4,
            faces,
            2);

        Assert.Equal(0.0, result.CutVolume, 6);
        Assert.Equal(0.5, result.FillVolume, 6);
        Assert.True(result.DaylightVertexCount >= 2);
        Assert.NotSame(faces, result.Faces);
    }

    [Fact]
    public void MeshTopologyOperations_MergeMeshes_RespectsCoincidentVertexZPolicy()
    {
        double[] firstVertices =
        {
            0.0, 0.0, 1.0,
            1.0, 0.0, 1.0,
            0.0, 1.0, 1.0
        };
        double[] secondVertices =
        {
            0.0, 0.0, 5.0,
            1.0, 0.0, 5.0,
            0.0, 1.0, 5.0
        };
        int[] faces = { 0, 1, 2 };

        MeshTopologyOperations.MergeMeshes(
            firstVertices,
            3,
            faces,
            1,
            secondVertices,
            3,
            faces,
            1,
            1e-6,
            CoincidentVertexZPolicy.KeepFirst,
            out double[] keepFirstVertices,
            out int keepFirstVertexCount,
            out _,
            out int keepFirstFaceCount);

        MeshTopologyOperations.MergeMeshes(
            firstVertices,
            3,
            faces,
            1,
            secondVertices,
            3,
            faces,
            1,
            1e-6,
            CoincidentVertexZPolicy.UseLatest,
            out double[] useLatestVertices,
            out int useLatestVertexCount,
            out _,
            out int useLatestFaceCount);

        Assert.Equal(3, keepFirstVertexCount);
        Assert.Equal(1, keepFirstFaceCount);
        Assert.Equal(1.0, keepFirstVertices[2], 6);
        Assert.Equal(3, useLatestVertexCount);
        Assert.Equal(1, useLatestFaceCount);
        Assert.Equal(5.0, useLatestVertices[2], 6);
    }

    [Fact]
    public void MeshBoundaryLoopBuilder_TryBuildBoundaryLoop_ReturnsBoundaryZ()
    {
        double[] vertices =
        {
            0.0, 0.0, 2.0,
            1.0, 0.0, 3.0,
            1.0, 1.0, 4.0,
            0.0, 1.0, 5.0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        bool success = MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(
            vertices,
            faces,
            2,
            1e-6,
            out double[] boundaryXy,
            out double[] boundaryZ);

        Assert.True(success);
        Assert.Equal(boundaryXy.Length / 2, boundaryZ.Length);
        Assert.Contains(2.0, boundaryZ);
        Assert.Contains(5.0, boundaryZ);
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
    public void SeamGraph_HasExcessiveNearBoundaryFragmentation_UsesSharedThresholds()
    {
        var seamLoop = new double[60 * 2];
        var graph = new SeamGraph
        {
            SeamLoopXy = seamLoop,
            PatchBoundaryEdgesNearSeam = 0,
            TerrainBoundaryEdgesNearSeam = 0,
            PatchMatchedSegments = 20,
            TerrainMatchedSegments = 60,
            PatchBoundarySegmentsNearSeam = 100,
            TerrainBoundarySegmentsNearSeam = 10
        };

        Assert.True(graph.HasExcessiveNearBoundaryFragmentation);
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
    public void SeamValidator_ValidatePatchForStitching_ReturnsSharedBoundaryAndGraph()
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

        SeamValidationResult result = SeamValidator.ValidatePatchForStitching(
            seamLoop,
            meshVertices,
            meshFaces,
            2,
            meshVertices,
            meshFaces,
            2,
            1e-6);

        Assert.True(result.IsValid);
        Assert.NotNull(result.SeamGraph);
        Assert.True(result.SeamGraph!.PatchHasFullSegmentMatch);
        Assert.Equal(4, result.PatchBoundaryLoopXy.Length / 2);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void SeamValidator_ValidatePatchSegmentMatch_RejectsMissingSeamBreak()
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

        SeamValidationResult result = SeamValidator.ValidatePatchSegmentMatch(
            seamLoop,
            coarseVertices,
            coarseFaces,
            2,
            coarseVertices,
            coarseFaces,
            2,
            1e-6);

        Assert.False(result.IsValid);
        Assert.NotNull(result.SeamGraph);
        Assert.False(result.SeamGraph!.PatchHasFullSegmentMatch);
        Assert.Contains("patch=", result.FailureReason);
    }

    [Fact]
    public void SeamValidator_ComputeLoopDeviation_RejectsDegenerateTargetLoop()
    {
        double[] sourceLoop = { 0.0, 0.0, 1.0, 0.0 };
        double[] targetLoop = { 0.0, 0.0 };

        LoopDeviationMetrics metrics = SeamValidator.ComputeLoopDeviation(sourceLoop, targetLoop, 1e-6);

        Assert.Equal(2, metrics.MissCount);
        Assert.Equal(double.MaxValue, metrics.MaxDistance);
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
