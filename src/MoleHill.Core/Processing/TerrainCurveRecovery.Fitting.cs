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
                if (terrainAt(samples[i], samples[i + 1]) is not double height || Math.Abs(height - contour.Level) > verticalTolerance ||
                    region != null && !region.Contains(samples[i], samples[i + 1]))
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
