using MoleHill.Core.Geometry;

namespace MoleHill.Core.Scan;

/// <summary>
/// Builds area outlines directly from a clean dense trace: built edges as lines and circular arcs, natural
/// edges as a quadratic spline on their thinned vertices. Heights are draped from the terrain afterwards, so
/// they never bend the plan. Fitting a general spline to the trace spent most of the scan's time and gave a
/// cubic for every straight run.
/// </summary>
internal static class ScanOutlineFitter
{
    /// <summary>
    /// Corners are found at a scale of four plan tolerances (two cells at least) and kept as hard joins; between them the trace is
    /// smoothed at that scale before fitting. A classified edge wanders by about the plan tolerance (a hedge, a
    /// shadow, a car), so fitting it raw at that tolerance drew every wander as a wobble or a short zig-zag,
    /// while a spline still rounded the real corners it could not see. Long straight runs are held as lines
    /// on every outline, not only paved ones: a lawn ends at a facade, a wall or a bed edge as often as at paving.
    /// </summary>
    public static ScanCurveGeometry Fit(double[] dense, bool closed, bool built, double cell, double planTolerance,
        double heightTolerance, Func<double, double, double?> heightAt, CancellationToken token)
    {
        if (dense.Length < 6)
            throw new ArgumentException("An outline needs two points.", nameof(dense));
        // A loop round a single lattice cell resamples to its own start: kept as drawn, and dropped as an area of none.
        if (dense.Length / 3 - (closed ? 1 : 0) < 2)
            return new ScanCurveGeometry(new[] { Polyline(dense, Enumerable.Range(0, dense.Length / 3).ToArray()) }, planTolerance, heightTolerance);
        // The trace's own rounding spans about a cell, so corners are looked for no finer than two.
        var runs = Fair(dense, closed, Math.Max(planTolerance * 4, cell * 2), heightAt);
        var pieces = runs.SelectMany(part => FitPart(part.Points, part.Closed, built, cell, planTolerance, heightTolerance, heightAt, token)).ToList();
        return new ScanCurveGeometry(pieces, planTolerance, heightTolerance);
    }

