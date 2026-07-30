using Rhino.Geometry;
using MoleHill.Core.Processing;

namespace MoleHill.Rhino.Services;

internal static class TerrainTriangulationInputBuilder
{
    internal readonly record struct FlattenedPolyline(double[] Points, bool IsClosed);

    public static List<double[]> CreateTriangulationPolylines(
        IReadOnlyList<Curve> breaklineCurves,
        IReadOnlyList<Curve> contourCurves,
        double tolerance)
    {
        // Keep the panel's newer conditioning behavior for both source types. Core derives spacing from
        // the observed source segments (with tolerance only as a microscopic floor), preventing the old
        // large-site explosion while preserving straight-run normalization and breakline stations.
        List<double[]> breaklines = CreateFlattenedPolylines(breaklineCurves, tolerance)
            .Select(static polyline => polyline.Points)
            .ToList();
        List<double[]> contours = CreateFlattenedPolylines(contourCurves, tolerance)
            .Select(static polyline => polyline.Points)
            .ToList();
        return TerrainConstraintPreprocessor.Process(
            breaklines,
            contours,
            tolerance);
    }

    public static List<double[]> CreateFlatPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        return CreateFlattenedPolylines(curves, tolerance)
            .Select(static polyline => polyline.Points)
            .ToList();
    }

    internal static List<FlattenedPolyline> CreateFlattenedPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        var result = new List<FlattenedPolyline>();
        foreach (var curve in curves)
        {
            if (curve == null)
                continue;

            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            result.Add(new FlattenedPolyline(ToFlatPolyline(polyline), curve.IsClosed));
        }

        return result;
    }

    public static double[] ToFlatPolyline(Polyline polyline)
    {
        var flat = new double[polyline.Count * 3];
        for (int i = 0; i < polyline.Count; i++)
        {
            flat[i * 3] = polyline[i].X;
            flat[i * 3 + 1] = polyline[i].Y;
            flat[i * 3 + 2] = polyline[i].Z;
        }

        return flat;
    }
}
