using System.Diagnostics;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
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
        // StoreMeshStageCache normalizes before caching. DuplicateMesh preserves that topology, so
        // normalizing again on every hot-cache restore is redundant O(vertices + faces) work.
        return TerrainRuntimeCacheCloner.CloneMesh(cachedEntry.MeshOutput);
    }

    private static void RestoreCachedDiagnostics(TerrainBuildResult build, StageCacheEntry cachedEntry)
    {
        build.Diagnostics.AddRange(cachedEntry.Diagnostics.Where(static line => !IsCachedTimingDiagnostic(line)));
        build.StructuredDiagnostics.AddRange(cachedEntry.StructuredDiagnostics.Where(
            static diagnostic => !diagnostic.Code.StartsWith("timing.", StringComparison.OrdinalIgnoreCase)));
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
        IEnumerable<GradingDiagnostic>? structuredDiagnostics = null)
    {
        timer.Stop();
        ThrowIfCancellationRequested(shouldCancel);
        mesh = NormalizeTerrainMesh(mesh);
        outputFingerprint = ComputeMeshStageOutputFingerprint(
            mesh,
            CombineConstraints(persistentHardConstraints, build.PersistentElevationConstraints));
        runtimeCache.StageEntries[stageKey] = new StageCacheEntry
        {
            StageName = stageName,
            PreResolutionFingerprint = preResolutionFingerprint,
            ResolvedInputFingerprint = resolvedInputFingerprint,
            OutputFingerprint = outputFingerprint,
            MeshOutput = TerrainRuntimeCacheCloner.CloneMesh(mesh),
            AuxiliaryObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(auxiliaryObjects),
            PersistentHardConstraints = TerrainRuntimeCacheCloner.CloneConstraints(persistentHardConstraints),
            PersistentElevationConstraints = TerrainRuntimeCacheCloner.CloneConstraints(build.PersistentElevationConstraints),
            Diagnostics = diagnostics.ToList(),
            StructuredDiagnostics = structuredDiagnostics?.ToList() ?? new List<GradingDiagnostic>()
        };

        build.RecordTiming(stageName, timer.Elapsed, detail);
        return mesh;
    }

    private static RhinoMesh? NormalizeTerrainMesh(RhinoMesh? mesh)
    {
        if (mesh == null || mesh.Faces.Count == 0)
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
            AuxiliaryObjects = source.AuxiliaryObjects,
            MarkerObjects = source.MarkerObjects,
            PersistentHardConstraints = source.PersistentHardConstraints,
            PersistentElevationConstraints = source.PersistentElevationConstraints,
            Diagnostics = source.Diagnostics,
            StructuredDiagnostics = source.StructuredDiagnostics,
            StairSurfaceCount = source.StairSurfaceCount,
            StairTreadDepthSummary = source.StairTreadDepthSummary,
            StairStepCountSummary = source.StairStepCountSummary
        };
    }
}
