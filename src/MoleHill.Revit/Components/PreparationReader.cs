// Reads Prepare Toposolid packages (through the MoleHill.Interop contract) into planner inputs.
using Grasshopper.Kernel.Types;
using MoleHill.Interop;
using MoleHill.Revit.Planning;
using Rhino.Geometry;

namespace MoleHill.Revit.Components;

internal static class PreparationReader
{
    public static bool TryRead(object? value, out PreparationInput? input)
    {
        if (value is IGH_Goo goo)
            value = goo.ScriptVariable();
        if (value is not IToposolidPreparation preparation)
        {
            input = null;
            return false;
        }

        input = new PreparationInput(
            preparation.Key,
            preparation.Name,
            preparation.GeometryFingerprint,
            preparation.MetersPerModelUnit,
            preparation.Profiles.Select(ToPolyline).ToArray(),
            preparation.ElevationPoints.ToArray(),
            preparation.SubdivisionProfiles
                .Select(subdivision => new SubdivisionInput(
                    subdivision.Key,
                    subdivision.Name,
                    subdivision.GeometryFingerprint,
                    subdivision.Profiles.Select(ToPolyline).ToArray()))
                .ToArray());
        return true;
    }

    private static Point3d[]? ToPolyline(Curve curve) =>
        curve.TryGetPolyline(out Polyline polyline) ? polyline.ToArray() : null;
}
