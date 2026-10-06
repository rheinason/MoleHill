namespace MoleHill.Core.Geometry;

/// <summary>
/// The 2D (plan) geometry primitives every part of Core shares. A private copy of one of these in another
/// file is a bug waiting to drift (<c>Geometry2DGuardTests</c> fails on new ones). Where two predicates
/// differ in meaning they carry different names: <see cref="SegmentsCrossStrictly"/> rejects a touch,
/// <see cref="SegmentsTouch"/> accepts one; <see cref="ParameterOnSegment"/> is unclamped,
/// <see cref="ParameterOnSegmentClamped"/> is not.
/// </summary>
internal static class Geometry2D
{
    /// <summary>The z component of the cross product of two plan vectors.</summary>
    public static double Cross(double ax, double ay, double bx, double by) => (ax * by) - (ay * bx);

    /// <summary>
    /// Twice the signed area of triangle a-b-c: positive when c lies left of a->b (counter-clockwise).
    /// </summary>
    public static double Orient(double ax, double ay, double bx, double by, double cx, double cy) =>
        ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));

    /// <summary>Unclamped linear interpolation.</summary>
    public static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    public static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>Squared 3D distance between vertices <paramref name="a"/> and <paramref name="b"/> of a flat XYZ array.</summary>
    public static double DistanceSquared3(double[] vertices, int a, int b)
    {
        double dx = vertices[a * 3] - vertices[b * 3];
        double dy = vertices[(a * 3) + 1] - vertices[(b * 3) + 1];
        double dz = vertices[(a * 3) + 2] - vertices[(b * 3) + 2];
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <inheritdoc cref="DistanceSquared3(double[], int, int)"/>
    public static double DistanceSquared3(List<double> vertices, int a, int b)
    {
        double dx = vertices[a * 3] - vertices[b * 3];
        double dy = vertices[(a * 3) + 1] - vertices[(b * 3) + 1];
        double dz = vertices[(a * 3) + 2] - vertices[(b * 3) + 2];
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>Parameter of the projection of p on the line through a-b (unclamped); 0 for a degenerate segment.</summary>
    public static double ParameterOnSegment(double ax, double ay, double bx, double by, double px, double py)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return (((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared;
    }

    /// <summary><see cref="ParameterOnSegment"/> clamped to [0, 1].</summary>
    public static double ParameterOnSegmentClamped(double ax, double ay, double bx, double by, double px, double py)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0.0, 1.0);
    }

    /// <summary>Signed area of a closed XY loop of <paramref name="count"/> vertices; positive counter-clockwise.</summary>
    public static double SignedArea(double[] xy, int count)
    {
        double sum = 0.0;
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            sum += (xy[i * 2] * xy[(next * 2) + 1]) - (xy[next * 2] * xy[(i * 2) + 1]);
        }

        return sum * 0.5;
    }

    /// <summary>True when p lies in the axis-aligned box of a-b, grown by <paramref name="epsilon"/>.</summary>
    public static bool InSegmentBox(double ax, double ay, double bx, double by, double px, double py, double epsilon = 1e-12) =>
        px >= Math.Min(ax, bx) - epsilon &&
        px <= Math.Max(ax, bx) + epsilon &&
        py >= Math.Min(ay, by) - epsilon &&
        py <= Math.Max(ay, by) + epsilon;

    /// <summary>
    /// True when segments a-b and c-d cross or touch: a proper crossing, or an end of one lying on the other
    /// (orientation within 1e-12 and inside its box). Collinear overlaps count.
    /// </summary>
    public static bool SegmentsTouch(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        double o1 = Orient(ax, ay, bx, by, cx, cy);
        double o2 = Orient(ax, ay, bx, by, dx, dy);
        double o3 = Orient(cx, cy, dx, dy, ax, ay);
        double o4 = Orient(cx, cy, dx, dy, bx, by);

        if (((o1 > 0.0 && o2 < 0.0) || (o1 < 0.0 && o2 > 0.0)) &&
            ((o3 > 0.0 && o4 < 0.0) || (o3 < 0.0 && o4 > 0.0)))
        {
            return true;
        }

        return (Math.Abs(o1) <= 1e-12 && InSegmentBox(ax, ay, bx, by, cx, cy)) ||
               (Math.Abs(o2) <= 1e-12 && InSegmentBox(ax, ay, bx, by, dx, dy)) ||
               (Math.Abs(o3) <= 1e-12 && InSegmentBox(cx, cy, dx, dy, ax, ay)) ||
               (Math.Abs(o4) <= 1e-12 && InSegmentBox(cx, cy, dx, dy, bx, by));
    }

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
                if (SegmentsCrossStrictly(
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

                if (SegmentsCrossStrictly(
                        xy[i * 2], xy[i * 2 + 1], xy[iNext * 2], xy[iNext * 2 + 1],
                        xy[j * 2], xy[j * 2 + 1], xy[jNext * 2], xy[jNext * 2 + 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True only for a proper crossing: each segment's ends lie strictly on opposite sides of the other.
    /// A touch, a shared endpoint or a collinear overlap is not a crossing.
    /// </summary>
    public static bool SegmentsCrossStrictly(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        double o1 = Orient(ax, ay, bx, by, cx, cy);
        double o2 = Orient(ax, ay, bx, by, dx, dy);
        double o3 = Orient(cx, cy, dx, dy, ax, ay);
        double o4 = Orient(cx, cy, dx, dy, bx, by);

        if (((o1 > 0.0 && o2 < 0.0) || (o1 < 0.0 && o2 > 0.0)) &&
            ((o3 > 0.0 && o4 < 0.0) || (o3 < 0.0 && o4 > 0.0)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// A point strictly inside the given simple polygon (flat XY pairs). The vertex average is used
    /// when it lies inside — typical for convex/near-convex footprints — but for concave (L/U-shaped)
    /// polygons the average can fall OUTSIDE, and containment/ownership decisions keyed on it then
    /// misclassify the polygon. In that case the point is derived from the convex-most vertex
    /// (O'Rourke's interior-point construction): the centroid of its ear triangle when no other vertex
    /// intrudes into the ear, otherwise the midpoint between the vertex and the deepest intruding
    /// vertex — both provably interior for a simple polygon.
    /// </summary>
    public static (double X, double Y) PolygonInteriorPoint(double[] xy, int count)
    {
        if (count <= 0)
            return (0.0, 0.0);

        double avgX = 0.0, avgY = 0.0;
        for (int i = 0; i < count; i++)
        {
            avgX += xy[i * 2];
            avgY += xy[i * 2 + 1];
        }

        avgX /= count;
        avgY /= count;
        if (count < 3 || PointInPolygon(avgX, avgY, xy, count))
            return (avgX, avgY);

        // Convex-most vertex (lowest Y, then lowest X) — strictly convex in any simple polygon.
        int v = 0;
        for (int i = 1; i < count; i++)
        {
            double yi = xy[i * 2 + 1];
            double yv = xy[v * 2 + 1];
            if (yi < yv || (yi == yv && xy[i * 2] < xy[v * 2]))
                v = i;
        }

        int p = (v + count - 1) % count;
        int n = (v + 1) % count;
        double ax = xy[p * 2], ay = xy[p * 2 + 1];
        double bx = xy[v * 2], by = xy[v * 2 + 1];
        double cx = xy[n * 2], cy = xy[n * 2 + 1];

        // Degenerate ear (duplicate/collinear corner): keep the average as the least-bad answer.
        if (Math.Abs(Orient(ax, ay, bx, by, cx, cy)) <= 1e-12)
            return (avgX, avgY);

        // Deepest other vertex intruding into the ear triangle, depth measured from line a-c toward
        // b. No intruder: the open ear triangle is empty (no polygon edge can enter it without
        // crossing edge a-b or b-c, which a simple polygon forbids), so its centroid is interior.
        // Otherwise the open segment from b to the deepest intruder is interior — an edge crossing it
        // would need a vertex deeper still — so the midpoint of that segment is interior.
        int deepest = -1;
        double deepestDepth = 0.0;
        for (int i = 0; i < count; i++)
        {
            if (i == p || i == v || i == n)
                continue;

            double qx = xy[i * 2], qy = xy[i * 2 + 1];
            if (!PointInTriangleInclusive(qx, qy, ax, ay, bx, by, cx, cy))
                continue;

            double depth = Math.Abs(Orient(ax, ay, cx, cy, qx, qy));
            if (deepest < 0 || depth > deepestDepth)
            {
                deepest = i;
                deepestDepth = depth;
            }
        }

        if (deepest < 0)
            return ((ax + bx + cx) / 3.0, (ay + by + cy) / 3.0);

        return ((bx + xy[deepest * 2]) * 0.5, (by + xy[(deepest * 2) + 1]) * 0.5);
    }

    /// <summary>True when p lies in triangle a-b-c or on its boundary (orientation tolerance 1e-12), either winding.</summary>
    public static bool PointInTriangleInclusive(
        double px, double py, double ax, double ay, double bx, double by, double cx, double cy)
    {
        const double tolerance = 1e-12;
        double o1 = Orient(ax, ay, bx, by, px, py);
        double o2 = Orient(bx, by, cx, cy, px, py);
        double o3 = Orient(cx, cy, ax, ay, px, py);
        bool hasNegative = o1 < -tolerance || o2 < -tolerance || o3 < -tolerance;
        bool hasPositive = o1 > tolerance || o2 > tolerance || o3 > tolerance;
        return !(hasNegative && hasPositive);
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
