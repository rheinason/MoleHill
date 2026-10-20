using MoleHill.Core.Geometry;
using MoleHill.Core.Processing;
using MoleHill.Core.Routing;

namespace MoleHill.Core.Scan;

/// <summary>Fits supported line/arc plans, then lifts their profiles without moving the plan.</summary>
internal static partial class ScanAlignmentFitter
{
    internal readonly record struct Plan(double Ax, double Ay, double Bx, double By,
        bool Arc = false, double Cx = 0, double Cy = 0, double Angle = 0, double Sweep = 0)
    {
        public double Radius => Distance(Ax - Cx, Ay - Cy);
        public double Length => Arc ? Radius * Math.Abs(Sweep) : Distance(Bx - Ax, By - Ay);
        public (double X, double Y) At(double t) => Arc
            ? (Cx + Radius * Math.Cos(Angle + Sweep * t), Cy + Radius * Math.Sin(Angle + Sweep * t))
            : (Ax + (Bx - Ax) * t, Ay + (By - Ay) * t);
        public double Station(double x, double y)
        {
            if (!Arc) return ((x - Ax) * (Bx - Ax) + (y - Ay) * (By - Ay)) / (Length * Length);
            double a = Math.Atan2(y - Cy, x - Cx) - Angle;
            while (a * Math.Sign(Sweep) < -.1) a += Math.Sign(Sweep) * Math.PI * 2;
            return a / Sweep;
        }
    }

    public static ScanCurveGeometry? Fit(double[] raw, bool closed, double cell, double planTolerance,
        double heightTolerance, CancellationToken token = default, Func<double, double, double?>? heightAt = null)
    {
        if (raw.Length < 6) return null;
        var ranges = Ranges(raw, closed, planTolerance);
        var pieces = new List<ScanCurvePiece>();
        double supported = 0;
        foreach (var (first, last) in ranges)
        {
            token.ThrowIfCancellationRequested();
            if (!TryPlan(raw, first, last, planTolerance, out Plan plan, out double share)) return null;
            pieces.AddRange(Lift(raw, first, last, plan, cell, planTolerance, heightTolerance, token, heightAt));
            supported += share * plan.Length;
        }
        if (pieces.Count == 0) return null;
        return new ScanCurveGeometry(pieces, planTolerance, heightTolerance,
            Math.Clamp(supported / Math.Max(ranges.Sum(r => Chord(raw, r.First, r.Last)), cell * 1e-9), 0, 1));
    }

    public static bool FitPair(double[] top, double[] toe, bool closed, double cell, double planTolerance,
        double heightTolerance, CancellationToken token, out ScanCurveGeometry high, out ScanCurveGeometry low, Action<string>? rejected = null)
    {
        high = low = null!;
        if (closed)
        {
            bool accepted = FitClosedPair(top, toe, cell, planTolerance, heightTolerance, token, out high, out low);
            if (!accepted) rejected?.Invoke("Unsupported closed rail alignment");
            return accepted;
        }
        if (top.Length != toe.Length || top.Length < 6) return Reject("Unpaired observations");
        var centre = top.Zip(toe, (a, b) => (a + b) * .5).ToArray();
        var ranges = Ranges(centre, closed, planTolerance);
        double length = 0;
        var highPieces = new List<ScanCurvePiece>(); var lowPieces = new List<ScanCurvePiece>();
        double minWidth = cell * .02;
        foreach (var (first, last) in ranges)
        {
            token.ThrowIfCancellationRequested();
            if (!TryPlan(centre, first, last, planTolerance, out var axis, out _) ||
                !TryPlan(top, first, last, planTolerance, out var hp, out _) ||
                !TryPlan(toe, first, last, planTolerance, out var lp, out _) || hp.Arc != lp.Arc) return Reject($"Incompatible rail alignment at {first}..{last}");
            // Corresponding rails must keep their side and progress in the same direction.
            double sign = 0;
            for (int j = 0; j <= 32; j++)
            {
                double t = j / 32.0;
                var h = hp.At(t); var l = lp.At(t);
                var a = axis.At(Math.Max(0, t - .01)); var b = axis.At(Math.Min(1, t + .01));
                double cross = (b.X - a.X) * (h.Y - l.Y) - (b.Y - a.Y) * (h.X - l.X);
                if (Distance(h.X - l.X, h.Y - l.Y) < minWidth || sign * cross < -cell * cell * 1e-8) return Reject("Rail order or separation");
                if (Math.Abs(cross) > cell * cell * 1e-9) sign = Math.Sign(cross);
            }
            length += axis.Length;
            highPieces.AddRange(Lift(top, first, last, hp, cell, planTolerance, heightTolerance, token));
            lowPieces.AddRange(Lift(toe, first, last, lp, cell, planTolerance, heightTolerance, token));
        }
        // A drawing needing a corner every few cells is still a raster trace, not a recovered wall.
        if (ranges.Count > Math.Max(4, length / Math.Max(cell * 4, planTolerance * 4))) return Reject("Excessive plan complexity");
        high = new ScanCurveGeometry(highPieces, planTolerance, heightTolerance);
        low = new ScanCurveGeometry(lowPieces, planTolerance, heightTolerance);
        return Simple(high.PointsXyz, closed) && Simple(low.PointsXyz, closed) || Reject("Self-intersecting rail");
        bool Reject(string reason) { rejected?.Invoke(reason); return false; }
    }

