using Rhino.Geometry;
using MoleHill.Core.Processing;

namespace MoleHill.Rhino.Services;

internal static class TerrainTriangulationInputBuilder
{
    public static List<double[]> CreateTriangulationPolylines(
        IReadOnlyList<Curve> breaklineCurves,
        IReadOnlyList<Curve> contourCurves,
        double tolerance)
    {
        // Keep the panel's newer conditioning behavior for both source types. Core derives spacing from
        // the observed source segments (with tolerance only as a microscopic floor), preventing the old
        // large-site explosion while preserving straight-run normalization and breakline stations.
        List<double[]> breaklines = CreateFlatPolylines(breaklineCurves, tolerance);
        List<double[]> contours = CreateFlatPolylines(contourCurves, tolerance);
        return TerrainConstraintPreprocessor.Process(
            breaklines,
            contours,
            tolerance);
    }

    public static List<double[]> CreateFlatPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        var result = new List<double[]>();
        foreach (var curve in curves)
        {
            if (curve == null)
                continue;

            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            result.Add(ToFlatPolyline(polyline));
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
