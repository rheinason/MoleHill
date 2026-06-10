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

    /// <summary>True if two closed XY polygons overlap (a vertex of one inside the other, or any edges cross).</summary>
    public static bool PolygonsOverlap(double[] aXy, double[] bXy)
    {
        int aCount = aXy.Length / 2;
        int bCount = bXy.Length / 2;
        if (aCount < 3 || bCount < 3)
            return false;

        for (int i = 0; i < aCount; i++)
        {
            if (PointInPolygon(aXy[i * 2], aXy[i * 2 + 1], bXy, bCount))
                return true;
        }

        for (int i = 0; i < bCount; i++)
        {
            if (PointInPolygon(bXy[i * 2], bXy[i * 2 + 1], aXy, aCount))
                return true;
        }

        for (int i = 0; i < aCount; i++)
        {
            int ai = (i + 1) % aCount;
            for (int j = 0; j < bCount; j++)
            {
                int bj = (j + 1) % bCount;
                if (SegmentsIntersect(
                        aXy[i * 2], aXy[i * 2 + 1], aXy[ai * 2], aXy[ai * 2 + 1],
                        bXy[j * 2], bXy[j * 2 + 1], bXy[bj * 2], bXy[bj * 2 + 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True if a closed XY polygon has any non-adjacent edge crossing (self-intersection).</summary>
    public static bool ClosedPolylineSelfIntersects(double[] xy, int vertexCount)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            int iNext = (i + 1) % vertexCount;
            for (int j = i + 1; j < vertexCount; j++)
            {
                int jNext = (j + 1) % vertexCount;
                if (i == j || i == jNext || iNext == j || iNext == jNext)
                    continue;

                if (SegmentsIntersect(
                        xy[i * 2], xy[i * 2 + 1], xy[iNext * 2], xy[iNext * 2 + 1],
                        xy[j * 2], xy[j * 2 + 1], xy[jNext * 2], xy[jNext * 2 + 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool SegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        double o1 = Cross(ax, ay, bx, by, cx, cy);
        double o2 = Cross(ax, ay, bx, by, dx, dy);
        double o3 = Cross(cx, cy, dx, dy, ax, ay);
        double o4 = Cross(cx, cy, dx, dy, bx, by);

        if (((o1 > 0.0 && o2 < 0.0) || (o1 < 0.0 && o2 > 0.0)) &&
            ((o3 > 0.0 && o4 < 0.0) || (o3 < 0.0 && o4 > 0.0)))
        {
            return true;
        }

        return false;
    }

    private static double Cross(double ax, double ay, double bx, double by, double px, double py) =>
        ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));

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
