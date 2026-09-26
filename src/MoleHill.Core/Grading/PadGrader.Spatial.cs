namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    // Compatibility wrappers. New internal code should use the neutral helpers directly.

    internal sealed class SpatialHash : SpatialVertexHash
    {
        public SpatialHash(double tolerance)
            : base(tolerance)
        {
        }
    }

    internal sealed class FaceGrid : TerrainFaceGrid
    {
        public FaceGrid(double[] vertices, int vertexCount, int[] faces, int faceCount)
            : base(vertices, vertexCount, faces, faceCount)
        {
        }
    }

    public static bool PointInPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        return GradingGeometry2D.PointInPolygon(px, py, polyXy, polyVertCount);
    }

    public static double DistToPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        return GradingGeometry2D.DistanceToPolygon(px, py, polyXy, polyVertCount);
    }

    public static (double X, double Y) PolygonInteriorPoint(double[] polyXy, int polyVertCount)
    {
        return GradingGeometry2D.PolygonInteriorPoint(polyXy, polyVertCount);
    }

    public static int FindNearVertex(List<double> xyList, double px, double py, double tolerance)
    {
        return GradingGeometry2D.FindNearVertex(xyList, px, py, tolerance);
    }

    public static double InterpolateZ(double[] vertices, int[] faces, int faceCount, double px, double py)
    {
        return GradingGeometry2D.InterpolateZ(vertices, faces, faceCount, px, py);
    }
}
