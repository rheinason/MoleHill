using MoleHill.Core.Routing;

namespace MoleHill.Core.Processing;

/// <summary>Fits contours before measuring their reconstruction; checks the terrain and requested region.</summary>
public static partial class TerrainCurveRecovery
{
    private static RecoveredContour FitContour(RecoveredContour contour, double planTolerance, double verticalTolerance,
        Func<double, double, double?> terrainAt, ITerrainRecoveryRegion? region)
    {
        double[] input = contour.IsClosed ? contour.PointsXyz.Concat(contour.PointsXyz.Take(3)).ToArray() : contour.PointsXyz;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            double plan = planTolerance * 0.25 * Math.Pow(0.25, attempt);
            FittedRouteCurve curve = RouteCurveFitter.Fit(new IReadOnlyList<double>[] { input }, plan, verticalTolerance * 0.1, 0, 0, 0);
            curve = AtElevation(curve, contour.Level);
            if (curve.Pieces.Count == 0 || curve.MaxPlanDeviation > plan * 1.01)
                continue;
            double[] samples = curve.PointsXyz;
            bool fits = true;
            for (int i = 0; i < samples.Length; i += 3)
            {
                // Region membership is checked by clipping below: a region may measure less ground than it
                // clips to (scan contours run through rough patches that are measured loosely).
                if (terrainAt(samples[i], samples[i + 1]) is not double height || Math.Abs(height - contour.Level) > verticalTolerance)
                {
                    fits = false;
                    break;
                }
            }
            if (!fits)
                continue;
            if (region != null)
            {
                double kept = region.Clip(samples, false).Sum(part => PlanLength(part.Points));
                if (PlanLength(samples) - kept > plan * 1e-4)
                    continue;
            }
            double[] distinct = contour.IsClosed ? samples[..^3] : samples;
            double[] light = contour.IsClosed
                ? SimplifyClosed(distinct, plan * 0.05, verticalTolerance * 0.05, terrainAt)
                : SimplifyOpen(distinct, plan * 0.05, verticalTolerance * 0.05, terrainAt);
            for (int i = 2; i < light.Length; i += 3)
                light[i] = contour.Level;
            return new RecoveredContour { Level = contour.Level, PointsXyz = light, IsClosed = contour.IsClosed, Curve = curve };
        }
        // A tight corner beside a mask edge can be impossible to smooth without crossing the excluded
        // area. Keep its exact clipped polyline; the host can express it as a degree-one NURBS.
        return contour;
    }

    /// <summary>
    /// Quadratic B-splines whose controls are the simplified contour's own vertices: as many controls as the
    /// polyline, smooth everywhere but at corners. A closed contour without corners is seamed at the midpoint of its
    /// last leg so it stays smooth across the seam. Where a span cuts a corner beyond the plan tolerance or the
    /// level's vertical tolerance, its middle vertex becomes a corner: the spline is split there and passes through
    /// it, kinked. Fitting a general spline to the polyline instead spent a control on every corner; on a designed
    /// terrain, whose contours turn sharply at pad edges and batter toes, it also ran for seconds per contour, mostly
    /// to give up and keep the polyline. Null when a straight leg itself misses the terrain or the curve leaves the
    /// region.
    /// </summary>
    private static RecoveredContour? SmoothContour(RecoveredContour contour, double planTolerance, double verticalTolerance,
        Func<double, double, double?> terrainAt, ITerrainRecoveryRegion? region)
    {
        double[] p = contour.PointsXyz; int n = p.Length / 3;
        if (n < 3) return null;
        bool closed = contour.IsClosed;
        var corners = new bool[n];
        // A contour smooth throughout may cut corners to the plan tolerance. Once it needs corners it is held as
        // closely as the general fit held it: cut to the full tolerance, its corners added spot heights beside it
        // (5,847 to 6,697 on bumpy ground).
        double planLimit = planTolerance;
        while (true)
        {
            var pieces = new List<RouteSplineCurve>();
            var samples = new List<double>();
            var added = new List<int>();
            double deviation = 0;
            foreach (int[] run in Runs())
            {
                // Controls are the run's vertices (-1: a seam or a straight leg's midpoint, which is no vertex).
                var vertex = new List<int>();
                var xy = new List<(double X, double Y)>();
                if (run.Length == 0)
                {
                    var seam = ((p[^3] + p[0]) * .5, (p[^2] + p[1]) * .5);
                    xy.Add(seam); vertex.Add(-1);
                    for (int i = 0; i < n; i++) { xy.Add((p[i * 3], p[i * 3 + 1])); vertex.Add(i); }
                    xy.Add(seam); vertex.Add(-1);
                }
                else
                {
                    foreach (int v in run) { xy.Add((p[v * 3], p[v * 3 + 1])); vertex.Add(v); }
                    if (run.Length == 2)
                    {
                        xy.Insert(1, ((xy[0].X + xy[1].X) * .5, (xy[0].Y + xy[1].Y) * .5)); vertex.Insert(1, -1);
                    }
                }
                int controls = xy.Count, spans = controls - 2;
                var knots = new double[controls + 3];
                for (int i = 0; i < knots.Length; i++) knots[i] = Math.Clamp(i - 2, 0, spans) / (double)spans;
                var points = new double[controls * 3];
                for (int i = 0; i < controls; i++) { points[i * 3] = xy[i].X; points[i * 3 + 1] = xy[i].Y; points[i * 3 + 2] = contour.Level; }
                var spline = new RouteSplineCurve(2, knots, points);
                pieces.Add(spline);
                int count = spans * 8, failedSpan = -1;
                for (int i = samples.Count == 0 ? 0 : 1; i <= count; i++)
                {
                    int span = Math.Min(i / 8, spans - 1);
                    var (x, y, _) = spline.PointAt(i / (double)count);
                    // A span follows its own three controls, so the legs beside them are measured first; the whole
                    // polyline only when those are out of tolerance.
                    double distance = double.PositiveInfinity;
                    for (int c = Math.Max(0, span - 1); c <= Math.Min(controls - 2, span + 2); c++)
                    {
                        int a = vertex[c], b = vertex[c + 1];
                        if (a < 0 || b < 0) continue;
                        distance = Math.Min(distance, PlanDistanceToSegment(x, y, p[a * 3], p[a * 3 + 1], p[b * 3], p[b * 3 + 1], out _));
                    }
                    if (distance > planLimit) distance = PlanDistanceToPolyline(p, closed, x, y);
                    if (distance > planLimit || terrainAt(x, y) is not double height || Math.Abs(height - contour.Level) > verticalTolerance)
                    {
                        if (span == failedSpan) continue;
                        failedSpan = span;
                        int middle = vertex[span + 1];
                        if (middle < 0 || corners[middle]) return null;
                        added.Add(middle);
                        continue;
                    }
                    deviation = Math.Max(deviation, distance);
                    samples.Add(x); samples.Add(y); samples.Add(contour.Level);
                }
            }
            if (added.Count > 0)
            {
                foreach (int v in added) corners[v] = true;
                planLimit = planTolerance * .25;
                continue;
            }
            double[] sampled = samples.ToArray();
            if (region != null)
            {
                double kept = region.Clip(sampled, false).Sum(part => PlanLength(part.Points));
                if (PlanLength(sampled) - kept > planTolerance * 1e-4) return null;
            }
            double[] distinct = closed ? sampled[..^3] : sampled;
            double[] light = closed
                ? SimplifyClosed(distinct, planTolerance * .05, verticalTolerance * .05, terrainAt)
                : SimplifyOpen(distinct, planTolerance * .05, verticalTolerance * .05, terrainAt);
            for (int i = 2; i < light.Length; i += 3) light[i] = contour.Level;
            return new RecoveredContour
            {
                Level = contour.Level, PointsXyz = light, IsClosed = closed,
                Curve = new FittedRouteCurve(pieces, sampled, deviation, 0, 0, null, default)
            };
        }

        // Vertex runs from corner to corner; a closed contour without corners is one seamed run (empty).
        List<int[]> Runs()
        {
            int first = Array.IndexOf(corners, true);
            if (closed && first < 0) return new List<int[]> { Array.Empty<int>() };
            var order = closed
                ? Enumerable.Range(0, n + 1).Select(i => (first + i) % n).ToList()
                : Enumerable.Range(0, n).ToList();
            var runs = new List<int[]>();
            var current = new List<int> { order[0] };
            for (int i = 1; i < order.Count; i++)
            {
                current.Add(order[i]);
                if (corners[order[i]] || i == order.Count - 1) { runs.Add(current.ToArray()); current = new List<int> { order[i] }; }
            }
            return runs;
        }
    }

    private static FittedRouteCurve AtElevation(FittedRouteCurve curve, double level)
    {
        var pieces = curve.Pieces.Select(piece =>
        {
            double[] controls = (double[])piece.ControlPoints.Clone();
            for (int i = 2; i < controls.Length; i += 3) controls[i] = level;
            return new RouteSplineCurve(piece.Degree, (double[])piece.Knots.Clone(), controls);
        }).ToArray();
        double[] samples = (double[])curve.PointsXyz.Clone();
        for (int i = 2; i < samples.Length; i += 3) samples[i] = level;
        return new FittedRouteCurve(pieces, samples, curve.MaxPlanDeviation, 0, 0, curve.SmallestRadius, curve.Complexity);
    }
}
