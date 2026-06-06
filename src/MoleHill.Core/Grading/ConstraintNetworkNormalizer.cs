using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal static class ConstraintNetworkNormalizer
{
    private readonly record struct Segment(
        double Ax,
        double Ay,
        double Az,
        double Bx,
        double By,
        double Bz,
        bool PreserveInputElevation);

    public static IReadOnlyList<SurfaceRemesher.ConstraintPolyline> SplitAtIntersections(
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance,
        out int splitCount)
    {
        splitCount = 0;
        if (constraints.Count < 2)
            return constraints;

        double resolvedTolerance = Math.Max(tolerance, 1e-9);
        List<Segment> segments = BuildSegments(constraints, resolvedTolerance);
        if (segments.Count < 2)
            return constraints;

        var splitParameters = new List<double>[segments.Count];
        var bounds = new Bounds2D[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            splitParameters[i] = new List<double>(2) { 0.0, 1.0 };
            Segment segment = segments[i];
            bounds[i] = new Bounds2D(
                Math.Min(segment.Ax, segment.Bx) - resolvedTolerance,
                Math.Max(segment.Ax, segment.Bx) + resolvedTolerance,
                Math.Min(segment.Ay, segment.By) - resolvedTolerance,
                Math.Max(segment.Ay, segment.By) + resolvedTolerance);
        }

        for (int i = 0; i < segments.Count; i++)
        {
            for (int j = i + 1; j < segments.Count; j++)
            {
                if (!bounds[i].Intersects(bounds[j]))
                    continue;

                Segment a = segments[i];
                Segment b = segments[j];
                if (!BoundaryClipper.TrySegmentIntersectionParameters(
                        a.Ax,
                        a.Ay,
                        a.Bx,
                        a.By,
                        b.Ax,
                        b.Ay,
                        b.Bx,
                        b.By,
                        out double t,
                        out double u))
                {
                    continue;
                }

                if (AddSplitParameter(splitParameters[i], t))
                    splitCount++;
                if (AddSplitParameter(splitParameters[j], u))
                    splitCount++;
            }
        }

        if (splitCount == 0)
            return constraints;

        var normalized = new List<SurfaceRemesher.ConstraintPolyline>(segments.Count + splitCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < segments.Count; i++)
        {
            Segment segment = segments[i];
            List<double> parameters = splitParameters[i];
            parameters.Sort();

            double previous = 0.0;
            for (int parameterIndex = 1; parameterIndex < parameters.Count; parameterIndex++)
            {
                double current = Math.Clamp(parameters[parameterIndex], 0.0, 1.0);
                if (current - previous <= 1e-9)
                    continue;

                double ax = Lerp(segment.Ax, segment.Bx, previous);
                double ay = Lerp(segment.Ay, segment.By, previous);
                double az = Lerp(segment.Az, segment.Bz, previous);
                double bx = Lerp(segment.Ax, segment.Bx, current);
                double by = Lerp(segment.Ay, segment.By, current);
                double bz = Lerp(segment.Az, segment.Bz, current);
                previous = current;

                double dx = bx - ax;
                double dy = by - ay;
                if ((dx * dx) + (dy * dy) <= resolvedTolerance * resolvedTolerance)
                    continue;

                string key = BuildUndirectedSegmentKey(ax, ay, bx, by, resolvedTolerance);
                if (!seen.Add(key))
                    continue;

                normalized.Add(new SurfaceRemesher.ConstraintPolyline(
                    new[] { ax, ay, az, bx, by, bz },
                    2,
                    IsClosed: false,
                    PreserveInputElevation: segment.PreserveInputElevation));
            }
        }

        return normalized.Count == 0 ? constraints : normalized;
    }

    private static List<Segment> BuildSegments(
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance)
    {
        var segments = new List<Segment>();
        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            int pointCount = NormalizePointCount(constraint, tolerance);
            if (pointCount < 2 || constraint.Points.Length < pointCount * 3)
                continue;

            for (int i = 0; i < pointCount - 1; i++)
                AddSegment(segments, constraint, i, i + 1, tolerance);

            if (constraint.IsClosed)
                AddSegment(segments, constraint, pointCount - 1, 0, tolerance);
        }

        return segments;
    }

    private static void AddSegment(
        List<Segment> segments,
        SurfaceRemesher.ConstraintPolyline constraint,
        int startIndex,
        int endIndex,
        double tolerance)
    {
        double ax = constraint.Points[startIndex * 3];
        double ay = constraint.Points[(startIndex * 3) + 1];
        double az = constraint.Points[(startIndex * 3) + 2];
        double bx = constraint.Points[endIndex * 3];
        double by = constraint.Points[(endIndex * 3) + 1];
        double bz = constraint.Points[(endIndex * 3) + 2];
        double dx = bx - ax;
        double dy = by - ay;
        if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
            return;

        segments.Add(new Segment(ax, ay, az, bx, by, bz, constraint.PreserveInputElevation));
    }

    private static int NormalizePointCount(SurfaceRemesher.ConstraintPolyline constraint, double tolerance)
    {
        int pointCount = constraint.PointCount;
        if (pointCount <= 1 || constraint.Points.Length < pointCount * 3)
            return 0;

        if (!constraint.IsClosed)
            return pointCount;

        double dx = constraint.Points[0] - constraint.Points[(pointCount - 1) * 3];
        double dy = constraint.Points[1] - constraint.Points[((pointCount - 1) * 3) + 1];
        return (dx * dx) + (dy * dy) <= tolerance * tolerance
            ? pointCount - 1
            : pointCount;
    }

    private static bool AddSplitParameter(List<double> parameters, double value)
    {
        double clamped = Math.Clamp(value, 0.0, 1.0);
        for (int i = 0; i < parameters.Count; i++)
        {
            if (Math.Abs(parameters[i] - clamped) <= 1e-9)
                return false;
        }

        parameters.Add(clamped);
        return clamped > 1e-9 && clamped < 1.0 - 1e-9;
    }

    private static string BuildUndirectedSegmentKey(
        double ax,
        double ay,
        double bx,
        double by,
        double tolerance)
    {
        long aqx = Quantize(ax, tolerance);
        long aqy = Quantize(ay, tolerance);
        long bqx = Quantize(bx, tolerance);
        long bqy = Quantize(by, tolerance);

        bool swap = aqx > bqx || (aqx == bqx && aqy > bqy);
        return swap
            ? $"{bqx}:{bqy}|{aqx}:{aqy}"
            : $"{aqx}:{aqy}|{bqx}:{bqy}";
    }

    private static long Quantize(double value, double tolerance)
    {
        return (long)Math.Round(value / tolerance, MidpointRounding.AwayFromZero);
    }

    private static double Lerp(double a, double b, double t)
    {
        return a + ((b - a) * t);
    }
}
