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
            SimplifyPolylineInPlace(polyline, simplifyTolerance, collapseCollinearVertices: false, maxSegmentLength);
            return polyline.Count >= (requireClosed ? 3 : 2);
        }

        for (int attempt = 0; attempt <= MaxPolylineRetryCount; attempt++)
        {
            var polyCurve = curve.ToPolyline(chordTolerance, AngleToleranceRadians, 0.0, maxSegmentLength);
            if (polyCurve == null || !polyCurve.TryGetPolyline(out polyline))
                return false;

            SimplifyPolylineInPlace(polyline, simplifyTolerance, collapseCollinearVertices: true, maxSegmentLength);
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
        double scale = qualityLength > 0.0 ? qualityLength : Math.Abs(modelTolerance);
        double floor = Math.Max(scale * 1e-12, double.Epsilon);
        double normalizedModelTolerance = Math.Max(Math.Abs(modelTolerance), floor);
        if (qualityLength <= 0)
            return normalizedModelTolerance;

        return Math.Max(Math.Min(normalizedModelTolerance, qualityLength * 0.25), floor);
    }

    private static double GetSimplificationTolerance(double modelTolerance, double qualityLength)
    {
        double scale = qualityLength > 0.0 ? qualityLength : Math.Abs(modelTolerance);
        double floor = Math.Max(scale * 1e-12, double.Epsilon);
        double normalizedModelTolerance = Math.Max(Math.Abs(modelTolerance), floor);
        if (qualityLength <= 0)
            return normalizedModelTolerance;

        return Math.Max(Math.Min(normalizedModelTolerance, qualityLength * 0.1), floor);
    }

    private static void SimplifyPolylineInPlace(
        Polyline polyline,
        double tolerance,
        bool collapseCollinearVertices,
        double maxSegmentLength)
    {
        if (polyline.Count < 3)
            return;

        bool isClosed = polyline.IsClosed;
        double toleranceSquared = tolerance * tolerance;
        var deduplicated = new List<Point3d>(polyline.Count);

        int limit = isClosed && polyline.Count > 1 ? polyline.Count - 1 : polyline.Count;
        for (int i = 0; i < limit; i++)
        {
            Point3d current = polyline[i];
            if (deduplicated.Count > 0)
            {
                Point3d previous = deduplicated[^1];
                double dx = current.X - previous.X;
                double dy = current.Y - previous.Y;
                if ((dx * dx) + (dy * dy) <= toleranceSquared)
                    continue;
            }

            deduplicated.Add(current);
        }

        List<Point3d> simplified = collapseCollinearVertices
            ? CollapseCollinearRuns(deduplicated, tolerance, maxSegmentLength)
            : deduplicated;

        if (isClosed && simplified.Count >= 3)
        {
            if (collapseCollinearVertices && simplified.Count >= 4 &&
                LiesOnChord(simplified[^2], simplified[^1], simplified[0], tolerance))
            {
                simplified.RemoveAt(simplified.Count - 1);
            }

            if (simplified[0].DistanceToSquared(simplified[^1]) > toleranceSquared)
                simplified.Add(simplified[0]);
        }

        if (simplified.Count < (isClosed ? 4 : 2))
            return;

        polyline.Clear();
        for (int i = 0; i < simplified.Count; i++)
            polyline.Add(simplified[i]);
    }

    /// <summary>
    /// Drops the interior vertices of every run that a single chord reproduces: each dropped vertex must lie on
    /// the chord within <paramref name="tolerance"/> in plan <b>and in elevation</b>. Elevation matters because
    /// a run that is straight in plan can still carry a profile (a draped line, a graded centreline), and a
    /// plan-only test collapsed it to one chord and lost the heights. Every vertex dropped from a run is checked
    /// against the run's final chord, not only the last one, so a gentle curve cannot drift off by accumulation.
    /// A run never grows past <paramref name="maxSegmentLength"/> (when positive): that length is the edge length
    /// the caller asked for, and merging past it undid the refinement it requested.
    /// </summary>
    private static List<Point3d> CollapseCollinearRuns(List<Point3d> points, double tolerance, double maxSegmentLength)
    {
        if (points.Count < 3)
            return points;

        var kept = new List<Point3d>(points.Count) { points[0] };
        int anchor = 0;
        int end = 1;
        while (end < points.Count - 1)
        {
            int candidate = end + 1;
            bool fits = maxSegmentLength <= 0.0 ||
                points[anchor].DistanceTo(points[candidate]) <= maxSegmentLength * (1.0 + 1e-9);
            for (int k = anchor + 1; fits && k < candidate; k++)
            {
                if (!LiesOnChord(points[anchor], points[k], points[candidate], tolerance))
                {
                    fits = false;
                    break;
                }
            }

            if (!fits)
            {
                kept.Add(points[end]);
                anchor = end;
            }

            end = candidate;
        }

        kept.Add(points[^1]);
        return kept;
    }

    /// <summary>True when <paramref name="b"/> lies on the chord from <paramref name="a"/> to <paramref name="c"/>
    /// within the tolerance, in plan and in elevation (the chord's height interpolated at b's plan position).</summary>
    private static bool LiesOnChord(Point3d a, Point3d b, Point3d c, double tolerance)
    {
        double acx = c.X - a.X;
        double acy = c.Y - a.Y;
        double chordLengthSquared = (acx * acx) + (acy * acy);
        double toleranceSquared = tolerance * tolerance;
        if (chordLengthSquared <= toleranceSquared)
            return Math.Abs(b.Z - a.Z) <= tolerance && Math.Abs(b.Z - c.Z) <= tolerance;

        double abx = b.X - a.X;
        double aby = b.Y - a.Y;
        double cross = (abx * acy) - (aby * acx);
        if (cross * cross > toleranceSquared * chordLengthSquared)
            return false;

        double t = ((abx * acx) + (aby * acy)) / chordLengthSquared;
        if (t < 0.0 || t > 1.0)
            return false;

        double chordZ = a.Z + (t * (c.Z - a.Z));
        return Math.Abs(b.Z - chordZ) <= tolerance;
    }
}
