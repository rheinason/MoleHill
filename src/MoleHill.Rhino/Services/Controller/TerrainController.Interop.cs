// TerrainController access points for other services: copies or read-only views of the final terrain mesh.
using MoleHill.Core.Engine;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainController
{
    public Mesh? DuplicateFinalTerrainMesh(RhinoDoc doc, Guid terrainId)
    {
        TerrainRuntimeCache cache = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        TerrainDisplayState? state = cache.DisplayState;
        if (state == null || state.IsPreview || state.HasDeferredOutputs || state.TerrainMesh == null)
            return null;
        return state.TerrainMesh.DuplicateMesh();
    }

    /// <summary>
    /// A copy of the mesh the given modifier received in the last build — the ground it graded, not what it
    /// left behind. Null when the terrain has not been built or the modifier is not in its stack.
    /// </summary>
    public Mesh? DuplicateIncomingModifierMesh(RhinoDoc doc, TerrainDefinition terrain, Guid modifierId)
    {
        int modifierIndex = terrain.Modifiers.FindIndex(modifier => modifier.Id == modifierId);
        if (modifierIndex < 0)
            return null;
        TerrainRuntimeCache cache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId);
        if (cache.DisplayState == null || cache.DisplayState.HasDeferredOutputs)
            return null;
        return FindIncomingModifierMesh(cache, terrain, modifierIndex)?.DuplicateMesh();
    }

    /// <summary>
    /// Copies of the breaklines the given modifier received in the last build, which a grading card turns
    /// into lock barriers (unless it grades through them). Empty when the terrain has not been built, the
    /// modifier is not in its stack, or it took the base terrain with none.
    /// </summary>
    public List<ConstraintPolyline> CopyIncomingModifierBreaklines(RhinoDoc doc, TerrainDefinition terrain, Guid modifierId)
    {
        int modifierIndex = terrain.Modifiers.FindIndex(modifier => modifier.Id == modifierId);
        if (modifierIndex < 0)
            return new List<ConstraintPolyline>();
        TerrainRuntimeCache cache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId);
        if (cache.DisplayState == null || cache.DisplayState.HasDeferredOutputs)
            return new List<ConstraintPolyline>();
        StageCacheEntry? entry = FindIncomingModifierStage(cache, terrain, modifierIndex);
        return entry == null
            ? new List<ConstraintPolyline>()
            : TerrainRuntimeCacheCloner.CloneConstraints(entry.PersistentHardConstraints);
    }

    /// <summary>
    /// Read-only view of the completed final terrain mesh for callers that only sample it (no copy).
    /// Never mutate or store the returned mesh — it is the live cached instance.
    /// </summary>
    public Mesh? PeekFinalTerrainMesh(RhinoDoc doc, Guid terrainId)
    {
        TerrainRuntimeCache cache = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        TerrainDisplayState? state = cache.DisplayState;
        if (state == null || state.IsPreview || state.HasDeferredOutputs)
            return null;
        return state.TerrainMesh;
    }
}
