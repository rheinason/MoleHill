using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class TerrainTriangulationInputBuilder
{
    public static List<double[]> CreateTriangulationPolylines(
        IReadOnlyList<Curve> breaklineCurves,
        IReadOnlyList<Curve> contourCurves,
        double tolerance)
    {
        var result = CreateFlatPolylines(breaklineCurves, tolerance);
        result.AddRange(CreateFlatPolylines(contourCurves, tolerance));
        return result;
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
