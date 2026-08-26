// Converts generic Grasshopper inputs back into MoleHill terrain payloads.
using Grasshopper.Kernel.Types;
using MoleHill.Grasshopper.Types;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Utilities;

internal static class TerrainDataAccess
{
    public static bool TryGetTerrain(object? source, out MoleHillTerrainData terrain)
    {
        terrain = null!;
        if (source == null)
            return false;

        switch (source)
        {
            case MoleHillTerrainData data:
                terrain = data.Duplicate();
                return true;
            case MoleHillTerrainGoo goo when goo.Value != null:
                terrain = goo.Value.Duplicate();
                return true;
            case GH_ObjectWrapper wrapper:
                return TryGetTerrain(wrapper.Value, out terrain);
            case GH_Mesh meshGoo when meshGoo.Value != null:
                terrain = new MoleHillTerrainData(meshGoo.Value);
                return true;
            case Mesh mesh:
                terrain = new MoleHillTerrainData(mesh);
                return true;
            case IGH_Goo genericGoo:
                object? value = genericGoo.ScriptVariable();
                return !ReferenceEquals(value, source) && TryGetTerrain(value, out terrain);
            default:
                return false;
        }
    }
}
