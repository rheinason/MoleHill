using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Runtime caches and cross-terrain wiring: the per-terrain runtime cache, the build snapshot a worker
// reads, and scheduling or unlinking the terrains that depend on another.
internal sealed partial class TerrainController
{
    private TerrainRuntimeCache GetRuntimeCache(uint docSerial, Guid terrainId)
    {
        if (_runtimeCaches.TryGetValue((docSerial, terrainId), out var cache))
            return cache;

        cache = new TerrainRuntimeCache();
        _runtimeCaches[(docSerial, terrainId)] = cache;
        return cache;
    }

    private TerrainBuildSnapshot CreateBuildSnapshot(
        RhinoDoc doc,
        TerrainDefinition terrain,
        bool includeSectionTerrains = true)
    {
        var references = new List<TerrainSectionReferenceSnapshot>();
        DocumentState state = GetState(doc);
        IEnumerable<Guid> sectionReferencedIds = includeSectionTerrains
            ? terrain.Annotations
                .OfType<TerrainSectionAnnotationDefinitionBase>()
                .Where(analysis => analysis.IsEnabled)
                .SelectMany(analysis => analysis.ComparisonTerrainIds)
            : Array.Empty<Guid>();
        IEnumerable<Guid> analysisReferencedIds = includeSectionTerrains
            ? terrain.Analyses
                .OfType<ReferenceComparisonAnalysisDefinition>()
                .Where(analysis => analysis.IsEnabled && analysis.ReferenceTerrainId.HasValue)
                .Select(analysis => analysis.ReferenceTerrainId!.Value)
            : Array.Empty<Guid>();
        IEnumerable<Guid> projectToReferencedIds = terrain.Modifiers
            .OfType<ProjectToModifierDefinition>()
            .Where(modifier => modifier.IsEnabled && !modifier.TargetMesh.HasReferences && modifier.TargetTerrainId.HasValue)
            .Select(modifier => modifier.TargetTerrainId!.Value);
        IEnumerable<Guid> referencedIds = sectionReferencedIds
            .Concat(analysisReferencedIds)
            .Concat(projectToReferencedIds)
            .Where(id => id != Guid.Empty && id != terrain.TerrainId)
            .Distinct();

        var fingerprints = new Dictionary<Guid, ulong>();
        foreach (Guid terrainId in referencedIds)
        {
            fingerprints[terrainId] = HasCompletedFinalTerrainMesh(doc, terrainId) &&
                _runtimeCaches.TryGetValue((doc.RuntimeSerialNumber, terrainId), out TerrainRuntimeCache? referencedCache)
                    ? referencedCache.LastFinalMeshFingerprint
                    : 0UL;

            TerrainDefinition? referencedTerrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
            if (referencedTerrain == null ||
                !_runtimeCaches.TryGetValue((doc.RuntimeSerialNumber, terrainId), out TerrainRuntimeCache? runtimeCache) ||
                runtimeCache.DisplayState is not { IsPreview: false, TerrainMesh: { } finalMesh })
                continue;

            Mesh mesh = finalMesh.DuplicateMesh();
            references.Add(new TerrainSectionReferenceSnapshot
            {
                TerrainId = terrainId,
                Name = referencedTerrain.Name,
                ColorArgb = referencedTerrain.TerrainColorArgb,
                Mesh = mesh,
                MeshFingerprint = TerrainBuildService.ComputeMeshFingerprintForDiagnostics(mesh)
            });
        }

        TerrainBuildSnapshot snapshot = TerrainBuildSnapshotBuilder.Create(doc, terrain, references);
        foreach ((Guid terrainId, ulong fingerprint) in fingerprints)
            snapshot.ReferencedTerrainFingerprints[terrainId] = fingerprint;
        return snapshot;
    }

    /// <summary>True when an enabled cut/fill, earthworks or section card on another terrain compares
    /// against this one.</summary>
    private static bool IsComparedAgainst(DocumentState state, Guid terrainId) =>
        state.Terrains.Any(other => other.TerrainId != terrainId && (
            other.Annotations
                .OfType<TerrainSectionAnnotationDefinitionBase>()
                .Any(section => section.IsEnabled && section.ComparisonTerrainIds.Contains(terrainId))
            || other.Analyses
                .OfType<ReferenceComparisonAnalysisDefinition>()
                .Any(analysis => analysis.IsEnabled && analysis.ReferenceTerrainId == terrainId)));