    /// <summary>
    /// Splits the trace at corners (a turn of 50 degrees or more across the scale, the sharpest in its
    /// neighbourhood) and smooths each run with a Gaussian of half the scale along its length. The trace is
    /// rounded at a corner by its own averaging, wider than the tolerance on a coarse lattice, so a corner
    /// moves to where lines fitted to the trace beyond the rounding cross, and the rounded samples move onto
    /// those lines. Open ends stay exactly where they are: they are junctions shared with other outlines.
    /// </summary>
    private static List<(double[] Points, bool Closed)> Fair(double[] dense, bool closed, double scale,
        Func<double, double, double?> heightAt)
    {
        int count = dense.Length / 3 - (closed ? 1 : 0);
        double length = 0;
        for (int i = 0; i + 1 < dense.Length / 3; i++)
            length += Math.Sqrt(Geometry2D.DistanceSquared(dense[i * 3], dense[i * 3 + 1], dense[i * 3 + 3], dense[i * 3 + 4]));
        double spacing = length / Math.Max(1, dense.Length / 3 - 1);
        int reach = (int)Math.Round(scale / Math.Max(spacing, 1e-12));
        if (reach < 2 || count < 4 * reach)
            return new() { (dense, closed) };
        // Turning angle across the scale at each sample.
        var turn = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (!closed && (i < reach || i + reach >= count)) continue;
            int a = ((i - reach) % count + count) % count, b = (i + reach) % count;
            double ux = X(i) - X(a), uy = Y(i) - Y(a), vx = X(b) - X(i), vy = Y(b) - Y(i);
            turn[i] = Math.Abs(Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy));
        }
        var corners = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (turn[i] < CornerAngle) continue;
            bool sharpest = true;
            for (int k = -reach; k <= reach && sharpest; k++)
            {
                if (k == 0) continue;
                int j = i + k;
                if (closed) j = (j % count + count) % count;
                else if (j < 0 || j >= count) continue;
                sharpest = turn[j] < turn[i] || turn[j] == turn[i] && j > i;
            }
            if (sharpest) corners.Add(i);
        }
        if (corners.Count > 0) dense = (double[])dense.Clone();
        foreach (int corner in corners) Square(corner);
        if (closed) Array.Copy(dense, 0, dense, count * 3, 3);
        var parts = new List<(double[] Points, bool Closed)>();
        if (closed && corners.Count == 0)
        {
            parts.Add((Smooth(Enumerable.Range(0, count + 1).ToArray(), true), true));
            return parts;
        }
        // Runs between corners (an open trace also breaks at its ends); a closed loop starts at its first corner.
        var stops = new List<int>();
        if (closed) { stops.AddRange(corners); stops.Add(corners[0] + count); }
        else { stops.Add(0); stops.AddRange(corners); stops.Add(count - 1); }
        for (int r = 0; r + 1 < stops.Count; r++)
            parts.Add((Smooth(Enumerable.Range(stops[r], stops[r + 1] - stops[r] + 1).ToArray(), false), false));
        return parts;

        double X(int i) => dense[(i % count) * 3];
        double Y(int i) => dense[(i % count) * 3 + 1];
        int Index(int i) => closed ? (i % count + count) % count : i;
        void Square(int i)
        {
            int near = Math.Max(1, reach / 2);
            if (!closed && (i - reach < 0 || i + reach >= count)) return;
            var before = Line(i - reach, i - near); var after = Line(i + near, i + reach);
            double cross = before.Dx * after.Dy - before.Dy * after.Dx;
            if (Math.Abs(cross) < .5) return;
            double t = ((after.X - before.X) * after.Dy - (after.Y - before.Y) * after.Dx) / cross;
            double px = before.X + before.Dx * t, py = before.Y + before.Dy * t;
            if (Geometry2D.DistanceSquared(px, py, X(Index(i)), Y(Index(i))) > scale * scale * .25) return;
            Place(i, px, py);
            // Rounded samples move onto the lines, never past the corner: one behind it would fold the run back.
            for (int k = 1; k < near; k++)
            {
                Project(i - k, before, px, py, X(Index(i - reach)), Y(Index(i - reach)));
                Project(i + k, after, px, py, X(Index(i + reach)), Y(Index(i + reach)));
            }
        }
        (double X, double Y, double Dx, double Dy) Line(int from, int to)
        {
            double mx = 0, my = 0; int n = to - from + 1;
            for (int j = from; j <= to; j++) { mx += X(Index(j)); my += Y(Index(j)); }
            mx /= n; my /= n;
            double sxx = 0, sxy = 0, syy = 0;
            for (int j = from; j <= to; j++)
            {
                double dx = X(Index(j)) - mx, dy = Y(Index(j)) - my;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }
            double angle = .5 * Math.Atan2(2 * sxy, sxx - syy);
            return (mx, my, Math.Cos(angle), Math.Sin(angle));
        }
        void Project(int i, (double X, double Y, double Dx, double Dy) line, double cornerX, double cornerY, double awayX, double awayY)
        {
            // Stations along the line measured from the corner, positive towards the far end of the run.
            double sign = (awayX - cornerX) * line.Dx + (awayY - cornerY) * line.Dy >= 0 ? 1 : -1;
            double t = Math.Max(0, ((X(Index(i)) - cornerX) * line.Dx + (Y(Index(i)) - cornerY) * line.Dy) * sign);
            Place(i, cornerX + line.Dx * sign * t, cornerY + line.Dy * sign * t);
        }
        void Place(int i, double x, double y)
        {
            int at = Index(i) * 3;
            dense[at] = x; dense[at + 1] = y;
            if (heightAt(x, y) is double z && double.IsFinite(z)) dense[at + 2] = z;
        }
        double[] Smooth(int[] run, bool loop)
        {
            int n = run.Length - (loop ? 1 : 0);
            double sigma = reach * .5;
            int window = (int)Math.Ceiling(sigma * 2.5);
            var result = new double[run.Length * 3];
            for (int i = 0; i < run.Length; i++)
            {
                int source = run[i] % count;
                result[i * 3] = dense[source * 3]; result[i * 3 + 1] = dense[source * 3 + 1]; result[i * 3 + 2] = dense[source * 3 + 2];
                if (!loop && (i == 0 || i == run.Length - 1)) continue;
                // An open run's window shrinks towards its ends, so a fixed end does not drag the samples beside it.
                int w = loop ? window : Math.Min(window, Math.Min(i, run.Length - 1 - i));
                double x = 0, y = 0, total = 0;
                for (int k = -w; k <= w; k++)
                {
                    int j = loop ? ((i + k) % n + n) % n : i + k;
                    double weight = Math.Exp(-k * k / (2 * sigma * sigma));
                    x += X(run[j]) * weight; y += Y(run[j]) * weight; total += weight;
                }
                result[i * 3] = x / total; result[i * 3 + 1] = y / total;
                if (heightAt(result[i * 3], result[i * 3 + 1]) is double z && double.IsFinite(z)) result[i * 3 + 2] = z;
            }
            if (loop) Array.Copy(result, 0, result, result.Length - 3, 3);
            return result;
        }
    }

    // About 50 degrees.
    private const double CornerAngle = .87;

    private static List<ScanCurvePiece> FitPart(double[] dense, bool closed, bool built, double cell, double planTolerance,
        double heightTolerance, Func<double, double, double?> heightAt, CancellationToken token)
    {
        int count = dense.Length / 3 - (closed ? 1 : 0);
        // Thinning spends half the plan budget; the primitive or spline spends the rest.
        double thinning = planTolerance * .5;
        int[] vertices = Thin(dense, count, closed, thinning);
        for (int attempt = 0; closed && attempt < 6 && (vertices.Length < 3 || SelfIntersects(dense, vertices)); attempt++)
            vertices = Thin(dense, count, closed, thinning *= .5);
        if (built)
            return Primitives(dense, count, closed, vertices, cell, planTolerance, heightTolerance, heightAt, token);
        if (!closed && HeldStraights(dense, vertices, thinning) is { Count: > 0 } held)
            return Mixed(dense, vertices, held, planTolerance, heightTolerance, heightAt, token);
        return Natural(dense, count, closed, vertices, thinning, planTolerance, heightAt, token);
    }

    /// <summary>A spline on the thinned vertices; a tighter thinning gives it more controls before settling for the polyline.</summary>
    private static List<ScanCurvePiece> Natural(double[] dense, int count, bool closed, int[] vertices, double thinning,
        double planTolerance, Func<double, double, double?> heightAt, CancellationToken token)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (Spline(dense, count, closed, vertices, planTolerance, heightAt) is ScanCurvePiece spline)
                return new List<ScanCurvePiece> { spline };
            int[] finer = Thin(dense, count, closed, thinning *= .6);
            if (closed && SelfIntersects(dense, finer)) break;
            vertices = finer;
        }
        return new List<ScanCurvePiece> { Polyline(dense, vertices) };
    }

    /// <summary>Vertex ranges of straight runs at least eight plan tolerances long on a natural outline.</summary>
    private static List<(int Start, int End)> HeldStraights(double[] dense, int[] vertices, double tolerance)
    {
        var held = new List<(int Start, int End)>();
        for (int start = 0; start + 1 < vertices.Length;)
        {
            int end = start + 1;
            while (end + 1 < vertices.Length && Straight(dense, vertices[start], vertices[end + 1], tolerance)) end++;
            double length = Math.Sqrt(Geometry2D.DistanceSquared(dense[vertices[start] * 3], dense[vertices[start] * 3 + 1],
                dense[vertices[end] * 3], dense[vertices[end] * 3 + 1]));
            if (length >= tolerance * 16) held.Add((start, end));
            start = end;
        }
        return held;
    }

    /// <summary>Held straights as draped lines, the stretches between them as open splines meeting the lines' ends.</summary>
    private static List<ScanCurvePiece> Mixed(double[] dense, int[] vertices, List<(int Start, int End)> held, double planTolerance,
        double heightTolerance, Func<double, double, double?> heightAt, CancellationToken token)
    {
        var pieces = new List<ScanCurvePiece>();
        int at = 0;
        foreach (var (start, end) in held.Append((vertices.Length - 1, vertices.Length - 1)))
        {
            if (start > at) Free(at, start);
            if (end > start) pieces.Add(Line(dense, vertices[start], vertices[end], heightTolerance, heightAt));
            at = end;
        }
        return pieces;

        void Free(int from, int to)
        {
            int first = vertices[from], last = vertices[to];
            double[] slice = dense[(first * 3)..(last * 3 + 3)];
            int[] local = vertices[from..(to + 1)].Select(v => v - first).ToArray();
            pieces.AddRange(local.Length < 3
                ? new List<ScanCurvePiece> { Polyline(slice, local) }
                : Natural(slice, slice.Length / 3, false, local, planTolerance * .5, planTolerance, heightAt, token));
        }
    }

    /// <summary>
    /// Straight lines through the thinned trace, or with <paramref name="thinned"/> false the trace itself: the
    /// fallback for an outline whose arcs or spline corner cuts cross its own trace or a neighbouring outline.
    /// </summary>
    public static ScanCurveGeometry Lines(double[] dense, bool closed, bool thinned, double planTolerance, double heightTolerance)
    {
        int count = dense.Length / 3 - (closed ? 1 : 0);
        int[] vertices = thinned ? Thin(dense, count, closed, planTolerance * .5) : Enumerable.Range(0, dense.Length / 3).ToArray();
        return new ScanCurveGeometry(new[] { Polyline(dense, vertices) }, planTolerance, heightTolerance);
    }

    /// <summary>Douglas-Peucker indices in plan; a closed trace starts at its point farthest from the first.</summary>
    private static int[] Thin(double[] p, int count, bool closed, double tolerance)
    {
        var keep = new bool[count + 1];
        int split = 0;
        if (closed)
        {
            double far = -1;
            for (int i = 1; i < count; i++)
            {
                double d = Geometry2D.DistanceSquared(p[0], p[1], p[i * 3], p[i * 3 + 1]);
                if (d > far) { far = d; split = i; }
            }
        }
        int last = closed ? count : count - 1;
        keep[0] = keep[last] = true;
        if (closed) keep[split] = true;
        var stack = new Stack<(int A, int B)>();
        if (closed && split > 0) { stack.Push((0, split)); stack.Push((split, last)); }
        else stack.Push((0, last));
        double squared = tolerance * tolerance;
        while (stack.TryPop(out var range))
        {
            int a = range.A, b = range.B, worst = -1;
            double ax = X(a), ay = Y(a), bx = X(b), by = Y(b), maximum = squared;
            for (int i = a + 1; i < b; i++)
            {
                double t = Geometry2D.ParameterOnSegmentClamped(ax, ay, bx, by, X(i), Y(i));
                double d = Geometry2D.DistanceSquared(ax + (bx - ax) * t, ay + (by - ay) * t, X(i), Y(i));
                if (d > maximum) { maximum = d; worst = i; }
            }
            if (worst < 0) continue;
            keep[worst] = true;
            stack.Push((a, worst)); stack.Push((worst, b));
        }
        return Enumerable.Range(0, last + 1).Where(i => keep[i]).ToArray();
        double X(int i) => p[(i % count) * 3];
        double Y(int i) => p[(i % count) * 3 + 1];
    }

    private static bool SelfIntersects(double[] p, int[] vertices)
    {
        var xy = new double[(vertices.Length - 1) * 2];
        for (int i = 0; i + 1 < vertices.Length; i++) { xy[i * 2] = p[vertices[i] * 3]; xy[i * 2 + 1] = p[vertices[i] * 3 + 1]; }
        return Geometry2D.ClosedPolylineSelfIntersects(xy, vertices.Length - 1);
    }

    /// <summary>
    /// Maximal straight runs first; a long one is a held straight. Circular arcs may only join the shorter
    /// runs between held straights, so a curve becomes arcs while a straight side next to a corner stays
    /// straight. Corners between pieces stay corners.
    /// </summary>
    private static List<ScanCurvePiece> Primitives(double[] dense, int count, bool closed, int[] vertices, double cell,
        double planTolerance, double heightTolerance, Func<double, double, double?> heightAt, CancellationToken token)
    {
        // A closed trace's last vertex index is count, the repeated start; Lift and TryPlan read raw indices.
        double[] raw = closed ? dense : dense[..(count * 3)];
        var runs = new List<(int Start, int End)>();
        for (int start = 0; start + 1 < vertices.Length;)
        {
            int end = start + 1;
            while (end + 1 < vertices.Length && Straight(raw, vertices[start], vertices[end + 1], planTolerance)) end++;
            runs.Add((start, end)); start = end;
        }
        double held = planTolerance * 8;
        bool Held(int run) => Math.Sqrt(Geometry2D.DistanceSquared(raw[vertices[runs[run].Start] * 3], raw[vertices[runs[run].Start] * 3 + 1],
            raw[vertices[runs[run].End] * 3], raw[vertices[runs[run].End] * 3 + 1])) >= held;
        var pieces = new List<ScanCurvePiece>();
        for (int run = 0; run < runs.Count;)
        {
            token.ThrowIfCancellationRequested();
            int last = run;
            ScanAlignmentFitter.Plan plan = default;
            if (!Held(run))
                while (last + 1 < runs.Count && !Held(last + 1) &&
                    Explains(raw, vertices[runs[run].Start], vertices[runs[last + 1].End], planTolerance, out var wider) && wider.Arc)
                { last++; plan = wider; }
            if (last == run)
                pieces.Add(Line(raw, vertices[runs[run].Start], vertices[runs[run].End], heightTolerance, heightAt));
            else
                pieces.AddRange(ScanAlignmentFitter.Lift(raw, vertices[runs[run].Start], vertices[runs[last].End], plan, cell, planTolerance, heightTolerance, token, heightAt));
            run = last + 1;
        }
        return pieces;
    }

    private static bool Straight(double[] raw, int first, int last, double tolerance)
    {
        double ax = raw[first * 3], ay = raw[first * 3 + 1], bx = raw[last * 3], by = raw[last * 3 + 1];
        for (int i = first + 1; i < last; i++)
        {
            double t = Geometry2D.ParameterOnSegmentClamped(ax, ay, bx, by, raw[i * 3], raw[i * 3 + 1]);
            if (Geometry2D.DistanceSquared(ax + (bx - ax) * t, ay + (by - ay) * t, raw[i * 3], raw[i * 3 + 1]) > tolerance * tolerance)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A line or arc holding every traced sample within tolerance. Alignment fitting accepts a share of outliers
    /// as evidence for a wall; a drawn outline that skipped them would cut across a real notch.
    /// </summary>
    private static bool Explains(double[] raw, int first, int last, double tolerance, out ScanAlignmentFitter.Plan plan)
    {
        if (!ScanAlignmentFitter.TryPlan(raw, first, last, tolerance, out plan, out _))
            return false;
        for (int i = first + 1; i < last; i++)
        {
            double x = raw[i * 3], y = raw[i * 3 + 1];
            double t = Math.Clamp(plan.Station(x, y), 0, 1);
            var q = plan.At(t);
            double off = plan.Arc ? Math.Abs(Math.Sqrt(Geometry2D.DistanceSquared(x, y, plan.Cx, plan.Cy)) - plan.Radius)
                : Math.Sqrt(Geometry2D.DistanceSquared(x, y, q.X, q.Y));
            if (off > tolerance || plan.Arc && Geometry2D.DistanceSquared(x, y, q.X, q.Y) > 4 * tolerance * tolerance)
                return false;
        }
        return true;
    }

    /// <summary>A straight plan run with heights from the terrain, split only where the drape leaves tolerance.</summary>
    private static ScanCurvePiece Line(double[] raw, int a, int b, double heightTolerance, Func<double, double, double?> heightAt)
    {
        double ax = raw[a * 3], ay = raw[a * 3 + 1], az = raw[a * 3 + 2];
        double bx = raw[b * 3], by = raw[b * 3 + 1], bz = raw[b * 3 + 2];
        var stations = new List<(double T, double Z)> { (0, az), (1, bz) };
        Refine(0, az, 1, bz, 0);
        stations.Sort((l, r) => l.T.CompareTo(r.T));
        var controls = new double[stations.Count * 3];
        var knots = new double[stations.Count + 2];
        for (int i = 0; i < stations.Count; i++)
        {
            controls[i * 3] = ax + (bx - ax) * stations[i].T;
            controls[i * 3 + 1] = ay + (by - ay) * stations[i].T;
            controls[i * 3 + 2] = stations[i].Z;
            knots[i + 1] = stations[i].T;
        }
        knots[0] = 0; knots[^1] = 1;
        return new ScanCurvePiece(1, knots, controls);

        void Refine(double t0, double z0, double t1, double z1, int depth)
        {
            double t = (t0 + t1) * .5;
            if (depth >= 8 || heightAt(ax + (bx - ax) * t, ay + (by - ay) * t) is not double z || !double.IsFinite(z) ||
                Math.Abs(z - (z0 + z1) * .5) <= heightTolerance)
                return;
            stations.Add((t, z));
            Refine(t0, z0, t, z, depth + 1); Refine(t, z, t1, z1, depth + 1);
        }
    }

    /// <summary>
    /// A clamped quadratic B-spline whose controls are the thinned vertices. A closed outline starts at the
    /// midpoint of its last leg, which makes the clamped curve the periodic one: smooth across the seam.
    /// Controls take the terrain height below them, exact wherever the ground is locally planar.
    /// </summary>
    private static ScanCurvePiece? Spline(double[] dense, int count, bool closed, int[] vertices, double planTolerance,
        Func<double, double, double?> heightAt)
    {
        var xy = new List<(double X, double Y)>();
        if (closed)
        {
            int n = vertices.Length - 1;
            if (n < 3) return null;
            (double X, double Y) last = P(vertices[n - 1]), first = P(vertices[0]);
            var seam = ((last.X + first.X) * .5, (last.Y + first.Y) * .5);
            xy.Add(seam);
            for (int i = 0; i < n; i++) xy.Add(P(vertices[i]));
            xy.Add(seam);
        }
        else
        {
            if (vertices.Length < 3) return null;
            foreach (int v in vertices) xy.Add(P(v));
        }
        int controls = xy.Count, spans = controls - 2;
        var knots = new double[controls + 3];
        for (int i = 0; i < knots.Length; i++) knots[i] = Math.Clamp(i - 2, 0, spans) / (double)spans;
        var points = new double[controls * 3];
        for (int i = 0; i < controls; i++)
        {
            points[i * 3] = xy[i].X; points[i * 3 + 1] = xy[i].Y; points[i * 3 + 2] = double.NaN;
            if (heightAt(xy[i].X, xy[i].Y) is double z && double.IsFinite(z)) points[i * 3 + 2] = z;
        }
        // Open ends are shared junctions and keep their traced heights exactly.
        if (!closed) { points[2] = dense[vertices[0] * 3 + 2]; points[^1] = dense[vertices[^1] * 3 + 2]; }
        for (int i = 0; i < controls; i++)
            if (double.IsNaN(points[i * 3 + 2])) points[i * 3 + 2] = NearestHeight(i);
        var spline = new ScanCurvePiece(2, knots, points);
        // The corner cut must stay inside the plan tolerance at every traced sample checked.
        for (int i = 0; i < count; i += Math.Max(1, count / 400))
            if (!Near(spline, dense[i * 3], dense[i * 3 + 1], spans, planTolerance))
                return null;
        return spline;

        (double X, double Y) P(int i) => (dense[(i % count) * 3], dense[(i % count) * 3 + 1]);
        double NearestHeight(int control)
        {
            int best = 0; double nearest = double.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                double d = Geometry2D.DistanceSquared(dense[i * 3], dense[i * 3 + 1], xy[control].X, xy[control].Y);
                if (d < nearest && double.IsFinite(dense[i * 3 + 2])) { nearest = d; best = i; }
            }
            return dense[best * 3 + 2];
        }
    }

    private static bool Near(ScanCurvePiece spline, double x, double y, int spans, double tolerance)
    {
        // Coarse search over spans, then a local refinement: the nearest curve point within tolerance.
        double squared = tolerance * tolerance, best = double.PositiveInfinity, bestU = 0;
        int samples = spans * 8;
        for (int i = 0; i <= samples; i++)
        {
            double u = i / (double)samples;
            var q = spline.PointAt(u);
            double d = Geometry2D.DistanceSquared(q.X, q.Y, x, y);
            if (d < best) { best = d; bestU = u; }
        }
        for (double step = .5 / samples; step > 1e-6 && best > squared; step *= .5)
            foreach (double u in new[] { bestU - step, bestU + step })
            {
                var q = spline.PointAt(Math.Clamp(u, 0, 1));
                double d = Geometry2D.DistanceSquared(q.X, q.Y, x, y);
                if (d < best) { best = d; bestU = Math.Clamp(u, 0, 1); }
            }
        return best <= squared;
    }

    private static ScanCurvePiece Polyline(double[] dense, int[] vertices)
    {
        int count = dense.Length / 3;
        var controls = new double[vertices.Length * 3];
        var knots = new double[vertices.Length + 2];
        for (int i = 0; i < vertices.Length; i++)
        {
            Array.Copy(dense, Math.Min(vertices[i], count - 1) * 3, controls, i * 3, 3);
            knots[i + 1] = i / (double)(vertices.Length - 1);
        }
        knots[^1] = 1;
        return new ScanCurvePiece(1, knots, controls);
    }
}
