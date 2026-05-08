using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal readonly record struct BarrierSegment(
    double Ax,
    double Ay,
    double Bx,
    double By,
    Bounds2D Bounds);

internal sealed class PreparedBarriers
{
    public static readonly PreparedBarriers Empty =
        new(Array.Empty<BarrierSegment>(), SpatialHashGrid2D.Build(Array.Empty<Bounds2D>()));

    public BarrierSegment[] Segments { get; }
    public SpatialHashGrid2D Index { get; }

    public PreparedBarriers(BarrierSegment[] segments, SpatialHashGrid2D index)
    {
        Segments = segments;
        Index = index;
    }
}

internal static class GradingBarriers
{
    /// <summary>
    /// Builds barrier segments from hard constraints (PathGrader source).
    /// Only constraints with PreserveInputElevation=true are treated as barriers.
    /// </summary>
    internal static PreparedBarriers Build(IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        if (constraints.Count == 0)
            return PreparedBarriers.Empty;

        const double tolerance = 1e-6;
        var segments = new List<BarrierSegment>(constraints.Count * 2);
        foreach (var constraint in constraints)
        {
            if (!constraint.PreserveInputElevation)
                continue;

            int pointCount = NormalizeConstraintPointCount(constraint, tolerance);
            if (pointCount < 2)
                continue;

            for (int i = 0; i < pointCount - 1; i++)
                AddConstraintSegment(segments, constraint.Points, i, i + 1);

            if (constraint.IsClosed)
                AddConstraintSegment(segments, constraint.Points, pointCount - 1, 0);
        }

        return BuildFromSegmentList(segments);
    }

    /// <summary>
    /// Builds barrier segments from lock curves (PadGrader source).
    /// All lock curves are treated as barriers.
    /// </summary>
    internal static PreparedBarriers BuildFromLockCurves(IReadOnlyList<PadGrader.LockCurve> lockCurves)
    {
        if (lockCurves.Count == 0)
            return PreparedBarriers.Empty;

        var segments = new List<BarrierSegment>(lockCurves.Count * 2);
        foreach (var lc in lockCurves)
        {
            if (lc.VertexCount < 2)
                continue;

            for (int i = 0; i < lc.VertexCount - 1; i++)
            {
                double ax = lc.XyVertices[i * 2];
                double ay = lc.XyVertices[i * 2 + 1];
                double bx = lc.XyVertices[(i + 1) * 2];
                double by = lc.XyVertices[(i + 1) * 2 + 1];
                AddRawSegment(segments, ax, ay, bx, by);
            }
        }

        return BuildFromSegmentList(segments);
    }

    /// <summary>
    /// Clips the segment (ax,ay)→(bx,by) to the first barrier crossing.
    /// Returns true if clipped; clippedBx/By is the intersection point.
    /// Returns false (with clippedBx/By = bx/by) if no crossing found.
    /// </summary>
    internal static bool TryClipSegment(
        PreparedBarriers barriers,
        double ax, double ay,
        double bx, double by,
        SpatialHashGrid2D.QueryScratch scratch,
        List<int> candidates,
        out double clippedBx,
        out double clippedBy)
    {
        if (barriers.Segments.Length == 0)
        {
            clippedBx = bx;
            clippedBy = by;
            return false;
        }

        var queryBounds = new Bounds2D(
            Math.Min(ax, bx),
            Math.Max(ax, bx),
            Math.Min(ay, by),
            Math.Max(ay, by));

        barriers.Index.GatherCandidates(queryBounds, candidates, scratch);

        double minT = double.MaxValue;
        foreach (int idx in candidates)
        {
            BarrierSegment seg = barriers.Segments[idx];
            if (!seg.Bounds.Intersects(queryBounds))
                continue;

            if (!BoundaryClipper.TrySegmentIntersectionParameters(
                    ax, ay, bx, by,
                    seg.Ax, seg.Ay, seg.Bx, seg.By,
                    out double t, out _))
                continue;

            // Only accept crossings strictly along the segment, away from the start
            if (t > 1e-9 && t < minT)
                minT = t;
        }

        if (minT <= 1.0)
        {
            clippedBx = ax + minT * (bx - ax);
            clippedBy = ay + minT * (by - ay);
            return true;
        }

        clippedBx = bx;
        clippedBy = by;
        return false;
    }