    private static List<(int First, int Last)> Ranges(double[] raw, bool closed, double tolerance)
    {
        int count = raw.Length / 3;
        if (!closed && TryPlan(raw, 0, count - 1, tolerance, out _, out _)) return new() { (0, count - 1) };
        var flat = (double[])raw.Clone();
        for (int i = 2; i < flat.Length; i += 3) flat[i] = 0;
        double[] simple = closed
            ? TerrainCurveRecovery.SimplifyClosed(flat[..^3], tolerance * .7, double.PositiveInfinity)
            : TerrainCurveRecovery.SimplifyOpen(flat, tolerance * .7, double.PositiveInfinity);
        var lookup = new Dictionary<(double X, double Y), int>();
        for (int i = 0; i < count - (closed ? 1 : 0); i++) lookup.TryAdd((raw[i * 3], raw[i * 3 + 1]), i);
        var indices = Enumerable.Range(0, simple.Length / 3)
            .Select(i => lookup[(simple[i * 3], simple[i * 3 + 1])]).Distinct().OrderBy(i => i).ToList();
        if (indices.Count == 0 || indices[0] != 0) indices.Insert(0, 0);
        if (indices[^1] != count - 1) indices.Add(count - 1);
        var result = new List<(int, int)>();
        int start = indices[0];
        for (int k = 1; k < indices.Count; k++)
        {
            // Merge consecutive thinned supports whenever one primitive explains them.
            if (k + 1 < indices.Count && TryPlan(raw, start, indices[k + 1], tolerance, out _, out _)) continue;
            result.Add((start, indices[k])); start = indices[k];
        }
        return result;
    }

    internal static bool TryPlan(double[] raw, int first, int last, double tolerance, out Plan plan, out double share)
    {
        double ax = raw[first * 3], ay = raw[first * 3 + 1], bx = raw[last * 3], by = raw[last * 3 + 1];
        plan = new(ax, ay, bx, by); share = 0;
        double length = plan.Length;
        if (length <= tolerance * 1e-7) return false;
        int hits = 0;
        for (int i = first; i <= last; i++)
        {
            double t = plan.Station(raw[i * 3], raw[i * 3 + 1]);
            double d = Math.Abs((raw[i * 3] - ax) * (by - ay) - (raw[i * 3 + 1] - ay) * (bx - ax)) / length;
            if (d <= tolerance && t >= -tolerance / length && t <= 1 + tolerance / length) hits++;
        }
        share = hits / (double)(last - first + 1);
        if (share >= .9) return true;
        // A circle through the held endpoints: solve its centre on the perpendicular bisector.
        double mx = (ax + bx) * .5, my = (ay + by) * .5, nx = -(by - ay) / length, ny = (bx - ax) / length;
        double numerator = 0, denominator = 0;
        for (int i = first + 1; i < last; i++)
        {
            double x = raw[i * 3] - mx, y = raw[i * 3 + 1] - my, n = x * nx + y * ny;
            numerator += n * (x * x + y * y - length * length * .25); denominator += 2 * n * n;
        }
        if (denominator < length * length * 1e-12) return false;
        double h = numerator / denominator, cx = mx + h * nx, cy = my + h * ny;
        double start = Math.Atan2(ay - cy, ax - cx), sweep = Math.Atan2(by - cy, bx - cx) - start;
        while (sweep > Math.PI) sweep -= Math.PI * 2;
        while (sweep < -Math.PI) sweep += Math.PI * 2;
        // Endpoints alone cannot choose the direction of a semicircle. Follow the
        // observed traversal, including its wrap across the angular seam.
        double observedSweep = 0, previous = start;
        for (int i = first + 1; i <= last; i++)
        {
            double angle = Math.Atan2(raw[i * 3 + 1] - cy, raw[i * 3] - cx);
            double step = angle - previous;
            while (step > Math.PI) step -= Math.PI * 2;
            while (step < -Math.PI) step += Math.PI * 2;
            observedSweep += step;
            previous = angle;
        }
        if (Math.Abs(sweep - observedSweep) > Math.PI)
            sweep += Math.Sign(observedSweep) * Math.PI * 2;
        plan = new(ax, ay, bx, by, true, cx, cy, start, sweep);
        if (Math.Abs(sweep) < 1e-6 || plan.Length > length * 2) return false;
        hits = 0;
        for (int i = first; i <= last; i++)
        {
            double t = plan.Station(raw[i * 3], raw[i * 3 + 1]);
            if (Math.Abs(Distance(raw[i * 3] - cx, raw[i * 3 + 1] - cy) - plan.Radius) <= tolerance &&
                t >= -.05 && t <= 1.05) hits++;
        }
        share = hits / (double)(last - first + 1);
        return share >= .9;
    }

