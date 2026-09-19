using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The wall plan cache exists because the wall *stage* fingerprint includes the upstream mesh, so an
/// upstream Z edit misses the stage and used to re-plan rails that had not changed. These tests cover
/// the cache's lifecycle contract: it survives the worker copy and the merge back, and it is pruned with
/// the stage it belongs to. Whether a plan is actually reused is exercised by the wall stage itself,
/// which needs the native runtime.
/// </summary>
public class TerrainRetainingWallPlanCacheTests
{
    private const string PreviewStageKey = "preview:modifier:1:RetainingWallModifierDefinition:a";
    private const string FinalStageKey = "final:modifier:1:RetainingWallModifierDefinition:a";

    private static RetainingWallPlanCacheEntry CreateEntry(ulong fingerprint = 42UL) => new()
    {
        Fingerprint = fingerprint,
        BuiltSolids = false,
        Plan = new RetainingWallPlannerCore.PlanResult(
            Array.Empty<RetainingWallPlannerCore.PlannedWall>(),
            Array.Empty<Line>(),
            Array.Empty<RetainingWallPlannerCore.ReportEntry>(),
            RetainingWallPlannerCore.PlanTiming.Empty)
    };

    [Fact]
    public void CreateWorkerCopy_CarriesPlanEntriesToTheWorker()
    {
        var cache = new TerrainRuntimeCache();
        cache.RetainingWallPlanEntries[PreviewStageKey] = CreateEntry();

        TerrainRuntimeCache worker = cache.CreateWorkerCopy();

        Assert.True(worker.RetainingWallPlanEntries.TryGetValue(PreviewStageKey, out var entry));
        Assert.Equal(42UL, entry!.Fingerprint);
    }

    [Fact]
    public void ReplaceBuildCachesFrom_TakesTheWorkersPlanEntriesAndDropsStaleOnes()
    {
        var main = new TerrainRuntimeCache();
        main.RetainingWallPlanEntries["preview:modifier:9:stale"] = CreateEntry(1UL);

        var worker = new TerrainRuntimeCache();
        worker.RetainingWallPlanEntries[PreviewStageKey] = CreateEntry(7UL);

        main.ReplaceBuildCachesFrom(worker);

        Assert.Equal(7UL, Assert.Contains(PreviewStageKey, main.RetainingWallPlanEntries).Fingerprint);
        Assert.DoesNotContain("preview:modifier:9:stale", main.RetainingWallPlanEntries);
        Assert.Empty(worker.RetainingWallPlanEntries);
    }

    [Fact]
    public void PruneUnused_KeepsTheUsedStageAndDropsTheRest()
    {
        var cache = new TerrainRuntimeCache();
        cache.RetainingWallPlanEntries[PreviewStageKey] = CreateEntry();
        cache.RetainingWallPlanEntries["preview:modifier:2:RetainingWallModifierDefinition:b"] = CreateEntry();
        cache.RetainingWallPlanEntries[FinalStageKey] = CreateEntry();

        cache.PruneUnused(new HashSet<string>(StringComparer.Ordinal) { PreviewStageKey }, TerrainBuildMode.Preview);

        Assert.Contains(PreviewStageKey, cache.RetainingWallPlanEntries);
        Assert.DoesNotContain("preview:modifier:2:RetainingWallModifierDefinition:b", cache.RetainingWallPlanEntries);
        // A different build mode owns its own prefix and must be left alone.
        Assert.Contains(FinalStageKey, cache.RetainingWallPlanEntries);
    }

    [Fact]
    public void DetachMeshOutputs_ClearsThePlanEntriesWithEverythingElse()
    {
        var cache = new TerrainRuntimeCache();
        cache.RetainingWallPlanEntries[PreviewStageKey] = CreateEntry();

        cache.DetachMeshOutputs();

        Assert.Empty(cache.RetainingWallPlanEntries);
    }
}
