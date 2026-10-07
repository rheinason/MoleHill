using MoleHill.Core.Geometry;

namespace MoleHill.Rhino.Services;

internal static class ConstraintConflictDiagnostics
{
    internal readonly record struct PolylineData(double[] Points, int PointCount, bool IsClosed);

    internal readonly record struct ConflictSample(
        string Kind,
        int PathConstraintIndex,
        int PathSegmentIndex,
        int HardConstraintIndex,
        int HardSegmentIndex,
        double X,
        double Y);

    internal sealed class ConflictSummary
    {
        public required int PathConstraintCount { get; init; }

        public required int PathSegmentCount { get; init; }

        public required int HardConstraintCount { get; init; }

        public required int HardSegmentCount { get; init; }

        public required int IntersectionCount { get; init; }

        public required int OverlapCount { get; init; }

        public required IReadOnlyList<ConflictSample> Samples { get; init; }

        public bool HasConflicts => IntersectionCount > 0 || OverlapCount > 0;

        public string CreateSummaryMessage()
        {
            string summary =
                $"Grade Path preflight: {PathSegmentCount:N0} path segments across {PathConstraintCount:N0} constraints vs {HardSegmentCount:N0} persistent hard-constraint segments across {HardConstraintCount:N0} constraints.";

            if (!HasConflicts)
                return summary + " No crossings or overlaps detected before remesh.";

            return summary +
                   $" Detected {IntersectionCount:N0} crossings/touches and {OverlapCount:N0} overlaps before remesh.";
        }

        public string? CreateSampleMessage(int maxSamples = 3)
        {
            if (Samples.Count == 0 || maxSamples <= 0)
                return null;

            var parts = new List<string>(Math.Min(Samples.Count, maxSamples));
            for (int i = 0; i < Samples.Count && i < maxSamples; i++)
            {
                ConflictSample sample = Samples[i];
                parts.Add(
                    $"{sample.Kind} path[{sample.PathConstraintIndex}] seg {sample.PathSegmentIndex} with hard[{sample.HardConstraintIndex}] seg {sample.HardSegmentIndex} near ({sample.X:0.###}, {sample.Y:0.###})");
            }

            return "Grade Path preflight samples: " + string.Join("; ", parts) + ".";
        }
    }

    private readonly record struct SegmentRef(
        double Ax,
        double Ay,
        double Bx,
        double By,
        int ConstraintIndex,
        int SegmentIndex);

    public static ConflictSummary Analyze(
        IReadOnlyList<PolylineData> pathConstraints,
        IReadOnlyList<PolylineData> hardConstraints,
        double tolerance)
    {
        double resolvedTolerance = Math.Max(tolerance, 1e-9);
        SegmentRef[] pathSegments = BuildSegments(pathConstraints);
        SegmentRef[] hardSegments = BuildSegments(hardConstraints);
        var samples = new List<ConflictSample>(3);
        int intersectionCount = 0;
        int overlapCount = 0;

        for (int pathIndex = 0; pathIndex < pathSegments.Length; pathIndex++)
        {
            SegmentRef pathSegment = pathSegments[pathIndex];
            for (int hardIndex = 0; hardIndex < hardSegments.Length; hardIndex++)
            {
                SegmentRef hardSegment = hardSegments[hardIndex];
                if (!BoundsIntersect(pathSegment, hardSegment, resolvedTolerance))
                    continue;

                ConflictKind kind = Classify(pathSegment, hardSegment, resolvedTolerance, out double x, out double y);
                if (kind == ConflictKind.None)
                    continue;

                if (kind == ConflictKind.Overlap)
                    overlapCount++;
                else
                    intersectionCount++;

                if (samples.Count < 3)
                {
                    samples.Add(new ConflictSample(
                        kind == ConflictKind.Overlap ? "overlap" : "intersection",
                        pathSegment.ConstraintIndex,
                        pathSegment.SegmentIndex,
                        hardSegment.ConstraintIndex,
                        hardSegment.SegmentIndex,
                        x,
                        y));
                }
            }
        }

        return new ConflictSummary
        {
            PathConstraintCount = pathConstraints.Count,
            PathSegmentCount = pathSegments.Length,
            HardConstraintCount = hardConstraints.Count,
            HardSegmentCount = hardSegments.Length,
            IntersectionCount = intersectionCount,
            OverlapCount = overlapCount,
            Samples = samples
        };
    }

    private enum ConflictKind
    {
        None,
        Intersection,
        Overlap
    }

    private static SegmentRef[] BuildSegments(IReadOnlyList<PolylineData> polylines)
    {
        var segments = new List<SegmentRef>();

        for (int constraintIndex = 0; constraintIndex < polylines.Count; constraintIndex++)
        {
            PolylineData polyline = polylines[constraintIndex];
            int pointCount = NormalizePointCount(polyline);
            if (pointCount < 2)
                continue;

            int segmentCount = polyline.IsClosed ? pointCount : pointCount - 1;
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                int next = (segmentIndex + 1) % pointCount;
                double ax = polyline.Points[segmentIndex * 3];
                double ay = polyline.Points[segmentIndex * 3 + 1];
                double bx = polyline.Points[next * 3];
                double by = polyline.Points[next * 3 + 1];
                if (Geometry2D.DistanceSquared(ax, ay, bx, by) <= 1e-18)
                    continue;

                segments.Add(new SegmentRef(ax, ay, bx, by, constraintIndex, segmentIndex));
            }
        }

