using MoleHill.Core.Geometry;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    /// <summary>
    /// Stops paths where they meet a hard constraint instead of refusing them. Each path is blocked along
    /// every stretch where a preserved-elevation hard constraint enters its road (between the road edges,
    /// not only across the centerline), and split around those stretches; a piece inside a closed constraint
    /// (a graded pad) is left to it, and every other piece is graded. Paths that meet nothing, and paths this
    /// does not reshape (variable-width and one-sided ones), are returned unchanged.
    /// </summary>
    /// <remarks>
    /// A road edge crossing a hard constraint has always been refused, and the refusal took the whole grade
    /// with it: one path into one lawn left every path in the modifier ungraded, while the build reported
    /// success (the park-scale probe: 24 paths, 22 crossings, nothing graded). A graded lawn's boundary, or
    /// a wall, is authoritative where a path meets it, so the path stops there.
    /// <para>Stops used to come from centerline crossings alone, so a constraint that entered the road
    /// without crossing the centerline (a bend, a pad corner, a stub), or crossed it exactly at a path vertex,
    /// still refused the whole grade; one lying inside the road along it was graded over and lost its height.
    /// <c>GradePathNearConstraintsStressTests</c> covers each.</para>
    /// </remarks>
    public static PathDefinition[] StopPathsAtHardConstraints(
        PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        double modelTolerance,
        out int stops,
        out int piecesLeftToConstraints)
    {
        stops = 0;
        piecesLeftToConstraints = 0;
        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        var barrierSegments = new List<(double Ax, double Ay, double Bx, double By)>();
        var closedBarriers = new List<double[]>();
        foreach (ConstraintPolyline constraint in hardConstraints)
        {
            if (!constraint.PreserveInputElevation || constraint.PointCount < 2)
                continue;

            int count = constraint.PointCount;
            int segmentCount = constraint.IsClosed ? count : count - 1;
            for (int s = 0; s < segmentCount; s++)
            {
                int t = (s + 1) % count;
                barrierSegments.Add((constraint.Points[s * 3], constraint.Points[(s * 3) + 1],
                    constraint.Points[t * 3], constraint.Points[(t * 3) + 1]));
            }

            if (constraint.IsClosed && count >= 3)
            {
                var ring = new double[count * 2];
                for (int i = 0; i < count; i++)
                {
                    ring[i * 2] = constraint.Points[i * 3];
                    ring[(i * 2) + 1] = constraint.Points[(i * 3) + 1];
                }

                closedBarriers.Add(ring);
            }
        }

        if (barrierSegments.Count == 0)
            return paths;

        var result = new List<PathDefinition>(paths.Length);
        foreach (PathDefinition path in paths)
        {
            if (path.HasVariableWidth || path.OutwardNormals is { Length: > 0 } || path.VertexCount < 2)
            {
                result.Add(path);
                continue;
            }

            // The centerline as an open run: a closed path is opened at its first vertex.
            int n = path.VertexCount + (path.IsClosed ? 1 : 0);
            var xy = new double[n * 2];
            var z = new double[n];
            Array.Copy(path.XyVertices, xy, path.VertexCount * 2);
            Array.Copy(path.ZValues, z, path.VertexCount);
            if (path.IsClosed)
            {
                xy[(n - 1) * 2] = xy[0];
                xy[((n - 1) * 2) + 1] = xy[1];
                z[n - 1] = z[0];
            }

            var cumulative = new double[n];
            for (int i = 1; i < n; i++)
                cumulative[i] = cumulative[i - 1] + Math.Sqrt(Sq(xy[i * 2] - xy[(i - 1) * 2]) + Sq(xy[(i * 2) + 1] - xy[((i - 1) * 2) + 1]));
            double total = cumulative[n - 1];

            // Every stretch of the path whose road a constraint enters, as an arc interval.
            double halfWidth = path.Width * 0.5;
            double margin = Math.Max(2.0 * tolerance, halfWidth * 0.05);
            var blocked = new List<(double Start, double End)>();
            for (int i = 0; i < n - 1; i++)
            {
                double ax = xy[i * 2], ay = xy[(i * 2) + 1], bx = xy[(i + 1) * 2], by = xy[((i + 1) * 2) + 1];
                double length = Math.Sqrt(Sq(bx - ax) + Sq(by - ay));
                if (length <= tolerance)
                    continue;

                // The road over this segment, in its own frame (along, across). At an interior vertex it reaches
                // a half width past the vertex, which covers the outside of the bend.
                double ux = (bx - ax) / length, uy = (by - ay) / length;
                double sLow = i > 0 ? -halfWidth : 0.0;
                double sHigh = length + (i < n - 2 ? halfWidth : 0.0);
                double dLimit = halfWidth + margin;
                foreach ((double cx, double cy, double ex, double ey) in barrierSegments)
                {
                    double s0 = ((cx - ax) * ux) + ((cy - ay) * uy), d0 = ((cy - ay) * ux) - ((cx - ax) * uy);
                    double s1 = ((ex - ax) * ux) + ((ey - ay) * uy), d1 = ((ey - ay) * ux) - ((ex - ax) * uy);
                    if (!TryClipToRectangle(s0, d0, s1, d1, sLow, sHigh, -dLimit, dLimit, out double from, out double to))
                        continue;

                    double sFrom = s0 + ((s1 - s0) * from), sTo = s0 + ((s1 - s0) * to);
                    double low = cumulative[i] + Math.Min(sFrom, sTo), high = cumulative[i] + Math.Max(sFrom, sTo);

                    // A path drawn to end on a constraint (snapped to a pad's edge) meets it only across its own
                    // end line; that is where it was meant to stop.
                    if (!path.IsClosed && (high <= tolerance || low >= total - tolerance))
                        continue;

                    blocked.Add((low - margin, high + margin));
                }
            }

            if (blocked.Count == 0)
            {
                result.Add(path);
                continue;
            }

            blocked.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<(double Start, double End)>();
            foreach ((double start, double end) in blocked)
            {
                if (merged.Count > 0 && start <= merged[^1].End)
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
                else
                    merged.Add((start, end));
            }

            stops += merged.Count;

            // The pieces between blocked stretches. A piece shorter than half the road is a nub left by a path
            // drawn a little past a constraint, not a road.
            var pieces = new List<(double Start, double End)>();
            double cursor = 0.0;
            foreach ((double start, double end) in merged)
            {
                pieces.Add((cursor, Math.Min(start, total)));
                cursor = Math.Max(cursor, end);
            }

            pieces.Add((cursor, total));
            foreach ((double start, double end) in pieces)
            {
                if (end - start <= Math.Max(4.0 * tolerance, halfWidth * 0.5))
                    continue;

                PointAt(xy, z, cumulative, (start + end) * 0.5, out double mx, out double my, out _);
                if (closedBarriers.Any(ring => Geometry2D.PointInPolygon(mx, my, ring, ring.Length / 2)))
                {
                    piecesLeftToConstraints++;
                    continue;
                }

                result.Add(Piece(path, xy, z, cumulative, start, end));
            }
        }

        return result.ToArray();

        static double Sq(double value) => value * value;
    }

    private static PathDefinition Piece(PathDefinition path, double[] xy, double[] z, double[] cumulative, double start, double end)
    {
        var pieceXy = new List<double>();
        var pieceZ = new List<double>();
        PointAt(xy, z, cumulative, start, out double sx, out double sy, out double sz);
        pieceXy.Add(sx);
        pieceXy.Add(sy);
        pieceZ.Add(sz);
        for (int i = 0; i < cumulative.Length; i++)
        {
            if (cumulative[i] <= start || cumulative[i] >= end)
                continue;
            pieceXy.Add(xy[i * 2]);
            pieceXy.Add(xy[(i * 2) + 1]);
            pieceZ.Add(z[i]);
        }

        PointAt(xy, z, cumulative, end, out double ex, out double ey, out double ez);
        pieceXy.Add(ex);
        pieceXy.Add(ey);
        pieceZ.Add(ez);

        return new PathDefinition(
            pieceXy.ToArray(), pieceZ.ToArray(), pieceZ.Count, path.Width, path.SlopeAngleDeg, path.MaxDistance,
            path.FillSlopeAngleDeg, leftEdgeXy: null, rightEdgeXy: null, isClosed: false,
            path.LeftCutSlopeAngleDeg, path.LeftFillSlopeAngleDeg, path.RightCutSlopeAngleDeg, path.RightFillSlopeAngleDeg);
    }

    private static void PointAt(double[] xy, double[] z, double[] cumulative, double s, out double x, out double y, out double elevation)
    {
        int i = 1;
        while (i < cumulative.Length - 1 && cumulative[i] < s)
            i++;
        double span = cumulative[i] - cumulative[i - 1];
        double t = span > 0 ? Math.Clamp((s - cumulative[i - 1]) / span, 0.0, 1.0) : 0.0;
        x = xy[(i - 1) * 2] + ((xy[i * 2] - xy[(i - 1) * 2]) * t);
        y = xy[((i - 1) * 2) + 1] + ((xy[(i * 2) + 1] - xy[((i - 1) * 2) + 1]) * t);
        elevation = z[i - 1] + ((z[i] - z[i - 1]) * t);
    }

    /// <summary>
    /// Liang-Barsky: the parameter range [<paramref name="from"/>, <paramref name="to"/>] of segment
    /// (x0, y0)-(x1, y1) inside the rectangle, boundary included; false when no part of it is.
    /// </summary>
    private static bool TryClipToRectangle(
        double x0, double y0, double x1, double y1, double minX, double maxX, double minY, double maxY,
        out double from, out double to)
    {
        from = 0.0;
        to = 1.0;
        double dx = x1 - x0, dy = y1 - y0;
        return Clip(-dx, x0 - minX, ref from, ref to) && Clip(dx, maxX - x0, ref from, ref to) &&
            Clip(-dy, y0 - minY, ref from, ref to) && Clip(dy, maxY - y0, ref from, ref to);

        static bool Clip(double p, double q, ref double from, ref double to)
        {
            if (p == 0.0)
                return q >= 0.0;

            double r = q / p;
            if (p < 0.0)
            {
                if (r > to)
                    return false;
                from = Math.Max(from, r);
            }
            else
            {
                if (r < from)
                    return false;
                to = Math.Min(to, r);
            }

            return true;
        }
    }
}
