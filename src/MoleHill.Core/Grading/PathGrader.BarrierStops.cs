using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    /// <summary>
    /// Stops paths where they meet a hard constraint instead of refusing them. Each path is split at every
    /// place its centerline crosses a preserved-elevation hard constraint; a piece inside a closed one (a
    /// graded pad) is left to it, and every other piece is graded, ending short of the constraint by as much
    /// as it takes for neither road edge to cross it at that angle. Paths that cross nothing, and paths this
    /// does not reshape (variable-width and one-sided ones), are returned unchanged.
    /// </summary>
    /// <remarks>
    /// A road edge crossing a hard constraint has always been refused, and the refusal took the whole grade
    /// with it: one path into one lawn left every path in the modifier ungraded, while the build reported
    /// success (the park-scale probe: 24 paths, 22 crossings, nothing graded). A graded lawn's boundary, or
    /// a wall, is authoritative where a path meets it, so the path stops there.
    /// </remarks>
    public static PathDefinition[] StopPathsAtHardConstraints(
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double modelTolerance,
        out int stops,
        out int piecesLeftToConstraints)
    {
        stops = 0;
        piecesLeftToConstraints = 0;
        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        var barrierSegments = new List<(double Ax, double Ay, double Bx, double By)>();
        var closedBarriers = new List<double[]>();
        foreach (SurfaceRemesher.ConstraintPolyline constraint in hardConstraints)
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

            // Every crossing as (arc position, setback the road edges need there).
            double halfWidth = path.Width * 0.5;
            var crossings = new List<(double S, double Setback)>();
            for (int i = 0; i < n - 1; i++)
            {
                double ax = xy[i * 2], ay = xy[(i * 2) + 1], bx = xy[(i + 1) * 2], by = xy[((i + 1) * 2) + 1];
                double dx = bx - ax, dy = by - ay;
                double length = Math.Sqrt((dx * dx) + (dy * dy));
                if (length <= tolerance)
                    continue;

                foreach ((double cx, double cy, double ex, double ey) in barrierSegments)
                {
                    if (!TrySegmentIntersection(ax, ay, bx, by, cx, cy, ex, ey, out double t))
                        continue;

                    double bdx = ex - cx, bdy = ey - cy;
                    double barrierLength = Math.Sqrt((bdx * bdx) + (bdy * bdy));
                    double sine = barrierLength > 0 ? Math.Abs((dx * bdy) - (dy * bdx)) / (length * barrierLength) : 1.0;
                    double cosine = barrierLength > 0 ? Math.Abs((dx * bdx) + (dy * bdy)) / (length * barrierLength) : 0.0;
                    double setback = (halfWidth * cosine / Math.Max(sine, 1e-3)) + Math.Max(2.0 * tolerance, halfWidth * 0.05);
                    crossings.Add((cumulative[i] + (t * length), setback));
                }
            }

            if (crossings.Count == 0)
            {
                result.Add(path);
                continue;
            }

            crossings.Sort(static (a, b) => a.S.CompareTo(b.S));
            stops += crossings.Count;

            // Pieces between consecutive crossings; the path's own ends need no setback.
            var bounds = new List<(double S, double Setback)> { (0.0, 0.0) };
            bounds.AddRange(crossings);
            bounds.Add((total, 0.0));
            for (int k = 0; k + 1 < bounds.Count; k++)
            {
                double start = bounds[k].S + bounds[k].Setback;
                double end = bounds[k + 1].S - bounds[k + 1].Setback;
                if (end - start <= Math.Max(4.0 * tolerance, halfWidth * 0.1))
                    continue;

                PointAt(xy, z, cumulative, (bounds[k].S + bounds[k + 1].S) * 0.5, out double mx, out double my, out _);
                if (closedBarriers.Any(ring => GradingGeometry2D.PointInPolygon(mx, my, ring, ring.Length / 2)))
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

    /// <summary>Proper crossing of segment a-b by segment c-e; <paramref name="t"/> is the parameter along a-b.</summary>
    private static bool TrySegmentIntersection(
        double ax, double ay, double bx, double by, double cx, double cy, double ex, double ey, out double t)
    {
        t = 0.0;
        double rx = bx - ax, ry = by - ay, sx = ex - cx, sy = ey - cy;
        double denominator = (rx * sy) - (ry * sx);
        if (Math.Abs(denominator) < 1e-15)
            return false;

        double qx = cx - ax, qy = cy - ay;
        t = ((qx * sy) - (qy * sx)) / denominator;
        double u = ((qx * ry) - (qy * rx)) / denominator;
        return t > 0.0 && t < 1.0 && u >= 0.0 && u <= 1.0;
    }
}