    /// <summary>
    /// Returns true if the segment (ax,ay)→(bx,by) crosses any barrier.
    /// Used for Z-grading barrier block checks.
    /// </summary>
    internal static bool IsCrossedByBarrier(
        PreparedBarriers barriers,
        double ax, double ay,
        double bx, double by,
        SpatialHashGrid2D.QueryScratch scratch,
        List<int> candidates)
    {
        if (barriers.Segments.Length == 0)
            return false;

        var queryBounds = new Bounds2D(
            Math.Min(ax, bx),
            Math.Max(ax, bx),
            Math.Min(ay, by),
            Math.Max(ay, by));

        barriers.Index.GatherCandidates(queryBounds, candidates, scratch);
        foreach (int idx in candidates)
        {
            BarrierSegment seg = barriers.Segments[idx];
            if (!seg.Bounds.Intersects(queryBounds))
                continue;

            if (BoundaryClipper.TrySegmentIntersectionParameters(
                    ax, ay, bx, by,
                    seg.Ax, seg.Ay, seg.Bx, seg.By,
                    out double t, out double u))
            {
                _ = u; // u is within [0,1] by TrySegmentIntersectionParameters contract
                if (t > 1e-9)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true only when the segment crosses a barrier away from its own endpoints.
    /// Endpoint touches are allowed so grade paths can start/end on existing breaklines.
    /// </summary>
    internal static bool IsInteriorCrossedByBarrier(
        PreparedBarriers barriers,
        double ax, double ay,
        double bx, double by,
        double startEndpointTolerance,
        double endEndpointTolerance,
        SpatialHashGrid2D.QueryScratch scratch,
        List<int> candidates)
    {
        if (barriers.Segments.Length == 0)
            return false;

        double dx = bx - ax;
        double dy = by - ay;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-12)
            return false;

        double startEndpointT = Math.Min(0.5, Math.Max(startEndpointTolerance, 1e-12) / length);
        double endEndpointT = Math.Min(0.5, Math.Max(endEndpointTolerance, 1e-12) / length);
        var queryBounds = new Bounds2D(
            Math.Min(ax, bx),
            Math.Max(ax, bx),
            Math.Min(ay, by),
            Math.Max(ay, by));

        barriers.Index.GatherCandidates(queryBounds, candidates, scratch);
        foreach (int idx in candidates)
        {
            BarrierSegment seg = barriers.Segments[idx];
            if (!seg.Bounds.Intersects(queryBounds))
                continue;

            if (BoundaryClipper.TrySegmentIntersectionParameters(
                    ax, ay, bx, by,
                    seg.Ax, seg.Ay, seg.Bx, seg.By,
                    out double t, out _))
            {
                if (t > startEndpointT && t < 1.0 - endEndpointT)
                    return true;
            }
        }

        return false;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static PreparedBarriers BuildFromSegmentList(List<BarrierSegment> segments)
    {
        if (segments.Count == 0)
            return PreparedBarriers.Empty;

        BarrierSegment[] arr = segments.ToArray();
        var bounds = new Bounds2D[arr.Length];
        for (int i = 0; i < arr.Length; i++)
            bounds[i] = arr[i].Bounds;

        return new PreparedBarriers(arr, SpatialHashGrid2D.Build(bounds));
    }

    private static void AddConstraintSegment(
        List<BarrierSegment> segments,
        double[] points,
        int startIndex,
        int endIndex)
    {
        double ax = points[startIndex * 3];
        double ay = points[startIndex * 3 + 1];
        double bx = points[endIndex * 3];
        double by = points[endIndex * 3 + 1];
        AddRawSegment(segments, ax, ay, bx, by);
    }

    private static void AddRawSegment(List<BarrierSegment> segments, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        if ((dx * dx) + (dy * dy) <= 1e-20)
            return;

        segments.Add(new BarrierSegment(
            ax, ay, bx, by,
            new Bounds2D(
                Math.Min(ax, bx),
                Math.Max(ax, bx),
                Math.Min(ay, by),
                Math.Max(ay, by))));
    }

    private static int NormalizeConstraintPointCount(SurfaceRemesher.ConstraintPolyline constraint, double tolerance)
    {
        int count = constraint.PointCount;
        if (!constraint.IsClosed || count < 2)
            return count;

        // Drop duplicate closing vertex if present
        double lastX = constraint.Points[(count - 1) * 3];
        double lastY = constraint.Points[(count - 1) * 3 + 1];
        double firstX = constraint.Points[0];
        double firstY = constraint.Points[1];
        double dx = lastX - firstX;
        double dy = lastY - firstY;
        if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
            return count - 1;

        return count;
    }
}
