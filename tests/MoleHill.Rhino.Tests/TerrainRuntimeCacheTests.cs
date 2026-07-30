using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainRuntimeCacheTests
{
    [Fact]
    public void FindIntersectingGradingStageKeys_ReturnsOnlyLaterOverlappingStages()
    {
        var cache = new TerrainRuntimeCache();
        cache.GradingTopologyEntries["final:modifier:2:GradePadModifierDefinition:a:topology:Pad"] = CreateEntry(
            "Pad",
            CreatePatch("pad:2", 2.0, 2.0, 4.0, 4.0));
        cache.GradingTopologyEntries["final:modifier:3:GradePadModifierDefinition:b:topology:Pad"] = CreateEntry(
            "Pad",
            CreatePatch("pad:3", 10.0, 10.0, 12.0, 12.0));
        cache.GradingTopologyEntries["final:modifier:0:GradePadModifierDefinition:c:topology:Pad"] = CreateEntry(
            "Pad",
            CreatePatch("pad:0", 1.0, 1.0, 3.0, 3.0));

        var candidates = new[]
        {
            CreatePatch("pad:new", 1.5, 1.5, 3.5, 3.5)
        };

        List<string> stageKeys = cache.FindIntersectingGradingStageKeys("final:", 1, candidates);

        Assert.Single(stageKeys);
        Assert.Equal("final:modifier:2:GradePadModifierDefinition:a", stageKeys[0]);
    }

    [Fact]
    public void FindIntersectingGradingStageKeys_ReturnsTransitiveConnectedStages()
    {
        var cache = new TerrainRuntimeCache();
        cache.GradingTopologyEntries["final:modifier:2:GradePadModifierDefinition:a:topology:Pad"] = CreateEntry(
            "Pad",
            CreatePatch("pad:2", 2.0, 2.0, 4.0, 4.0));
        cache.GradingTopologyEntries["final:modifier:3:GradePadModifierDefinition:b:topology:Pad"] = CreateEntry(
            "Pad",
            CreatePatch("pad:3", 3.5, 3.5, 5.5, 5.5));
        cache.GradingTopologyEntries["final:modifier:4:GradePadModifierDefinition:c:topology:Pad"] = CreateEntry(
            "Pad",
            CreatePatch("pad:4", 5.0, 5.0, 7.0, 7.0));

        var candidates = new[]
        {
            CreatePatch("pad:new", 1.5, 1.5, 3.0, 3.0)
        };

        List<string> stageKeys = cache.FindIntersectingGradingStageKeys("final:", 1, candidates);

        Assert.Equal(
            new[]
            {
                "final:modifier:2:GradePadModifierDefinition:a",
                "final:modifier:3:GradePadModifierDefinition:b",
                "final:modifier:4:GradePadModifierDefinition:c"
            },
            stageKeys.OrderBy(static key => key).ToArray());
    }

    [Fact]
    public void InvalidateStages_RemovesStageAndAssociatedTopologyEntries()
    {
        var cache = new TerrainRuntimeCache();
        cache.StageEntries["final:modifier:2:GradePadModifierDefinition:a"] = new StageCacheEntry();
        cache.GradingTopologyEntries["final:modifier:2:GradePadModifierDefinition:a:topology:Pad"] = CreateEntry(
            "Pad",
            CreatePatch("pad:2", 2.0, 2.0, 4.0, 4.0));

        cache.InvalidateStages(new[] { "final:modifier:2:GradePadModifierDefinition:a" });

        Assert.Empty(cache.StageEntries);
        Assert.Empty(cache.GradingTopologyEntries);
    }

    [Fact]
    public void PruneUnused_RemovesUnusedFinalModifierStagesAndAssociatedCaches()
    {
        var cache = new TerrainRuntimeCache();
        cache.StageEntries["final:modifier:1:RetainingWallModifierDefinition:wall"] = new StageCacheEntry();
        cache.StageEntries["final:modifier:2:GradePadModifierDefinition:pad"] = new StageCacheEntry();
        cache.GradingTopologyEntries["final:modifier:2:GradePadModifierDefinition:pad:topology:Pad"] = CreateEntry(
            "Pad",
            CreatePatch("pad:2", 2.0, 2.0, 4.0, 4.0));

        cache.PruneUnused(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "final:modifier:2:GradePadModifierDefinition:pad",
                "final:modifier:2:GradePadModifierDefinition:pad:topology:Pad"
            },
            TerrainBuildMode.Final);

        Assert.DoesNotContain("final:modifier:1:RetainingWallModifierDefinition:wall", cache.StageEntries.Keys);
        Assert.Contains("final:modifier:2:GradePadModifierDefinition:pad", cache.StageEntries.Keys);
        Assert.Contains("final:modifier:2:GradePadModifierDefinition:pad:topology:Pad", cache.GradingTopologyEntries.Keys);
    }

    [Fact]
    public void PruneUnused_PreservesOtherBuildModeCaches()
    {
        var cache = new TerrainRuntimeCache();
        cache.StageEntries["preview:modifier:1:RetainingWallModifierDefinition:wall"] = new StageCacheEntry();
        cache.StageEntries["final:modifier:1:RetainingWallModifierDefinition:wall"] = new StageCacheEntry();

        cache.PruneUnused(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "final:modifier:1:RetainingWallModifierDefinition:wall"
            },
            TerrainBuildMode.Final);

        Assert.Contains("preview:modifier:1:RetainingWallModifierDefinition:wall", cache.StageEntries.Keys);
        Assert.Contains("final:modifier:1:RetainingWallModifierDefinition:wall", cache.StageEntries.Keys);
    }

    [RhinoNativeFact]
    public void ReplaceBuildCachesFrom_ReturnsOnlyDisplacedMeshes()
    {
        var retainedMesh = new Mesh();
        var displacedMesh = new Mesh();
        var incomingMesh = new Mesh();
        try
        {
            var cache = new TerrainRuntimeCache();
            cache.StageEntries["retained"] = new StageCacheEntry { MeshOutput = retainedMesh };
            cache.StageEntries["old"] = new StageCacheEntry { MeshOutput = displacedMesh };

            var source = new TerrainRuntimeCache();
            source.StageEntries["retained"] = new StageCacheEntry { MeshOutput = retainedMesh };
            source.StageEntries["new"] = new StageCacheEntry { MeshOutput = incomingMesh };

            List<Mesh> displacedMeshes = cache.ReplaceBuildCachesFrom(source);

            Assert.Same(displacedMesh, Assert.Single(displacedMeshes));
            Assert.Same(retainedMesh, cache.StageEntries["retained"].MeshOutput);
            Assert.Same(incomingMesh, cache.StageEntries["new"].MeshOutput);
            Assert.Empty(source.StageEntries);
        }
        finally
        {
            retainedMesh.Dispose();
            displacedMesh.Dispose();
            incomingMesh.Dispose();
        }
    }

    [Fact]
    public void CloneStageCacheEntry_PreservesStructuredDiagnostics()
    {
        var entry = new StageCacheEntry
        {
            Diagnostics = new List<string> { "legacy message" },
            StructuredDiagnostics = new List<GradingDiagnostic>
            {
                GradingDiagnostic.Warning("grade_pad.test", "structured message", "grade_pad", targetIndex: 2)
            }
        };

        StageCacheEntry clone = TerrainRuntimeCacheCloner.CloneStageCacheEntry(entry);

        Assert.Equal("legacy message", Assert.Single(clone.Diagnostics));
        GradingDiagnostic diagnostic = Assert.Single(clone.StructuredDiagnostics);
        Assert.Equal("grade_pad.test", diagnostic.Code);
        Assert.Equal(GradingDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("grade_pad", diagnostic.Operation);
        Assert.Equal(2, diagnostic.TargetIndex);
    }

    [Fact]
    public void CloneGradingTopologyEntry_PreservesStructuredDiagnostics()
    {
        GradingTopologyCacheEntry entry = CreateEntry(
            "Pad",
            CreatePatch("pad:2", 2.0, 2.0, 4.0, 4.0));
        entry.StructuredDiagnostics.Add(
            GradingDiagnostic.Information("grade_pad.topology", "topology message", "grade_pad", targetIndex: 2));

        GradingTopologyCacheEntry clone = TerrainRuntimeCacheCloner.CloneGradingTopologyEntry(entry);

        GradingDiagnostic diagnostic = Assert.Single(clone.StructuredDiagnostics);
        Assert.Equal("grade_pad.topology", diagnostic.Code);
        Assert.Equal(GradingDiagnosticSeverity.Information, diagnostic.Severity);
        Assert.Equal("grade_pad", diagnostic.Operation);
        Assert.Equal(2, diagnostic.TargetIndex);
    }

    [Fact]
    public void BuildPathTopologySummary_DiscardsGeometryButPreservesSummary()
    {
        double[] vertices =
        {
            0.0, 0.0, 1.0,
            2.0, 0.0, 2.0,
            0.0, 2.0, 3.0
        };
        int[] faces = { 0, 1, 2, 2 };
        GradingPatch patch = CreatePatch("path:0", -1.0, -1.0, 3.0, 3.0);

        GradingTopologyCacheEntry entry = TerrainBuildService.BuildPathTopologySummary(
            vertices,
            3,
            faces,
            1,
            gradingResult: null,
            new[] { patch },
            new[] { "path diagnostic" });

        Assert.Equal("Path", entry.GraderKind);
        Assert.Empty(entry.Vertices);
        Assert.Equal(3, entry.VertexCount);
        Assert.Empty(entry.Faces);
        Assert.Equal(1, entry.FaceCount);
        Assert.NotEqual(0UL, entry.OutputFingerprint);
        Assert.NotSame(patch, Assert.Single(entry.PatchSummaries));
        Assert.Equal("path diagnostic", Assert.Single(entry.Diagnostics));
    }

    [Fact]
    public void BuildPathTopologySummary_FingerprintsGeometryBeforeDiscardingIt()
    {
        double[] vertices =
        {
            0.0, 0.0, 1.0,
            2.0, 0.0, 2.0,
            0.0, 2.0, 3.0
        };
        int[] faces = { 0, 1, 2, 2 };

        GradingTopologyCacheEntry original = TerrainBuildService.BuildPathTopologySummary(
            vertices,
            3,
            faces,
            1,
            gradingResult: null,
            Array.Empty<GradingPatch>(),
            Array.Empty<string>());
        vertices[2] = 4.0;
        GradingTopologyCacheEntry changed = TerrainBuildService.BuildPathTopologySummary(
            vertices,
            3,
            faces,
            1,
            gradingResult: null,
            Array.Empty<GradingPatch>(),
            Array.Empty<string>());

        Assert.NotEqual(original.OutputFingerprint, changed.OutputFingerprint);
        Assert.Empty(original.Vertices);
        Assert.Empty(changed.Vertices);
    }

    [Theory]
    [InlineData("final:modifier:2:GradePadModifierDefinition:a:topology:Pad", 2)]
    [InlineData("preview:modifier:7:GradePathModifierDefinition:b", 7)]
    public void TryParseModifierIndex_ParsesModifierIndex(string stageKey, int expected)
    {
        bool parsed = TerrainStageKey.TryParseModifierIndex(stageKey, out int modifierIndex);

        Assert.True(parsed);
        Assert.Equal(expected, modifierIndex);
    }

    private static GradingTopologyCacheEntry CreateEntry(string graderKind, params GradingPatch[] patches)
    {
        return new GradingTopologyCacheEntry
        {
            GraderKind = graderKind,
            Fingerprint = 1,
            OutputFingerprint = 2,
            Vertices = Array.Empty<double>(),
            VertexCount = 0,
            Faces = Array.Empty<int>(),
            FaceCount = 0,
            PatchSummaries = patches.ToList(),
            Diagnostics = new List<string>()
        };
    }

    private static GradingPatch CreatePatch(string ownerKey, double minX, double minY, double maxX, double maxY)
    {
        double[] loop =
        {
            minX, minY,
            maxX, minY,
            maxX, maxY,
            minX, maxY
        };

        return new GradingPatch
        {
            OwnerKey = ownerKey,
            Kind = GradingPatchKind.Pad,
            Priority = 0.0,
            OwnedRegionLoopXy = loop,
            DaylightLoopXy = Array.Empty<double>(),
            StitchLoopXy = Array.Empty<double>(),
            DirtyBounds = new Bounds2D(minX, maxX, minY, maxY),
            UsesFallbackBand = false
        };
    }
}
