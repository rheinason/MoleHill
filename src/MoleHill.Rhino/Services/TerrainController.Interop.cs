// TerrainController access points for other services: copies or read-only views of the final terrain mesh.
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