    private void ScheduleTerrainDependents(RhinoDoc doc, DocumentState state, Guid referencedTerrainId)
    {
        foreach (TerrainDefinition dependent in state.Terrains)
        {
            if (dependent.TerrainId == referencedTerrainId || !dependent.LiveUpdateEnabled)
                continue;
            bool referencesTerrain = dependent.Annotations
                .OfType<TerrainSectionAnnotationDefinitionBase>()
                .Any(analysis => analysis.IsEnabled && analysis.ComparisonTerrainIds.Contains(referencedTerrainId));
            bool referencesTerrainFromAnalysis = dependent.Analyses
                .OfType<ReferenceComparisonAnalysisDefinition>()
                .Any(analysis => analysis.IsEnabled && analysis.ReferenceTerrainId == referencedTerrainId);
            bool referencesTerrainFromProjectTo = dependent.Modifiers
                .OfType<ProjectToModifierDefinition>()
                .Any(modifier => modifier.IsEnabled && !modifier.TargetMesh.HasReferences &&
                                 modifier.TargetTerrainId == referencedTerrainId);
            bool projectionCycle = referencesTerrainFromProjectTo &&
                                   TerrainProjectionDependsOn(state, referencedTerrainId, dependent.TerrainId, new HashSet<Guid>());
            if (referencesTerrain || referencesTerrainFromAnalysis ||
                (referencesTerrainFromProjectTo && !projectionCycle))
                ScheduleRebuild(doc, dependent.TerrainId, notify: false);
        }
    }

    private static bool TerrainProjectionDependsOn(
        DocumentState state,
        Guid terrainId,
        Guid soughtTerrainId,
        HashSet<Guid> visited)
    {
        if (terrainId == soughtTerrainId)
            return true;
        if (!visited.Add(terrainId))
            return false;

        TerrainDefinition? terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return false;

        foreach (Guid targetId in terrain.Modifiers
                     .OfType<ProjectToModifierDefinition>()
                     .Where(modifier => modifier.IsEnabled && !modifier.TargetMesh.HasReferences && modifier.TargetTerrainId.HasValue)
                     .Select(modifier => modifier.TargetTerrainId!.Value))
        {
            if (TerrainProjectionDependsOn(state, targetId, soughtTerrainId, visited))
                return true;
        }

        return false;
    }

    private void RemoveTerrainReferences(RhinoDoc doc, DocumentState state, Guid removedTerrainId)
    {
        foreach (TerrainDefinition terrain in state.Terrains)
        {
            bool changed = false;
            foreach (TerrainSectionAnnotationDefinitionBase section in terrain.Annotations.OfType<TerrainSectionAnnotationDefinitionBase>())
            {
                changed |= section.ComparisonTerrainIds.RemoveAll(id => id == removedTerrainId) > 0;
                changed |= section.ProfileColorArgbs.Remove(removedTerrainId);
                if (section.CutFillReferenceTerrainId == removedTerrainId)
                {
                    section.CutFillReferenceTerrainId = null;
                    changed = true;
                }
            }

            foreach (ReferenceComparisonAnalysisDefinition analysis in terrain.Analyses.OfType<ReferenceComparisonAnalysisDefinition>())
            {
                if (analysis.ReferenceTerrainId != removedTerrainId)
                    continue;
                analysis.ReferenceTerrainId = null;
                changed = true;
            }

            foreach (ProjectToModifierDefinition modifier in terrain.Modifiers.OfType<ProjectToModifierDefinition>())
            {
                if (modifier.TargetTerrainId != removedTerrainId)
                    continue;
                modifier.TargetTerrainId = null;
                changed = true;
            }

            if (changed && terrain.LiveUpdateEnabled)
                ScheduleRebuild(doc, terrain.TerrainId, notify: false);
        }
    }

    private void RemoveRuntimeCache(uint docSerial, Guid terrainId)
    {
        TerrainRebuildState? rebuildState = null;
        if (_rebuildStates.TryGetValue((docSerial, terrainId), out var existingRebuildState))
        {
            rebuildState = existingRebuildState;
            RetireRunningWorker(rebuildState, invalidateGeneration: true);
        }

        if (_runtimeCaches.TryGetValue((docSerial, terrainId), out var cache))
        {
            List<Mesh> detachedMeshes = cache.DetachMeshOutputs();
            if (rebuildState != null)
                DisposeDisplacedCacheMeshesWhenSafe(detachedMeshes, rebuildState);
            else
                DisposeMeshes(detachedMeshes);
        }

        _runtimeCaches.Remove((docSerial, terrainId));
    }

    private void ClearRuntimeCaches(uint docSerial)
    {
        foreach (var key in _runtimeCaches.Keys.Where(key => key.docSerial == docSerial).ToList())
            RemoveRuntimeCache(key.docSerial, key.terrainId);
    }
}
