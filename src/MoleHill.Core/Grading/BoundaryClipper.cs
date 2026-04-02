namespace MoleHill.Core.Grading;

internal readonly record struct ClippedSegment(
    double StartX,
    double StartY,
    double StartZ,
    double EndX,
    double EndY,
    double EndZ,
    double StartT,
    double EndT);

internal static class BoundaryClipper
{
    private const double ParameterTolerance = 1e-9;

    public static bool IsInsideOrOnBoundary(
        double x,
        double y,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        if (!hasBoundaryLoop)
            return true;

        return PadGrader.PointInPolygon(x, y, boundaryLoop, boundaryVertexCount)
            || PadGrader.DistToPolygon(x, y, boundaryLoop, boundaryVertexCount) <= tolerance;
    }

    public static bool IsPolylineInsideBoundary(
        double[] xyVertices,
        int vertexCount,
        bool isClosed,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        if (!hasBoundaryLoop || vertexCount <= 0)
            return true;

        if (!PadGrader.AllPointsInsideOrOnBoundary(xyVertices, vertexCount, boundaryLoop, boundaryVertexCount, tolerance))
            return false;

        int segmentCount = isClosed ? vertexCount : vertexCount - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            int next = (i + 1) % vertexCount;
            if (!SegmentIsFullyInsideBoundary(
                    xyVertices[i * 2],
                    xyVertices[i * 2 + 1],
                    xyVertices[next * 2],
                    xyVertices[next * 2 + 1],
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    tolerance))
            {
                return false;
            }
        }

        return true;
    }

    public static bool SegmentIsFullyInsideBoundary(
        double ax,
        double ay,
        double bx,
        double by,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        if (!hasBoundaryLoop)
            return true;

        List<ClippedSegment> pieces = ClipSegmentToBoundary(
            ax,
            ay,
            0.0,
            bx,
            by,
            0.0,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance);

        if (pieces.Count != 1)
            return false;

        ClippedSegment piece = pieces[0];
        return piece.StartT <= ParameterTolerance && piece.EndT >= 1.0 - ParameterTolerance;
    }

    public static List<ClippedSegment> ClipSegmentToBoundary(
        double ax,
        double ay,
        double az,
        double bx,
        double by,
        double bz,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        var pieces = new List<ClippedSegment>(1);
        if (!hasBoundaryLoop || boundaryVertexCount < 3)
        {
            pieces.Add(new ClippedSegment(ax, ay, az, bx, by, bz, 0.0, 1.0));
            return pieces;
        }

        var parameters = new List<double>(boundaryVertexCount + 2) { 0.0, 1.0 };
        for (int i = 0; i < boundaryVertexCount; i++)
        {
            int next = (i + 1) % boundaryVertexCount;
            double cx = boundaryLoop[i * 2];
            double cy = boundaryLoop[i * 2 + 1];
            double dx = boundaryLoop[next * 2];
            double dy = boundaryLoop[next * 2 + 1];

            if (TrySegmentIntersectionParameters(ax, ay, bx, by, cx, cy, dx, dy, out double t, out _))
            {
                parameters.Add(Math.Clamp(t, 0.0, 1.0));
            }
            else if (TryCollinearOverlapParameters(ax, ay, bx, by, cx, cy, dx, dy, out double overlapStart, out double overlapEnd))
            {
                parameters.Add(Math.Clamp(overlapStart, 0.0, 1.0));
                parameters.Add(Math.Clamp(overlapEnd, 0.0, 1.0));
            }
        }

        parameters.Sort();
        int uniqueCount = 0;
        for (int i = 0; i < parameters.Count; i++)
        {
            double value = Math.Clamp(parameters[i], 0.0, 1.0);
            if (uniqueCount > 0 && Math.Abs(value - parameters[uniqueCount - 1]) <= ParameterTolerance)
                continue;

            parameters[uniqueCount++] = value;
        }

        for (int i = 0; i < uniqueCount - 1; i++)
        {
            double startT = parameters[i];
            double endT = parameters[i + 1];
            if (endT - startT <= ParameterTolerance)
                continue;

            double midT = (startT + endT) * 0.5;
            double midX = ax + ((bx - ax) * midT);
            double midY = ay + ((by - ay) * midT);
            if (!IsInsideOrOnBoundary(midX, midY, true, boundaryLoop, boundaryVertexCount, tolerance))
                continue;

            pieces.Add(new ClippedSegment(
                ax + ((bx - ax) * startT),
                ay + ((by - ay) * startT),
                az + ((bz - az) * startT),
                ax + ((bx - ax) * endT),
                ay + ((by - ay) * endT),
                az + ((bz - az) * endT),
                startT,
                endT));
        }

        return pieces;
    }

    internal static bool TrySegmentIntersectionParameters(
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy,
        double dx,
        double dy,
        out double t,
        out double u)
    {
        double abx = bx - ax;
        double aby = by - ay;
        double cdx = dx - cx;
        double cdy = dy - cy;
        double denom = Cross(abx, aby, cdx, cdy);
        if (Math.Abs(denom) <= 1e-12)
        {
            t = 0.0;
            u = 0.0;
            return false;
        }

        double acx = cx - ax;
        double acy = cy - ay;
        t = Cross(acx, acy, cdx, cdy) / denom;
        u = Cross(acx, acy, abx, aby) / denom;
        return t >= -ParameterTolerance &&
               t <= 1.0 + ParameterTolerance &&
               u >= -ParameterTolerance &&
               u <= 1.0 + ParameterTolerance;
    }

    private static bool TryCollinearOverlapParameters(
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy,
        double dx,
        double dy,
        out double startT,
        out double endT)
    {
        startT = 0.0;
        endT = 0.0;

        double abx = bx - ax;
        double aby = by - ay;
        double acx = cx - ax;
        double acy = cy - ay;
        double adx = dx - ax;
        double ady = dy - ay;
        if (Math.Abs(Cross(abx, aby, acx, acy)) > 1e-12 ||
            Math.Abs(Cross(abx, aby, adx, ady)) > 1e-12)
        {
            return false;
        }

        double lenSq = (abx * abx) + (aby * aby);
        if (lenSq <= 1e-20)
            return false;

        double tc = ((acx * abx) + (acy * aby)) / lenSq;
        double td = ((adx * abx) + (ady * aby)) / lenSq;
        startT = Math.Max(0.0, Math.Min(tc, td));
        endT = Math.Min(1.0, Math.Max(tc, td));
        return endT - startT > ParameterTolerance;
    }

    private static double Cross(double ax, double ay, double bx, double by)
    {
        return (ax * by) - (ay * bx);
    }
}