        return segments.ToArray();
    }

    private static int NormalizePointCount(PolylineData polyline)
    {
        if (!polyline.IsClosed || polyline.PointCount < 3)
            return polyline.PointCount;

        int last = polyline.PointCount - 1;
        double dx = polyline.Points[last * 3] - polyline.Points[0];
        double dy = polyline.Points[last * 3 + 1] - polyline.Points[1];
        return (dx * dx) + (dy * dy) <= 1e-12
            ? last
            : polyline.PointCount;
    }

    private static bool BoundsIntersect(SegmentRef left, SegmentRef right, double tolerance)
    {
        double leftMinX = Math.Min(left.Ax, left.Bx) - tolerance;
        double leftMaxX = Math.Max(left.Ax, left.Bx) + tolerance;
        double leftMinY = Math.Min(left.Ay, left.By) - tolerance;
        double leftMaxY = Math.Max(left.Ay, left.By) + tolerance;

        double rightMinX = Math.Min(right.Ax, right.Bx) - tolerance;
        double rightMaxX = Math.Max(right.Ax, right.Bx) + tolerance;
        double rightMinY = Math.Min(right.Ay, right.By) - tolerance;
        double rightMaxY = Math.Max(right.Ay, right.By) + tolerance;

        return !(leftMaxX < rightMinX || leftMinX > rightMaxX || leftMaxY < rightMinY || leftMinY > rightMaxY);
    }

    private static ConflictKind Classify(SegmentRef left, SegmentRef right, double tolerance, out double x, out double y)
    {
        x = 0.0;
        y = 0.0;

        double rx = left.Bx - left.Ax;
        double ry = left.By - left.Ay;
        double sx = right.Bx - right.Ax;
        double sy = right.By - right.Ay;
        double qpx = right.Ax - left.Ax;
        double qpy = right.Ay - left.Ay;
        double rxs = Geometry2D.Cross(rx, ry, sx, sy);
        double qpxr = Geometry2D.Cross(qpx, qpy, rx, ry);

        if (Math.Abs(rxs) <= tolerance && Math.Abs(qpxr) <= tolerance)
        {
            double t0 = ParameterOnSegment(left.Ax, left.Ay, left.Bx, left.By, right.Ax, right.Ay);
            double t1 = ParameterOnSegment(left.Ax, left.Ay, left.Bx, left.By, right.Bx, right.By);
            double minT = Math.Max(0.0, Math.Min(t0, t1));
            double maxT = Math.Min(1.0, Math.Max(t0, t1));
            if (maxT < 0.0 || minT > 1.0)
                return ConflictKind.None;

            if (maxT - minT <= tolerance)
            {
                var overlapPoint = Lerp(left.Ax, left.Ay, left.Bx, left.By, Math.Clamp(minT, 0.0, 1.0));
                x = overlapPoint.X;
                y = overlapPoint.Y;
                return IsSharedEndpointOnly(left, right, x, y, tolerance)
                    ? ConflictKind.None
                    : ConflictKind.Intersection;
            }

            var midpoint = Lerp(left.Ax, left.Ay, left.Bx, left.By, (minT + maxT) * 0.5);
            x = midpoint.X;
            y = midpoint.Y;
            return ConflictKind.Overlap;
        }

        if (Math.Abs(rxs) <= tolerance)
            return ConflictKind.None;

        double t = Geometry2D.Cross(qpx, qpy, sx, sy) / rxs;
        double u = Geometry2D.Cross(qpx, qpy, rx, ry) / rxs;
        if (t < -tolerance || t > 1.0 + tolerance || u < -tolerance || u > 1.0 + tolerance)
            return ConflictKind.None;

        var intersection = Lerp(left.Ax, left.Ay, left.Bx, left.By, Math.Clamp(t, 0.0, 1.0));
        x = intersection.X;
        y = intersection.Y;
        return IsSharedEndpointOnly(left, right, x, y, tolerance)
            ? ConflictKind.None
            : ConflictKind.Intersection;
    }

    private static bool IsSharedEndpointOnly(SegmentRef left, SegmentRef right, double x, double y, double tolerance)
    {
        bool onLeftStart = Geometry2D.DistanceSquared(left.Ax, left.Ay, x, y) <= tolerance * tolerance;
        bool onLeftEnd = Geometry2D.DistanceSquared(left.Bx, left.By, x, y) <= tolerance * tolerance;
        bool onRightStart = Geometry2D.DistanceSquared(right.Ax, right.Ay, x, y) <= tolerance * tolerance;
        bool onRightEnd = Geometry2D.DistanceSquared(right.Bx, right.By, x, y) <= tolerance * tolerance;

        return (onLeftStart || onLeftEnd) && (onRightStart || onRightEnd);
    }

    private static double ParameterOnSegment(double ax, double ay, double bx, double by, double px, double py)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return (((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared;
    }

    private static (double X, double Y) Lerp(double ax, double ay, double bx, double by, double t)
    {
        return (ax + ((bx - ax) * t), ay + ((by - ay) * t));
    }
}
