using MoleHill.Core.Sculpting;
using MoleHill.Core.Grading;
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

            (ResolvedGradePathDefinition[] resolvedPaths, _) = TerrainBuildService.ResolveGradePathDefinitions(
                snapshot,
                gradePath,
                curveTolerance,
                curveTolerance);
            foreach (ResolvedGradePathDefinition resolvedPath in resolvedPaths)
            {
                if (resolvedPath.SourceObjectId == Guid.Empty ||
                    !selectedIds.Contains(resolvedPath.SourceObjectId))
                    continue;

                PathGrader.PathDefinition definition = resolvedPath.Definition;
                if (definition.HasVariableWidth)
                {
                    double[] footprint = BuildFootprint(definition);
                    mask.AddPolygon(footprint, footprint.Length / 2);
                }
                else
                {
                    mask.AddPolyline(
                        definition.XyVertices,
                        definition.VertexCount,
                        gradePath.Width * 0.5,
                        definition.IsClosed);
                }
                semanticPathIds.Add(resolvedPath.SourceObjectId);
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

    private static double[] BuildFootprint(PathGrader.PathDefinition path)
    {
        int count = path.VertexCount;
        var footprint = new double[count * 4];
        for (int i = 0; i < count; i++)
        {
            footprint[i * 2] = path.LeftEdgeXy![i * 2];
            footprint[(i * 2) + 1] = path.LeftEdgeXy[(i * 2) + 1];
            int source = count - 1 - i;
            int destination = count + i;
            footprint[destination * 2] = path.RightEdgeXy![source * 2];
            footprint[(destination * 2) + 1] = path.RightEdgeXy[(source * 2) + 1];
        }
        return footprint;
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
