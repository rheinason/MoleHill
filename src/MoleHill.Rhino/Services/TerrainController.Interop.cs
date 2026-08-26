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
}
