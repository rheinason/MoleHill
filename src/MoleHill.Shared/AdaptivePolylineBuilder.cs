using Rhino.Geometry;

namespace MoleHill.Shared;

internal static class AdaptivePolylineBuilder
{
    private const int MaxPolylineVertexCount = 4096;
    private const int MaxPolylineRetryCount = 4;
    private const double AngleToleranceRadians = Math.PI / 36.0;

    public static bool TryGetPolyline(
        Curve curve,
        double modelTolerance,
        bool requireClosed,
        double requestedEdgeLength,
        double maxArea,
        out Polyline polyline)
    {
        polyline = new Polyline();

        if (requireClosed && !curve.IsClosed)
            return false;

        double qualityLength = GetQualityLength(requestedEdgeLength, maxArea);
        double chordTolerance = GetInitialChordTolerance(modelTolerance, qualityLength);
        double simplifyTolerance = GetSimplificationTolerance(modelTolerance, qualityLength);
        double maxSegmentLength = qualityLength > 0 ? qualityLength : 0.0;

        if (curve.TryGetPolyline(out polyline))
        {
            SimplifyPolylineInPlace(polyline, simplifyTolerance, collapseCollinearVertices: false);
            return polyline.Count >= (requireClosed ? 3 : 2);
        }

        for (int attempt = 0; attempt <= MaxPolylineRetryCount; attempt++)
        {
            var polyCurve = curve.ToPolyline(chordTolerance, AngleToleranceRadians, 0.0, maxSegmentLength);
            if (polyCurve == null || !polyCurve.TryGetPolyline(out polyline))
                return false;

            SimplifyPolylineInPlace(polyline, simplifyTolerance, collapseCollinearVertices: true);
            if (polyline.Count <= MaxPolylineVertexCount)
                return polyline.Count >= (requireClosed ? 3 : 2);

            chordTolerance *= 2.0;
            if (maxSegmentLength > 0.0)
                maxSegmentLength *= 2.0;
        }

        return polyline.Count >= (requireClosed ? 3 : 2);
    }

    private static double GetQualityLength(double requestedEdgeLength, double maxArea)
    {
        if (requestedEdgeLength > 0)
            return requestedEdgeLength;

        if (maxArea > 0)
            return Math.Sqrt(4.0 * maxArea / Math.Sqrt(3.0));

        return 0.0;
    }

    private static double GetInitialChordTolerance(double modelTolerance, double qualityLength)
    {
        double normalizedModelTolerance = Math.Max(modelTolerance, 1e-9);
        if (qualityLength <= 0)
            return normalizedModelTolerance;

        return Math.Max(Math.Min(normalizedModelTolerance, qualityLength * 0.25), 1e-9);
    }

    private static double GetSimplificationTolerance(double modelTolerance, double qualityLength)
    {
        double normalizedModelTolerance = Math.Max(modelTolerance, 1e-9);
        if (qualityLength <= 0)
            return normalizedModelTolerance;

        return Math.Max(Math.Min(normalizedModelTolerance, qualityLength * 0.1), 1e-9);
    }

    private static void SimplifyPolylineInPlace(Polyline polyline, double xyTolerance, bool collapseCollinearVertices)
    {
        if (polyline.Count < 3)
            return;

        bool isClosed = polyline.IsClosed;
        double xyToleranceSquared = xyTolerance * xyTolerance;
        var simplified = new List<Point3d>(polyline.Count);

        int limit = isClosed && polyline.Count > 1 ? polyline.Count - 1 : polyline.Count;
        for (int i = 0; i < limit; i++)
        {
            Point3d current = polyline[i];
            if (simplified.Count > 0)
            {
                Point3d previous = simplified[^1];
                double dx = current.X - previous.X;
                double dy = current.Y - previous.Y;
                if ((dx * dx) + (dy * dy) <= xyToleranceSquared)
                    continue;
            }

            if (collapseCollinearVertices)
            {
                while (simplified.Count >= 2 && IsNearlyCollinear(simplified[^2], simplified[^1], current, xyToleranceSquared))
                    simplified.RemoveAt(simplified.Count - 1);
            }

            simplified.Add(current);
        }

        if (isClosed && simplified.Count >= 3)
        {
            if (collapseCollinearVertices)
            {
                while (simplified.Count >= 3 && IsNearlyCollinear(simplified[^2], simplified[^1], simplified[0], xyToleranceSquared))
                    simplified.RemoveAt(simplified.Count - 1);
            }

            if (simplified[0].DistanceToSquared(simplified[^1]) > xyToleranceSquared)
                simplified.Add(simplified[0]);
        }

        if (simplified.Count < (isClosed ? 4 : 2))
            return;

        polyline.Clear();
        for (int i = 0; i < simplified.Count; i++)
            polyline.Add(simplified[i]);
    }

    private static bool IsNearlyCollinear(Point3d a, Point3d b, Point3d c, double toleranceSquared)
    {
        double abx = b.X - a.X;
        double aby = b.Y - a.Y;
        double bcx = c.X - b.X;
        double bcy = c.Y - b.Y;
        double cross = Math.Abs((abx * bcy) - (aby * bcx));
        double chordLengthSquared = ((c.X - a.X) * (c.X - a.X)) + ((c.Y - a.Y) * (c.Y - a.Y));
        if (chordLengthSquared <= toleranceSquared)
            return true;

        return (cross * cross) <= toleranceSquared * chordLengthSquared;
    }
}