    internal static IEnumerable<ScanCurvePiece> Lift(double[] raw, int first, int last, Plan plan,
        double cell, double planTolerance, double heightTolerance, CancellationToken token, Func<double, double, double?>? heightAt = null)
    {
        var profile = Enumerable.Range(first, last - first + 1)
            .Select(i => (T: Math.Clamp(plan.Station(raw[i * 3], raw[i * 3 + 1]), 0, 1), Z: raw[i * 3 + 2]))
            .OrderBy(p => p.T).GroupBy(p => Math.Round(p.T, 10))
            .Select(g => (T: g.First().T, Z: g.Average(p => p.Z))).ToArray();
        if (profile.Length < 2) yield break;
        if (heightAt != null)
        {
            int steps = Math.Max(2, (int)Math.Ceiling(plan.Length / (cell * .5)));
            var sampled = new List<(double T, double Z)>();
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps; var p = plan.At(t);
                if (heightAt(p.X, p.Y) is double z && double.IsFinite(z)) sampled.Add((t, z));
            }
            if (sampled.Count >= 2) profile = sampled.ToArray();
        }
        double Height(double t)
        {
            int j = Array.FindIndex(profile, p => p.T >= t);
            if (j <= 0) return j == 0 ? profile[0].Z : profile[^1].Z;
            double s = (t - profile[j - 1].T) / (profile[j].T - profile[j - 1].T);
            return profile[j - 1].Z + (profile[j].Z - profile[j - 1].Z) * s;
        }
        if (!plan.Arc)
        {
            double[] xyz = profile.SelectMany(p => new[] { p.T * plan.Length, 0.0, p.Z }).ToArray();
            var fitted = RouteCurveFitter.Fit(new IReadOnlyList<double>[] { xyz }, Math.Min(cell, planTolerance) * .01, heightTolerance, 0, 0, 0);
            foreach (var piece in fitted.Pieces)
            {
                var points = (double[])piece.ControlPoints.Clone();
                for (int i = 0; i < points.Length; i += 3)
                { var p = plan.At(points[i] / plan.Length); points[i] = p.X; points[i + 1] = p.Y; }
                yield return new ScanCurvePiece(piece.Degree, piece.Knots, points);
            }
            yield break;
        }
        var queue = new Stack<(double A, double B, int Depth)>(); queue.Push((0, 1, 0));
        while (queue.TryPop(out var interval))
        {
            token.ThrowIfCancellationRequested();
            double a = interval.A, b = interval.B, mid = (a + b) * .5;
            double half = plan.Sweep * (b - a) * .5, weight = Math.Cos(half);
            if (Math.Abs(half) > Math.PI / 4 + 1e-12)
            {
                queue.Push((mid, b, interval.Depth + 1));
                queue.Push((a, mid, interval.Depth + 1));
                continue;
            }
            var p = plan.At(a); var q = plan.At(b); var m = plan.At(mid);
            double z0 = Height(a), z1 = Height(b), zm = Height(mid);
            var piece = new ScanCurvePiece(2, new double[] { 0, 0, 0, 1, 1, 1 }, new[] {
                p.X, p.Y, z0, plan.Cx + (m.X - plan.Cx) / weight, plan.Cy + (m.Y - plan.Cy) / weight,
                ((2 + 2 * weight) * zm - z0 - z1) / (2 * weight), q.X, q.Y, z1 }, new[] { 1.0, weight, 1.0 });
            bool fits = true;
            for (int j = 1; j < 32 && fits; j++)
            {
                var v = piece.PointAt(j / 32.0);
                fits = Math.Abs(v.Z - Height(plan.Station(v.X, v.Y))) <= heightTolerance;
            }
            if (fits || interval.Depth >= 14) yield return piece;
            else { queue.Push((mid, b, interval.Depth + 1)); queue.Push((a, mid, interval.Depth + 1)); }
        }
    }
    private static double Chord(double[] p, int a, int b) => Distance(p[b * 3] - p[a * 3], p[b * 3 + 1] - p[a * 3 + 1]);
    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
    private static bool Simple(double[] xyz, bool closed)
    {
        var xy = xyz.Where((_, i) => i % 3 != 2).ToArray();
        if (closed) return !Geometry2D.ClosedPolylineSelfIntersects(xy[..^2], xy.Length / 2 - 1);
        for (int i = 0; i + 5 < xyz.Length; i += 3)
            for (int j = i + 6; j + 5 < xyz.Length; j += 3)
                if (Geometry2D.SegmentsTouch(xyz[i], xyz[i + 1], xyz[i + 3], xyz[i + 4], xyz[j], xyz[j + 1], xyz[j + 3], xyz[j + 4])) return false;
        return true;
    }
}
