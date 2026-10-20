using MoleHill.Core.Analysis;
using MoleHill.Core.Routing;

namespace MoleHill.Core.Scan;

internal static partial class ScanBoundaryFitter
{
    private static double[] SmoothPlan(double[] raw, bool closed, double cell, double tolerance)
    {
        int count = raw.Length / 3;
        var result = (double[])raw.Clone();
        int reach = (int)Math.Min(count / 2, Math.Min(128, Math.Ceiling(3 * tolerance / cell)));
        var held = new bool[count];
        int support = Math.Min(4, count / 4);
        if (support >= 2)
            for (int corner = 0; corner < count; corner++)
            {
                if (!closed && (corner < support || corner + support >= count)) continue;
                int a = (corner - support + count) % count, b = (corner + support) % count;
                double ax = raw[corner * 3] - raw[a * 3], ay = raw[corner * 3 + 1] - raw[a * 3 + 1];
                double bx = raw[b * 3] - raw[corner * 3], by = raw[b * 3 + 1] - raw[corner * 3 + 1];
                double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
                if (la < cell * (support - .1) || lb < cell * (support - .1) ||
                    (ax * bx + ay * by) > .8 * la * lb) continue;
                bool straight = true;
                for (int j = 1; j < support; j++)
                {
                    int p = (corner - j + count) % count, q = (corner + j) % count;
                    straight &= Math.Abs((raw[p * 3] - raw[corner * 3]) * ay - (raw[p * 3 + 1] - raw[corner * 3 + 1]) * ax) < cell * la * 1e-6;
                    straight &= Math.Abs((raw[q * 3] - raw[corner * 3]) * by - (raw[q * 3 + 1] - raw[corner * 3 + 1]) * bx) < cell * lb * 1e-6;
                }
                if (straight) for (int j = -reach; j <= reach; j++) held[(corner + j + count) % count] = true;
            }
        for (int i = closed ? 0 : 1; i < (closed ? count : count - 1); i++)
        {
            if (held[i]) continue;
            double x = 0, y = 0, weight = 0;
            for (int offset = -reach; offset <= reach; offset++)
            {
                int index = i + offset;
                if (!closed && (index < 0 || index >= count)) continue;
                index = (index + count) % count;
                double w = reach + 1 - Math.Abs(offset);
                x += w * raw[index * 3]; y += w * raw[index * 3 + 1]; weight += w;
            }
            double dx = x / weight - raw[i * 3], dy = y / weight - raw[i * 3 + 1];
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double share = distance > 0 ? Math.Min(1, tolerance * .4 / distance) : 0;
            result[i * 3] += dx * share; result[i * 3 + 1] += dy * share;
        }
        return result;
    }

    private static FittedRouteCurve FitStraightPlan(double[] plan, ScanGrid ground, MeshHeightProjector? projector,
        double planTolerance, double heightTolerance, CancellationToken token)
    {
        var pieces = new List<RouteSplineCurve>();
        var samples = new List<double>();
        double heightError = 0, grade = 0;
        for (int i = 0; i + 5 < plan.Length; i += 3)
        {
            token.ThrowIfCancellationRequested();
            double x = plan[i], y = plan[i + 1], dx = plan[i + 3] - x, dy = plan[i + 4] - y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= ground.CellSize * 1e-9) continue;
            int steps = Math.Max(2, (int)Math.Ceiling(length / (ground.CellSize * .5)));
            var profile = new List<double>();
            for (int j = 0; j <= steps; j++)
            {
                double t = (double)j / steps, px = x + dx * t, py = y + dy * t;
                if (!(projector != null && projector.TryGetHeightExtremes(px, py, ground.CellSize * 1e-7, out _, out double z)))
                {
                    px = Math.Clamp(px, ground.OriginX, ground.NodeX(ground.Columns - 1));
                    py = Math.Clamp(py, ground.OriginY, ground.NodeY(ground.Rows - 1));
                    if (!ScanGridSampling.TryHeight(ground, px, py, out z))
                        throw new InvalidOperationException("No ground height along the cleaned road edge.");
                }
                profile.Add(t * length); profile.Add(0); profile.Add(z);
            }
            // Fit station/elevation, then map station back onto the held XY segment. New height knots
            // add controls on that same line; no XYZ refit can move the road sideways.
            FittedRouteCurve fit = RouteCurveFitter.Fit(new IReadOnlyList<double>[] { profile },
                Math.Min(planTolerance, ground.CellSize) * .01, heightTolerance, 0, 0, 0);
            foreach (var piece in fit.Pieces)
                pieces.Add(new RouteSplineCurve(piece.Degree, (double[])piece.Knots.Clone(), Map(piece.ControlPoints)));
            double[] mapped = Map(fit.PointsXyz);
            samples.AddRange(samples.Count == 0 ? mapped : mapped.Skip(3));
            heightError = Math.Max(heightError, fit.MaxVerticalDeviation); grade = Math.Max(grade, fit.MaxGrade);

            double[] Map(double[] stationZ)
            {
                var xyz = (double[])stationZ.Clone();
                for (int k = 0; k < xyz.Length; k += 3)
                {
                    double t = xyz[k] / length;
                    xyz[k] = t >= 1 ? plan[i + 3] : t <= 0 ? x : x + dx * t;
                    xyz[k + 1] = t >= 1 ? plan[i + 4] : t <= 0 ? y : y + dy * t;
                }
                return xyz;
            }
        }
        return new FittedRouteCurve(pieces, samples.ToArray(), planTolerance * .95, heightError, grade, null, default);
    }
}
