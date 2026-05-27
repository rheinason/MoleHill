using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Services;
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

    [Theory]
    [InlineData("final:modifier:2:GradePadModifierDefinition:a:topology:Pad", 2)]
    [InlineData("preview:modifier:7:GradePathModifierDefinition:b", 7)]
    public void TryParseModifierIndex_ParsesModifierIndex(string stageKey, int expected)
    {
        bool parsed = TerrainRuntimeCache.TryParseModifierIndex(stageKey, out int modifierIndex);

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
