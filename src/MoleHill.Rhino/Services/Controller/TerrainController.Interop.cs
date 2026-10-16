// TerrainController access points for other services: copies or read-only views of the final terrain mesh.
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
