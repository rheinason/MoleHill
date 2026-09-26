using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    private static RhinoMesh? RestoreCachedMeshStage(TerrainBuildResult build, StageCacheEntry cachedEntry, out ulong outputFingerprint)
    {
        RestoreCachedDiagnostics(build, cachedEntry);
        build.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.AuxiliaryObjects));
        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(cachedEntry.PersistentHardConstraints));
        build.PersistentElevationConstraints.Clear();
        build.PersistentElevationConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(cachedEntry.PersistentElevationConstraints));
        outputFingerprint = cachedEntry.OutputFingerprint;
        // StoreMeshStageCache normalizes before caching (while preserving already-finalized Retopo
        // quads). DuplicateMesh preserves that topology, so normalizing again on every hot-cache
        // restore is redundant O(vertices + faces) work.
        return TerrainRuntimeCacheCloner.CloneMesh(cachedEntry.MeshOutput);
    }

    private static void RestoreCachedDiagnostics(TerrainBuildResult build, StageCacheEntry cachedEntry)
    {
        build.Diagnostics.AddRange(cachedEntry.Diagnostics.Where(static line => !IsCachedTimingDiagnostic(line)));
        build.StructuredDiagnostics.AddRange(cachedEntry.StructuredDiagnostics.Where(
            static diagnostic => !diagnostic.Code.StartsWith("timing.", StringComparison.OrdinalIgnoreCase)));
        build.RuntimeOverlays.AddRange(TerrainRuntimeCacheCloner.CloneRuntimeOverlays(cachedEntry.RuntimeOverlays));
    }

    internal static bool IsCachedTimingDiagnostic(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.StartsWith("timing.", StringComparison.OrdinalIgnoreCase) ||
               line.Contains(" remesh timing", StringComparison.OrdinalIgnoreCase) ||
               line.Contains(" planner timing:", StringComparison.OrdinalIgnoreCase);
    }

    private static RhinoMesh? StoreMeshStageCache(
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        string stageName,
        ulong preResolutionFingerprint,
        ulong resolvedInputFingerprint,
        RhinoMesh? mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints,
        IEnumerable<GeneratedRhinoObject> auxiliaryObjects,
        IEnumerable<string> diagnostics,
        string? detail,
        Stopwatch timer,
        out ulong outputFingerprint,
        Func<bool>? shouldCancel = null,
        IEnumerable<GradingDiagnostic>? structuredDiagnostics = null,
        TerrainBuildProgressReporter? reportProgress = null,
        IEnumerable<RuntimeOverlayItem>? runtimeOverlays = null)
    {
        timer.Stop();
        ThrowIfCancellationRequested(shouldCancel);
        reportProgress?.Start("Stage mesh normalization");
        mesh = NormalizeTerrainMesh(mesh);
        reportProgress?.Complete(
            "Stage mesh normalization",
            mesh == null ? "no mesh" : $"{mesh.Vertices.Count:N0} vertices, {mesh.Faces.Count:N0} faces");
        outputFingerprint = ComputeMeshStageOutputFingerprint(
            mesh,
            CombineConstraints(persistentHardConstraints, build.PersistentElevationConstraints));
        reportProgress?.Start("Stage cache clone");
        RhinoMesh? cachedMesh = TerrainRuntimeCacheCloner.CloneMesh(mesh);
        reportProgress?.Complete(
            "Stage cache clone",
            cachedMesh == null ? "no mesh" : $"{cachedMesh.Vertices.Count:N0} vertices, {cachedMesh.Faces.Count:N0} faces");
        runtimeCache.StageEntries[stageKey] = new StageCacheEntry
        {
            StageName = stageName,
            PreResolutionFingerprint = preResolutionFingerprint,
            ResolvedInputFingerprint = resolvedInputFingerprint,
            OutputFingerprint = outputFingerprint,
            MeshOutput = cachedMesh,
            AuxiliaryObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(auxiliaryObjects),
            PersistentHardConstraints = TerrainRuntimeCacheCloner.CloneConstraints(persistentHardConstraints),
            PersistentElevationConstraints = TerrainRuntimeCacheCloner.CloneConstraints(build.PersistentElevationConstraints),
            Diagnostics = diagnostics.ToList(),
            StructuredDiagnostics = structuredDiagnostics?.ToList() ?? new List<GradingDiagnostic>(),
            RuntimeOverlays = TerrainRuntimeCacheCloner.CloneRuntimeOverlays(runtimeOverlays ?? Array.Empty<RuntimeOverlayItem>())
        };

        build.RecordTiming(stageName, timer.Elapsed, detail);
        return mesh;
    }

    private static RhinoMesh? NormalizeTerrainMesh(RhinoMesh? mesh)
    {
        if (mesh == null || mesh.Faces.Count == 0)
            return mesh;

        if (RhinoGeometryConversions.IsNormalizedMesh(mesh))
            return mesh;

        RhinoGeometryConversions.NormalizeMeshInPlace(mesh);
        return mesh;
    }

    private static StageCacheEntry CloneStageCacheEntry(
        StageCacheEntry source,
        ulong preResolutionFingerprint,
        ulong resolvedInputFingerprint)
    {
        return new StageCacheEntry
        {
            StageName = source.StageName,
            PreResolutionFingerprint = preResolutionFingerprint,
            ResolvedInputFingerprint = resolvedInputFingerprint,
            OutputFingerprint = source.OutputFingerprint,
            MeshOutput = source.MeshOutput,
            AnalysisOutput = TerrainRuntimeCacheCloner.CloneAnalyses(source.AnalysisOutput),
            ZoneObjects = source.ZoneObjects,
            TerrainRegions = source.TerrainRegions.Select(region => region.Duplicate()).ToList(),
            ZoneAnalysisOutput = TerrainRuntimeCacheCloner.CloneZoneAnalyses(source.ZoneAnalysisOutput),
            AuxiliaryObjects = source.AuxiliaryObjects,
            MarkerObjects = source.MarkerObjects,
            PersistentHardConstraints = source.PersistentHardConstraints,
            PersistentElevationConstraints = source.PersistentElevationConstraints,
            Diagnostics = source.Diagnostics,
            StructuredDiagnostics = source.StructuredDiagnostics,
            RuntimeOverlays = TerrainRuntimeCacheCloner.CloneRuntimeOverlays(source.RuntimeOverlays),
            StairSurfaceCount = source.StairSurfaceCount,
            StairTreadDepthSummary = source.StairTreadDepthSummary,
            StairStepCountSummary = source.StairStepCountSummary
        };
    }
}
