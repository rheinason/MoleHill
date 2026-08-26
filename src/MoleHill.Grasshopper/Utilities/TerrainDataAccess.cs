// Converts generic Grasshopper inputs back into MoleHill terrain payloads.
using Grasshopper.Kernel.Types;
using MoleHill.Grasshopper.Types;
using MoleHill.Shared;
using Rhino;
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
                terrain = data;
                return true;
            case MoleHillTerrainGoo goo when goo.Value != null:
                terrain = goo.Value;
                return true;
            case GH_ObjectWrapper wrapper:
                return TryGetTerrain(wrapper.Value, out terrain);
            case GH_Mesh meshGoo when meshGoo.Value != null:
                terrain = CreateFromMesh(meshGoo.Value);
                return true;
            case Mesh mesh:
                terrain = CreateFromMesh(mesh);
                return true;
            case IGH_Goo genericGoo:
                object? value = genericGoo.ScriptVariable();
                return !ReferenceEquals(value, source) && TryGetTerrain(value, out terrain);
            default:
                return false;
        }
    }

    private static MoleHillTerrainData CreateFromMesh(Mesh mesh)
    {
        ModelUnitContext units = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        return new MoleHillTerrainData(
            mesh,
            unitSystem: units.IsSupported ? units.UnitSystem.ToString() : "Unspecified",
            metersPerModelUnit: units.IsSupported ? units.MetersPerModelUnit : 1.0);
    }
}
