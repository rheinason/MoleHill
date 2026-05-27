namespace MoleHill.Core.Grading;

internal static class GradingGeometry2D
{
    public static bool PointInPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        bool inside = false;
        for (int i = 0, j = polyVertCount - 1; i < polyVertCount; j = i++)
        {
            double xi = polyXy[i * 2];
            double yi = polyXy[i * 2 + 1];
            double xj = polyXy[j * 2];
            double yj = polyXy[j * 2 + 1];

            if (((yi > py) != (yj > py)) &&
                (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static double DistanceToPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        double minDist = double.MaxValue;
        for (int i = 0, j = polyVertCount - 1; i < polyVertCount; j = i++)
        {
            double dist = DistanceToSegment(
                px,
                py,
                polyXy[j * 2],
                polyXy[j * 2 + 1],
                polyXy[i * 2],
                polyXy[i * 2 + 1]);
            if (dist < minDist)
                minDist = dist;
        }

        return minDist;
    }

    public static bool AllPointsInsideOrOnBoundary(
        double[] xy,
        int vertexCount,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            double px = xy[i * 2];
            double py = xy[i * 2 + 1];
            if (PointInPolygon(px, py, boundaryLoop, boundaryVertexCount))
                continue;

            if (DistanceToPolygon(px, py, boundaryLoop, boundaryVertexCount) <= tolerance)
                continue;

            return false;
        }

        return true;
    }

    public static int FindNearVertex(List<double> xyList, double px, double py, double tolerance)
    {
        double tolSq = tolerance * tolerance;
        int count = xyList.Count / 2;
        for (int i = 0; i < count; i++)
        {
            double dx = xyList[i * 2] - px;
            double dy = xyList[i * 2 + 1] - py;
            if (dx * dx + dy * dy < tolSq)
                return i;
        }

        return -1;
    }

    public static double InterpolateZ(double[] vertices, int[] faces, int faceCount, double px, double py)
    {
        const double tol = 1e-4;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            double x0 = vertices[i0 * 3];
            double y0 = vertices[i0 * 3 + 1];
            double z0 = vertices[i0 * 3 + 2];
            double x1 = vertices[i1 * 3];
            double y1 = vertices[i1 * 3 + 1];
            double z1 = vertices[i1 * 3 + 2];
            double x2 = vertices[i2 * 3];
            double y2 = vertices[i2 * 3 + 1];
            double z2 = vertices[i2 * 3 + 2];

            double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
            if (Math.Abs(denom) < 1e-12)
                continue;

            double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
            double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
            double w2 = 1.0 - w0 - w1;

            if (w0 >= -tol && w1 >= -tol && w2 >= -tol)
                return w0 * z0 + w1 * z1 + w2 * z2;
        }

        double nearestZ = 0;
        double nearestDistSq = double.MaxValue;
        int vCount = vertices.Length / 3;
        for (int i = 0; i < vCount; i++)
        {
            double dx = vertices[i * 3] - px;
            double dy = vertices[i * 3 + 1] - py;
            double distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestZ = vertices[i * 3 + 2];
            }
        }

        return nearestZ;
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-20)
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

        double t = Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
        double cx = ax + t * dx;
        double cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }
}
