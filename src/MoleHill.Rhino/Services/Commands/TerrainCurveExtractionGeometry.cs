using MoleHill.Core.Processing;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Creates exactly the same native contour for viewport preview and document output.</summary>
internal static class TerrainCurveExtractionGeometry
{
    public static Curve Contour(TerrainCurveRecovery.RecoveredContour contour)
    {
        if (contour.Curve != null) return RouteCurveBuilder.FromFitted(contour.Curve);
        var points = new List<Point3d>();
        for (int i = 0; i < contour.PointsXyz.Length; i += 3)
            points.Add(new Point3d(contour.PointsXyz[i], contour.PointsXyz[i + 1], contour.PointsXyz[i + 2]));
        if (contour.IsClosed) points.Add(points[0]);
        using var polyline = new PolylineCurve(points);
        return polyline.ToNurbsCurve();
    }
}
