using MoleHill.Core.Processing;

namespace MoleHill.Core.Scan;

internal static partial class ScanAlignmentFitter
{
    private static bool FitClosedPair(double[] top, double[] toe, double cell, double plan, double height,
        CancellationToken token, out ScanCurveGeometry high, out ScanCurveGeometry low)
    {
        high = low = null!;
        if (top.Length != toe.Length || top.Length < 12) return false;
        int middle = (top.Length / 3 - 1) / 2, lastNode = top.Length / 3 - 1;
        if (TryPlan(top, 0, middle, plan, out var arcA, out _) && arcA.Arc &&
            TryPlan(top, middle, lastNode, plan, out var arcB, out _) && arcB.Arc &&
            Distance(arcA.Cx - arcB.Cx, arcA.Cy - arcB.Cy) <= plan && Math.Abs(arcA.Radius - arcB.Radius) <= plan &&
            FitPair(top[..((middle + 1) * 3)], toe[..((middle + 1) * 3)], false, cell, plan, height, token, out var ah, out var al) &&
            FitPair(top[(middle * 3)..], toe[(middle * 3)..], false, cell, plan, height, token, out var bh, out var bl))
        {
            high = new ScanCurveGeometry(ah.Pieces.Concat(bh.Pieces).ToArray(), plan, height);
            low = new ScanCurveGeometry(al.Pieces.Concat(bl.Pieces).ToArray(), plan, height);
            return Simple(high.PointsXyz, true) && Simple(low.PointsXyz, true);
        }
        var centre = top.Zip(toe, (a, b) => (a + b) * .5).ToArray()[..^3];
        for (int i = 2; i < centre.Length; i += 3) centre[i] = 0;
        var corners = TerrainCurveRecovery.SimplifyClosed(centre, Math.Max(plan * .7, cell * .75), double.PositiveInfinity);
        int n = corners.Length / 3;
        if (n < 3) return false;
        var indices = new int[n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < centre.Length; i += 3)
                if (centre[i] == corners[j * 3] && centre[i + 1] == corners[j * 3 + 1]) { indices[j] = i / 3; break; }
        ScanCurveGeometry? Rail(double[] raw)
        {
            var supports = new List<(double S, double N, double Z)>[n];
            var lines = new (double X, double Y, double Dx, double Dy, double Z, double Grade)[n];
            for (int j = 0; j < n; j++)
            {
                token.ThrowIfCancellationRequested();
                int next = (j + 1) % n;
                double dx = corners[next * 3] - corners[j * 3], dy = corners[next * 3 + 1] - corners[j * 3 + 1];
                double length = Distance(dx, dy); dx /= length; dy /= length;
                var samples = new List<(double S, double N, double Z)>();
                for (int i = indices[j]; ; i = (i + 1) % (raw.Length / 3 - 1))
                {
                    double x = raw[i * 3] - corners[j * 3], y = raw[i * 3 + 1] - corners[j * 3 + 1];
                    samples.Add((x * dx + y * dy, -x * dy + y * dx, raw[i * 3 + 2]));
                    if (i == indices[next]) break;
                }
                supports[j] = samples;
                double offset = samples.Select(p => p.N).OrderBy(x => x).ElementAt(samples.Count / 2);
                if (samples.Count(p => Math.Abs(p.N - offset) <= plan) < .9 * Math.Max(1, samples.Count - 2)) return null;
                double meanS = samples.Average(p => p.S), meanZ = samples.Average(p => p.Z);
                double variance = samples.Sum(p => Math.Pow(p.S - meanS, 2));
                double grade = variance > 0 ? samples.Sum(p => (p.S - meanS) * (p.Z - meanZ)) / variance : 0;
                lines[j] = (corners[j * 3] - dy * offset, corners[j * 3 + 1] + dx * offset, dx, dy, meanZ - grade * meanS, grade);
            }
            double Profile(int side, double station)
            {
                var points = supports[side].GroupBy(p => Math.Round(p.S, 10)).Select(g => (S: g.First().S, Z: g.Average(p => p.Z))).OrderBy(p => p.S).ToArray();
                if (points.Length == 1) return points[0].Z;
                int k = Array.FindIndex(points, p => p.S >= station);
                k = Math.Clamp(k < 0 ? points.Length - 1 : k, 1, points.Length - 1);
                return points[k - 1].Z + (points[k].Z - points[k - 1].Z) * (station - points[k - 1].S) / (points[k].S - points[k - 1].S);
            }
            var vertices = new double[(n + 1) * 3];
            for (int j = 0; j < n; j++)
            {
                var a = lines[(j + n - 1) % n]; var b = lines[j];
                double cross = a.Dx * b.Dy - a.Dy * b.Dx;
                if (Math.Abs(cross) < 1e-8) return null;
                double t = ((b.X - a.X) * b.Dy - (b.Y - a.Y) * b.Dx) / cross;
                double x = a.X + t * a.Dx, y = a.Y + t * a.Dy;
                if (Distance(x - corners[j * 3], y - corners[j * 3 + 1]) > cell * 3 + plan) return null;
                double z = (Profile(j, (x - b.X) * b.Dx + (y - b.Y) * b.Dy) +
                    Profile((j + n - 1) % n, (x - a.X) * a.Dx + (y - a.Y) * a.Dy)) * .5;
                vertices[j * 3] = x; vertices[j * 3 + 1] = y; vertices[j * 3 + 2] = z;
            }
            Array.Copy(vertices, 0, vertices, n * 3, 3);
            var pieces = new List<ScanCurvePiece>();
            for (int j = 0; j < n; j++)
            {
                var start = new[] { vertices[j * 3], vertices[j * 3 + 1], vertices[j * 3 + 2] };
                var end = new[] { vertices[(j + 1) * 3], vertices[(j + 1) * 3 + 1], vertices[(j + 1) * 3 + 2] };
                var axis = new Plan(start[0], start[1], end[0], end[1]);
                var samples = new List<double>(start);
                int next = (j + 1) % n;
                for (int i = indices[j]; ; i = (i + 1) % (raw.Length / 3 - 1))
                {
                    double t = axis.Station(raw[i * 3], raw[i * 3 + 1]);
                    if (t > 1e-8 && t < 1 - 1e-8) samples.AddRange(raw.Skip(i * 3).Take(3));
                    if (i == indices[next]) break;
                }
                samples.AddRange(end);
                pieces.AddRange(Lift(samples.ToArray(), 0, samples.Count / 3 - 1, axis, cell, plan, height, token));
            }
            return new ScanCurveGeometry(pieces, plan, height);
        }
        var h = Rail(top); var l = Rail(toe);
        if (h == null || l == null || !Simple(h.PointsXyz, true) || !Simple(l.PointsXyz, true)) return false;
        high = h; low = l; return true;
    }
}
