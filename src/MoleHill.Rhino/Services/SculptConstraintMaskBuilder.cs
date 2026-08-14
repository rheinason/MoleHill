using MoleHill.Core.Sculpting;
using MoleHill.Rhino.Model;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Converts Sculpt source curves into the shared Core protection mask. Selected source
/// curves belonging to earlier Grade Path modifiers are expanded to their configured design width;
/// all other closed/open curves become protected polygons/breaklines respectively.</summary>
internal static class SculptConstraintMaskBuilder
{
    public static SculptConstraintMask Build(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SculptModifierDefinition sculpt)
    {
        var mask = new SculptConstraintMask(sculpt.EffectiveConstraintFeather);
        IReadOnlyList<ResolvedSourceObject> selectedObjects =
            TerrainBuildSnapshotResolver.ResolveObjects(snapshot, sculpt.Constraints);
        if (selectedObjects.Count == 0)
            return mask;

        var selectedIds = selectedObjects
            .Select(static item => item.ObjectId)
            .Where(static id => id != Guid.Empty)
            .ToHashSet();
        var semanticPathIds = new HashSet<Guid>();
        double curveTolerance = TerrainTolerancePolicy.Create(
            terrain.GlobalTolerance,
            snapshot.ModelAbsoluteTolerance,
            snapshot.ResolvedUnitContext).CurveChordTolerance;

        int sculptIndex = terrain.Modifiers.IndexOf(sculpt);
        int priorCount = sculptIndex < 0 ? 0 : sculptIndex;
        for (int index = 0; index < priorCount; index++)
        {
            if (terrain.Modifiers[index] is not GradePathModifierDefinition gradePath ||
                !gradePath.IsEnabled ||
                gradePath.Width <= 0.0)
            {
                continue;
            }

            foreach (ResolvedSourceObject source in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, gradePath.Paths))
            {
                if (source.ObjectId == Guid.Empty ||
                    !selectedIds.Contains(source.ObjectId) ||
                    source.Geometry is not Curve curve ||
                    !TryGetXyPolyline(curve, curveTolerance, gradePath.Width, out double[] xy, out int count))
                {
                    continue;
                }

                mask.AddPolyline(xy, count, gradePath.Width * 0.5, curve.IsClosed);
                semanticPathIds.Add(source.ObjectId);
            }
        }

        foreach (ResolvedSourceObject source in selectedObjects)
        {
            if (semanticPathIds.Contains(source.ObjectId) || source.Geometry is not Curve curve)
                continue;
            if (!TryGetXyPolyline(curve, curveTolerance, requestedEdgeLength: 0.0, out double[] xy, out int count))
                continue;

            if (curve.IsClosed && count >= 3)
                mask.AddPolygon(xy, count);
            else
                mask.AddPolyline(xy, count, curveTolerance);
        }

        return mask;
    }

    private static bool TryGetXyPolyline(
        Curve curve,
        double tolerance,
        double requestedEdgeLength,
        out double[] xy,
        out int count)
    {
        xy = Array.Empty<double>();
        count = 0;
        if (!RhinoSourceResolver.TryGetPolyline(
                curve,
                tolerance,
                requireClosed: false,
                requestedEdgeLength,
                maxArea: 0.0,
                out Polyline polyline) ||
            polyline.Count < 2)
        {
            return false;
        }

        count = polyline.Count;
        if (curve.IsClosed && count > 2 && polyline[0].DistanceTo(polyline[^1]) <= tolerance)
            count--;
        if (count < 2)
            return false;

        xy = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            xy[i * 2] = polyline[i].X;
            xy[(i * 2) + 1] = polyline[i].Y;
        }

        return true;
    }
}
