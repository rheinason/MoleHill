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
    /// One quadratic B-spline whose controls are the simplified contour's own vertices: as many controls as the
    /// polyline, smooth everywhere, one piece. A closed contour is seamed at the midpoint of its last leg so it
    /// stays smooth across the seam. Fitting a general spline to the simplified polyline instead spent a control
    /// on every corner and joined many pieces. Null when a corner cut leaves the plan tolerance, the level's
    /// vertical tolerance or the region.
    /// </summary>
    private static RecoveredContour? SmoothContour(RecoveredContour contour, double planTolerance, double verticalTolerance,
        Func<double, double, double?> terrainAt, ITerrainRecoveryRegion? region)
    {
        double[] p = contour.PointsXyz; int n = p.Length / 3;
        if (n < 3) return null;
        var xy = new List<(double X, double Y)>();
        if (contour.IsClosed)
        {
            var seam = ((p[^3] + p[0]) * .5, (p[^2] + p[1]) * .5);
            xy.Add(seam);
            for (int i = 0; i < n; i++) xy.Add((p[i * 3], p[i * 3 + 1]));
            xy.Add(seam);
        }
        else
            for (int i = 0; i < n; i++) xy.Add((p[i * 3], p[i * 3 + 1]));
        int controls = xy.Count, spans = controls - 2;
        var knots = new double[controls + 3];
        for (int i = 0; i < knots.Length; i++) knots[i] = Math.Clamp(i - 2, 0, spans) / (double)spans;
        var points = new double[controls * 3];
        for (int i = 0; i < controls; i++) { points[i * 3] = xy[i].X; points[i * 3 + 1] = xy[i].Y; points[i * 3 + 2] = contour.Level; }
        var spline = new RouteSplineCurve(2, knots, points);
        var samples = new List<double>();
        double deviation = 0;
        int count = spans * 8;
        for (int i = 0; i <= count; i++)
        {
            var (x, y, _) = spline.PointAt(i / (double)count);
            deviation = Math.Max(deviation, PlanDistanceToPolyline(p, contour.IsClosed, x, y));
            if (deviation > planTolerance || terrainAt(x, y) is not double height || Math.Abs(height - contour.Level) > verticalTolerance)
                return null;
            samples.Add(x); samples.Add(y); samples.Add(contour.Level);
        }
        double[] sampled = samples.ToArray();
        if (region != null)
        {
            double kept = region.Clip(sampled, false).Sum(part => PlanLength(part.Points));
            if (PlanLength(sampled) - kept > planTolerance * 1e-4) return null;
        }
        double[] distinct = contour.IsClosed ? sampled[..^3] : sampled;
        double[] light = contour.IsClosed
            ? SimplifyClosed(distinct, planTolerance * .05, verticalTolerance * .05, terrainAt)
            : SimplifyOpen(distinct, planTolerance * .05, verticalTolerance * .05, terrainAt);
        for (int i = 2; i < light.Length; i += 3) light[i] = contour.Level;
        return new RecoveredContour
        {
            Level = contour.Level, PointsXyz = light, IsClosed = contour.IsClosed,
            Curve = new FittedRouteCurve(new[] { spline }, sampled, deviation, 0, 0, null, default)
        };
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
