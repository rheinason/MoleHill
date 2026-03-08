using MoleHill.Core.Grading;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Grading;

internal static class RetainingWallPlanner
{
    internal enum ReportLevel
    {
        Info,
        Warning,
        Error
    }

    internal sealed class ReportEntry
    {
        public ReportLevel Level { get; }
        public string Message { get; }

        public ReportEntry(ReportLevel level, string message)
        {
            Level = level;
            Message = message;
        }

        public override string ToString() => $"[{Level}] {Message}";
    }

    internal sealed class PlannedWall
    {
        public RetainingWallMeshGrader.WallStripDefinition Strip { get; }
        public Brep? Brep { get; }
        public int CurveA { get; }
        public int CurveB { get; }

        public PlannedWall(RetainingWallMeshGrader.WallStripDefinition strip, Brep? brep, int curveA, int curveB)
        {
            Strip = strip;
            Brep = brep;
            CurveA = curveA;
            CurveB = curveB;
        }
    }

    internal sealed class PlanResult
    {
        public IReadOnlyList<PlannedWall> Walls { get; }
        public IReadOnlyList<Line> PairLines { get; }
        public IReadOnlyList<ReportEntry> Report { get; }

        public PlanResult(IReadOnlyList<PlannedWall> walls, IReadOnlyList<Line> pairLines, IReadOnlyList<ReportEntry> report)
        {
            Walls = walls;
            PairLines = pairLines;
            Report = report;
        }
    }

    private sealed class PreparedCurve
    {
        public int SourceIndex { get; }
        public Point3d[] Points { get; private set; }
        public double[] CumLen { get; private set; }
        public double Length => CumLen[CumLen.Length - 1];

        public PreparedCurve(int sourceIndex, Point3d[] points)
        {
            SourceIndex = sourceIndex;
            Points = points;
            CumLen = BuildCumLen(points);
        }

        public void Reverse()
        {
            Array.Reverse(Points);
            CumLen = BuildCumLen(Points);
        }

        public void SetEnd(bool atStart, Point2d xy, double? z = null)
        {
            int idx = atStart ? 0 : Points.Length - 1;
            Point3d p = Points[idx];
            Points[idx] = new Point3d(xy.X, xy.Y, z ?? p.Z);
            CumLen = BuildCumLen(Points);
        }
    }

    private sealed class Pair
    {
        public PreparedCurve A { get; }
        public PreparedCurve B { get; }
        public bool Failed { get; set; }

        public Pair(PreparedCurve a, PreparedCurve b)
        {
            A = a;
            B = b;
        }

        public Point3d EndMid(bool start)
        {
            int ia = start ? 0 : A.Points.Length - 1;
            int ib = start ? 0 : B.Points.Length - 1;
            return new Point3d(
                (A.Points[ia].X + B.Points[ib].X) * 0.5,
                (A.Points[ia].Y + B.Points[ib].Y) * 0.5,
                (A.Points[ia].Z + B.Points[ib].Z) * 0.5);
        }
    }

    private sealed class Map
    {
        public Point3d[] A { get; }
        public Point3d[] B { get; }
        public int Count => A.Length;

        public Map(Point3d[] a, Point3d[] b)
        {
            A = a;
            B = b;
        }
    }

    public static PlanResult Plan(IReadOnlyList<Curve> curves, double tolerance)
    {
        var report = new List<ReportEntry>();
        var prepared = PrepareCurves(curves, tolerance, report);
        if (prepared.Count == 0) return new PlanResult(Array.Empty<PlannedWall>(), Array.Empty<Line>(), report);

        var pairs = PairCurves(prepared, tolerance, report);
        DetectCrossings(pairs, tolerance, report);
        ResolveCorners(pairs, tolerance, report);

        var pairLines = new List<Line>();
        var walls = new List<PlannedWall>();

        foreach (var pair in pairs)
        {
            if (pair.Failed) continue;
            pairLines.Add(new Line(pair.EndMid(true), pair.EndMid(false)));

            foreach (Map map in BuildMaps(pair, tolerance, report))
            {
                if (map.Count < 2) continue;

                bool aIsToe = DetermineToe(map, pair, report);
                var strip = BuildStrip(map, aIsToe);
                Brep? brep = BuildBrep(map);
                if (brep == null)
                {
                    report.Add(new ReportEntry(ReportLevel.Warning,
                        $"Brep generation failed for pair ({pair.A.SourceIndex}, {pair.B.SourceIndex})."));
                }
                else if (!brep.IsSolid)
                {
                    report.Add(new ReportEntry(ReportLevel.Warning,
                        $"Brep for pair ({pair.A.SourceIndex}, {pair.B.SourceIndex}) is not closed."));
                }

                walls.Add(new PlannedWall(strip, brep, pair.A.SourceIndex, pair.B.SourceIndex));
            }
        }

        return new PlanResult(walls, pairLines, report);
    }

    private static List<PreparedCurve> PrepareCurves(IReadOnlyList<Curve> curves, double tolerance, List<ReportEntry> report)
    {
        var result = new List<PreparedCurve>();
        double chordTol = Math.Max(tolerance / 4.0, 1e-6);
        double angleTol = 5.0 * Math.PI / 180.0;

        for (int i = 0; i < curves.Count; i++)
        {
            Curve? c = curves[i];
            if (c == null) { report.Add(new ReportEntry(ReportLevel.Warning, $"Curve {i}: null; skipped.")); continue; }
            if (c.IsClosed) { report.Add(new ReportEntry(ReportLevel.Warning, $"Curve {i}: closed; skipped.")); continue; }

            Polyline pl;
            if (!c.TryGetPolyline(out pl))
            {
                var poly = c.ToPolyline(chordTol, angleTol, 0, 0);
                if (poly == null || !poly.TryGetPolyline(out pl))
                {
                    report.Add(new ReportEntry(ReportLevel.Warning, $"Curve {i}: tessellation failed; skipped."));
                    continue;
                }
            }

            Point3d[] points = CleanupPolyline(pl, tolerance);
            if (points.Length < 2) { report.Add(new ReportEntry(ReportLevel.Warning, $"Curve {i}: too short after cleanup; skipped.")); continue; }
            points = EnsureMinSegments(points, 8);
            result.Add(new PreparedCurve(i, points));
        }

        return result;
    }

    private static List<Pair> PairCurves(List<PreparedCurve> curves, double tolerance, List<ReportEntry> report)
    {
        var bestFor = new Dictionary<int, (int idx, double mean, double iqr, double max, double score)>();

        foreach (PreparedCurve a in curves)
        {
            if (a.Length < 2 * tolerance)
            {
                report.Add(new ReportEntry(ReportLevel.Warning, $"Curve {a.SourceIndex}: length < 2T; skipped."));
                continue;
            }

            int n = Math.Clamp((int)Math.Round(a.Length / Math.Max(tolerance, 1e-9)), 8, 64);
            Point3d[] sample = SampleByCount(a.Points, a.CumLen, n);

            (int idx, double mean, double iqr, double max, double score) best = (-1, 0, 0, 0, double.MinValue);
            foreach (PreparedCurve b in curves)
            {
                if (b.SourceIndex == a.SourceIndex || b.Length < 2 * tolerance) continue;

                double[] dist = new double[n];
                int wins = 0;
                for (int i = 0; i < n; i++)
                {
                    double d = DistancePointPolyline2D(sample[i], b.Points);
                    dist[i] = d;
                }

                for (int i = 0; i < n; i++)
                {
                    bool nearest = true;
                    foreach (PreparedCurve other in curves)
                    {
                        if (other.SourceIndex == a.SourceIndex || other.SourceIndex == b.SourceIndex || other.Length < 2 * tolerance) continue;
                        if (DistancePointPolyline2D(sample[i], other.Points) < dist[i]) { nearest = false; break; }
                    }
                    if (nearest) wins++;
                }

                double mean = dist.Average();
                double iqr = Iqr(dist);
                double max = dist.Max();
                double score = (wins / (double)n) / (1.0 + iqr / Math.Max(tolerance, 1e-9));
                if (score > best.score) best = (b.SourceIndex, mean, iqr, max, score);
            }

            if (best.idx >= 0) bestFor[a.SourceIndex] = best;
        }

        var pairs = new List<Pair>();
        var used = new HashSet<(int, int)>();
        foreach (var kv in bestFor)
        {
            int aIdx = kv.Key;
            int bIdx = kv.Value.idx;
            if (!bestFor.TryGetValue(bIdx, out var bBest) || bBest.idx != aIdx) continue;

            int lo = Math.Min(aIdx, bIdx);
            int hi = Math.Max(aIdx, bIdx);
            if (!used.Add((lo, hi))) continue;

            bool pass = kv.Value.mean <= tolerance && kv.Value.iqr <= tolerance && kv.Value.max <= 1.5 * tolerance &&
                        bBest.mean <= tolerance && bBest.iqr <= tolerance && bBest.max <= 1.5 * tolerance;
            if (!pass) continue;

            PreparedCurve baseA = curves.First(c => c.SourceIndex == aIdx);
            PreparedCurve baseB = curves.First(c => c.SourceIndex == bIdx);
            var a = new PreparedCurve(baseA.SourceIndex, (Point3d[])baseA.Points.Clone());
            var b = new PreparedCurve(baseB.SourceIndex, (Point3d[])baseB.Points.Clone());

            if (ShouldFlip(a, b)) b.Reverse();
            if (AverageGap(a, b) < tolerance / 10.0)
            {
                report.Add(new ReportEntry(ReportLevel.Warning, $"Pair ({aIdx}, {bIdx}) skipped: curves too close (<T/10)."));
                continue;
            }

            pairs.Add(new Pair(a, b));
        }

        var paired = new HashSet<int>(pairs.SelectMany(p => new[] { p.A.SourceIndex, p.B.SourceIndex }));
        foreach (PreparedCurve c in curves)
            if (c.Length >= 2 * tolerance && !paired.Contains(c.SourceIndex))
                report.Add(new ReportEntry(ReportLevel.Warning, $"Curve {c.SourceIndex}: no mutual pair found."));

        return pairs;
    }

    private static void DetectCrossings(List<Pair> pairs, double tolerance, List<ReportEntry> report)
    {
        for (int i = 0; i < pairs.Count; i++)
        {
            if (pairs[i].Failed) continue;
            Line li = new(pairs[i].EndMid(true), pairs[i].EndMid(false));

            for (int j = i + 1; j < pairs.Count; j++)
            {
                if (pairs[j].Failed) continue;
                Line lj = new(pairs[j].EndMid(true), pairs[j].EndMid(false));
                if (!TrySegmentIntersection(li, lj, out double t, out double u, out double angleDeg)) continue;
                if (t <= 1e-5 || t >= 1 - 1e-5 || u <= 1e-5 || u >= 1 - 1e-5) continue;
                if (angleDeg <= 10.0) continue;

                pairs[i].Failed = true;
                pairs[j].Failed = true;
                report.Add(new ReportEntry(ReportLevel.Error,
                    $"Crossing walls detected between pairs ({pairs[i].A.SourceIndex}, {pairs[i].B.SourceIndex}) and ({pairs[j].A.SourceIndex}, {pairs[j].B.SourceIndex})."));
            }
        }
    }

    private static void ResolveCorners(List<Pair> pairs, double tolerance, List<ReportEntry> report)
    {
        foreach (var pair in pairs.Where(p => !p.Failed))
        {
            foreach (var other in pairs.Where(p => !p.Failed && !ReferenceEquals(p, pair)))
            {
                foreach (bool s0 in new[] { true, false })
                {
                    foreach (bool s1 in new[] { true, false })
                    {
                        Point3d p0 = pair.EndMid(s0);
                        Point3d p1 = other.EndMid(s1);
                        if (Distance2D(p0, p1) > tolerance) continue;

                        Point2d d0 = PairDirection(pair, s0);
                        Point2d d1 = PairDirection(other, s1);
                        if (!IntersectLines(new Point2d(p0.X, p0.Y), d0, new Point2d(p1.X, p1.Y), d1, out Point2d x)) continue;

                        pair.A.SetEnd(s0, x);
                        pair.B.SetEnd(s0, x);
                        other.A.SetEnd(s1, x);
                        other.B.SetEnd(s1, x);

                        SnapZ(pair, s0, other, s1);
                        report.Add(new ReportEntry(ReportLevel.Info,
                            $"Corner resolved between pairs ({pair.A.SourceIndex}, {pair.B.SourceIndex}) and ({other.A.SourceIndex}, {other.B.SourceIndex})."));
                    }
                }
            }
        }
    }

    private static IEnumerable<Map> BuildMaps(Pair pair, double tolerance, List<ReportEntry> report)
    {
        double minLength = Math.Min(pair.A.Length, pair.B.Length);
        double baseStep = Math.Clamp(tolerance * 2.0, minLength / 64.0, minLength / 8.0);

        var station = new SortedSet<double> { 0, pair.A.Length };
        for (double d = baseStep; d < pair.A.Length; d += baseStep) station.Add(d);
        for (int i = 1; i < pair.A.CumLen.Length - 1; i++) station.Add(pair.A.CumLen[i]);

        double[] paramsA = station.ToArray();
        var pointsA = new Point3d[paramsA.Length];
        var pointsB = new Point3d[paramsA.Length];
        var fail = new bool[paramsA.Length];

        double prev = 0;
        int consecutive = 0;
        bool clampInfo = false;
        for (int i = 0; i < paramsA.Length; i++)
        {
            pointsA[i] = PointAt(pair.A.Points, pair.A.CumLen, paramsA[i]);
            Point2d tan = TangentAt(pair.A.Points, pair.A.CumLen, paramsA[i]);
            Point2d normal = Unit(new Point2d(-tan.Y, tan.X));

            double pb;
            bool hit = IntersectNormal(pointsA[i], normal, pair.B.Points, pair.B.CumLen, out pb);
            if (!hit) ClosestAlong(pointsA[i], pair.B.Points, pair.B.CumLen, Math.Max(0, prev - pair.B.Length * 0.15), Math.Min(pair.B.Length, prev + pair.B.Length * 0.15), out pb);

            if (i > 0 && pb < prev - pair.B.Length * 0.05)
            {
                fail[i] = true;
                consecutive++;
                if (consecutive < 5)
                {
                    pb = prev;
                    if (!clampInfo)
                    {
                        report.Add(new ReportEntry(ReportLevel.Info,
                            $"Pair ({pair.A.SourceIndex}, {pair.B.SourceIndex}): monotonic mapping clamped."));
                        clampInfo = true;
                    }
                }
            }
            else
            {
                consecutive = 0;
            }

            pb = Math.Clamp(pb, 0, pair.B.Length);
            prev = pb;
            pointsB[i] = PointAt(pair.B.Points, pair.B.CumLen, pb);
        }

        bool[] splitMask = MarkLongRuns(fail);
        if (!splitMask.Any(v => v))
        {
            yield return new Map(pointsA, pointsB);
            yield break;
        }

        report.Add(new ReportEntry(ReportLevel.Warning,
            $"Pair ({pair.A.SourceIndex}, {pair.B.SourceIndex}): split due to >=5 consecutive monotonic failures."));

        int start = 0;
        while (start < splitMask.Length)
        {
            while (start < splitMask.Length && splitMask[start]) start++;
            int end = start;
            while (end < splitMask.Length && !splitMask[end]) end++;
            int len = end - start;
            if (len >= 2)
            {
                var a = new Point3d[len];
                var b = new Point3d[len];
                Array.Copy(pointsA, start, a, 0, len);
                Array.Copy(pointsB, start, b, 0, len);
                yield return new Map(a, b);
            }
            start = end + 1;
        }
    }

    private static bool DetermineToe(Map map, Pair pair, List<ReportEntry> report)
    {
        int i0 = map.Count / 3;
        int i1 = map.Count - i0;
        if (i1 <= i0) { i0 = 0; i1 = map.Count; }

        double a = 0;
        double b = 0;
        int n = 0;
        for (int i = i0; i < i1; i++) { a += map.A[i].Z; b += map.B[i].Z; n++; }
        if (n > 0) { a /= n; b /= n; }

        if (Math.Abs(a - b) <= 1e-9)
        {
            report.Add(new ReportEntry(ReportLevel.Info,
                $"Pair ({pair.A.SourceIndex}, {pair.B.SourceIndex}): equal middle-third mean Z; using curve A as toe."));
            return true;
        }

        return a < b;
    }

    private static RetainingWallMeshGrader.WallStripDefinition BuildStrip(Map map, bool aIsToe)
    {
        int n = map.Count;
        var toeXy = new double[n * 2];
        var topXy = new double[n * 2];
        var toeZ = new double[n];
        var topZ = new double[n];

        for (int i = 0; i < n; i++)
        {
            Point3d toe = aIsToe ? map.A[i] : map.B[i];
            Point3d top = aIsToe ? map.B[i] : map.A[i];

            toeXy[i * 2] = toe.X;
            toeXy[i * 2 + 1] = toe.Y;
            topXy[i * 2] = top.X;
            topXy[i * 2 + 1] = top.Y;
            toeZ[i] = toe.Z;
            topZ[i] = top.Z;
        }

        return new RetainingWallMeshGrader.WallStripDefinition(toeXy, toeZ, topXy, topZ, n);
    }

    private static Brep? BuildBrep(Map map)
    {
        var sections = new List<Curve>(map.Count);
        for (int i = 0; i < map.Count; i++)
        {
            Point3d a = map.A[i];
            Point3d b = map.B[i];
            double low = Math.Min(a.Z, b.Z);
            double high = Math.Max(a.Z, b.Z);
            var pl = new Polyline(new[]
            {
                new Point3d(a.X, a.Y, low),
                new Point3d(a.X, a.Y, high),
                new Point3d(b.X, b.Y, high),
                new Point3d(b.X, b.Y, low),
                new Point3d(a.X, a.Y, low)
            });
            sections.Add(new PolylineCurve(pl));
        }

        Brep[] loft = Brep.CreateFromLoft(sections, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
        if (loft == null || loft.Length == 0) return null;

        var join = new List<Brep> { loft[0] };
        AddCap(sections[0], join, 1e-3);
        AddCap(sections[sections.Count - 1], join, 1e-3);
        Brep[] joined = Brep.JoinBreps(join, 1e-3);
        return joined != null && joined.Length > 0 ? joined[0] : loft[0];
    }

    private static void AddCap(Curve section, List<Brep> join, double tol)
    {
        Brep[]? planar = Brep.CreatePlanarBreps(section, tol);
        if (planar != null && planar.Length > 0) { join.Add(planar[0]); return; }
        if (!section.TryGetPolyline(out Polyline pl) || pl.Count < 4) return;
        Brep? cap = Brep.CreateFromCornerPoints(pl[0], pl[1], pl[2], pl[3], tol);
        if (cap != null) join.Add(cap);
    }

    private static bool[] MarkLongRuns(bool[] fail)
    {
        var mask = new bool[fail.Length];
        int i = 0;
        while (i < fail.Length)
        {
            if (!fail[i]) { i++; continue; }
            int start = i;
            while (i < fail.Length && fail[i]) i++;
            if (i - start >= 5)
                for (int k = start; k < i; k++) mask[k] = true;
        }
        return mask;
    }

    private static void SnapZ(Pair a, bool aStart, Pair b, bool bStart)
    {
        int ai = aStart ? 0 : a.A.Points.Length - 1;
        int aj = aStart ? 0 : a.B.Points.Length - 1;
        int bi = bStart ? 0 : b.A.Points.Length - 1;
        int bj = bStart ? 0 : b.B.Points.Length - 1;
        double[] z = { a.A.Points[ai].Z, a.B.Points[aj].Z, b.A.Points[bi].Z, b.B.Points[bj].Z };
        double[] sorted = (double[])z.Clone();
        Array.Sort(sorted);
        double low = (sorted[0] + sorted[1]) * 0.5;
        double high = (sorted[2] + sorted[3]) * 0.5;
        double mid = (low + high) * 0.5;
        a.A.SetEnd(aStart, new Point2d(a.A.Points[ai].X, a.A.Points[ai].Y), z[0] <= mid ? low : high);
        a.B.SetEnd(aStart, new Point2d(a.B.Points[aj].X, a.B.Points[aj].Y), z[1] <= mid ? low : high);
        b.A.SetEnd(bStart, new Point2d(b.A.Points[bi].X, b.A.Points[bi].Y), z[2] <= mid ? low : high);
        b.B.SetEnd(bStart, new Point2d(b.B.Points[bj].X, b.B.Points[bj].Y), z[3] <= mid ? low : high);
    }

    private static Point2d PairDirection(Pair pair, bool start)
    {
        Point3d a0 = pair.A.Points[start ? 0 : pair.A.Points.Length - 1];
        Point3d a1 = pair.A.Points[start ? 1 : pair.A.Points.Length - 2];
        Point3d b0 = pair.B.Points[start ? 0 : pair.B.Points.Length - 1];
        Point3d b1 = pair.B.Points[start ? 1 : pair.B.Points.Length - 2];
        Point2d d = new(((a1.X + b1.X) - (a0.X + b0.X)) * 0.5, ((a1.Y + b1.Y) - (a0.Y + b0.Y)) * 0.5);
        if (!start) d = new(-d.X, -d.Y);
        return Unit(d);
    }

    private static bool ShouldFlip(PreparedCurve a, PreparedCurve b)
    {
        Point3d a0 = a.Points[0];
        Point3d a1 = a.Points[a.Points.Length - 1];
        Point3d b0 = b.Points[0];
        Point3d b1 = b.Points[b.Points.Length - 1];
        double forward = Distance2D(a0, b0) + Distance2D(a1, b1);
        double reverse = Distance2D(a0, b1) + Distance2D(a1, b0);
        return reverse < forward;
    }

    private static double AverageGap(PreparedCurve a, PreparedCurve b)
    {
        int n = 20;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0 : (double)i / (n - 1);
            Point3d p = PointAt(a.Points, a.CumLen, a.Length * t);
            sum += DistancePointPolyline2D(p, b.Points);
        }
        return sum / n;
    }

    private static double Iqr(double[] values)
    {
        if (values.Length == 0) return 0;
        double[] s = (double[])values.Clone();
        Array.Sort(s);
        return Percentile(s, 0.75) - Percentile(s, 0.25);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        double x = p * (sorted.Length - 1);
        int i0 = (int)Math.Floor(x);
        int i1 = Math.Min(sorted.Length - 1, i0 + 1);
        double t = x - i0;
        return sorted[i0] + (sorted[i1] - sorted[i0]) * t;
    }

    private static bool TrySegmentIntersection(Line a, Line b, out double t, out double u, out double angleDeg)
    {
        t = 0;
        u = 0;
        angleDeg = 0;

        Point2d p = new(a.FromX, a.FromY);
        Point2d r = new(a.ToX - a.FromX, a.ToY - a.FromY);
        Point2d q = new(b.FromX, b.FromY);
        Point2d s = new(b.ToX - b.FromX, b.ToY - b.FromY);
        if (!Solve2x2(r.X, -s.X, r.Y, -s.Y, q.X - p.X, q.Y - p.Y, out t, out u))
            return false;

        double lr = Math.Sqrt(r.X * r.X + r.Y * r.Y);
        double ls = Math.Sqrt(s.X * s.X + s.Y * s.Y);
        if (lr < 1e-12 || ls < 1e-12) return false;
        double cos = Math.Clamp((r.X * s.X + r.Y * s.Y) / (lr * ls), -1.0, 1.0);
        angleDeg = Math.Acos(Math.Abs(cos)) * 180.0 / Math.PI;
        return true;
    }

    private static bool IntersectLines(Point2d p, Point2d d, Point2d q, Point2d e, out Point2d x)
    {
        x = default;
        if (!Solve2x2(d.X, -e.X, d.Y, -e.Y, q.X - p.X, q.Y - p.Y, out double t, out _))
            return false;
        x = new Point2d(p.X + d.X * t, p.Y + d.Y * t);
        return true;
    }

    private static bool IntersectNormal(Point3d p, Point2d normal, Point3d[] poly, double[] cumLen, out double along)
    {
        along = 0;
        bool found = false;
        double bestAbs = double.MaxValue;
        for (int i = 0; i < poly.Length - 1; i++)
        {
            Point2d a = new(poly[i].X, poly[i].Y);
            Point2d b = new(poly[i + 1].X, poly[i + 1].Y);
            Point2d seg = new(b.X - a.X, b.Y - a.Y);
            if (!Solve2x2(normal.X, -seg.X, normal.Y, -seg.Y, a.X - p.X, a.Y - p.Y, out double r, out double u))
                continue;
            if (u < -1e-9 || u > 1 + 1e-9) continue;
            if (Math.Abs(r) >= bestAbs) continue;
            bestAbs = Math.Abs(r);
            double segLen = Distance2D(poly[i], poly[i + 1]);
            along = cumLen[i] + Math.Clamp(u, 0, 1) * segLen;
            found = true;
        }
        return found;
    }

    private static bool ClosestAlong(Point3d p, Point3d[] poly, double[] cumLen, double minAlong, double maxAlong, out double along)
    {
        along = 0;
        double lo = Math.Max(0, minAlong);
        double hi = Math.Min(cumLen[cumLen.Length - 1], maxAlong);
        if (hi <= lo) return false;

        bool found = false;
        double best = double.MaxValue;
        for (int i = 0; i < poly.Length - 1; i++)
        {
            double s0 = cumLen[i];
            double s1 = cumLen[i + 1];
            if (s1 < lo || s0 > hi) continue;

            Point3d a = poly[i];
            Point3d b = poly[i + 1];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-18) continue;
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            t = Math.Clamp(t, 0, 1);
            double s = s0 + t * (s1 - s0);
            if (s < lo || s > hi) continue;
            double cx = a.X + t * dx;
            double cy = a.Y + t * dy;
            double d2 = (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy);
            if (d2 >= best) continue;
            best = d2;
            along = s;
            found = true;
        }

        return found;
    }

    private static Point3d[] CleanupPolyline(Polyline pl, double tolerance)
    {
        var pts = new List<Point3d>();
        for (int i = 0; i < pl.Count; i++)
            if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(pl[i]) > tolerance * 0.25)
                pts.Add(pl[i]);

        if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= tolerance * 0.25)
            pts.RemoveAt(pts.Count - 1);
        return pts.ToArray();
    }

    private static Point3d[] EnsureMinSegments(Point3d[] points, int minSegments)
    {
        int minPts = minSegments + 1;
        if (points.Length >= minPts) return points;
        var c = new PolylineCurve(points);
        double[]? t = c.DivideByCount(minPts - 1, true);
        if (t == null || t.Length < minPts) return points;
        var sampled = new Point3d[t.Length];
        for (int i = 0; i < t.Length; i++)
            sampled[i] = c.PointAt(t[i]);
        return sampled;
    }

    private static Point3d[] SampleByCount(Point3d[] points, double[] cumLen, int n)
    {
        var r = new Point3d[n];
        double total = cumLen[cumLen.Length - 1];
        for (int i = 0; i < n; i++)
            r[i] = PointAt(points, cumLen, total * i / Math.Max(1, n - 1));
        return r;
    }

    private static double[] BuildCumLen(Point3d[] points)
    {
        var cum = new double[points.Length];
        for (int i = 1; i < points.Length; i++)
            cum[i] = cum[i - 1] + Distance2D(points[i - 1], points[i]);
        return cum;
    }

    private static Point3d PointAt(Point3d[] points, double[] cumLen, double along)
    {
        if (along <= 0) return points[0];
        double total = cumLen[cumLen.Length - 1];
        if (along >= total) return points[points.Length - 1];

        int idx = Array.BinarySearch(cumLen, along);
        if (idx >= 0) return points[idx];
        idx = ~idx;
        int i0 = Math.Max(0, idx - 1);
        int i1 = Math.Min(points.Length - 1, idx);
        double s0 = cumLen[i0];
        double s1 = cumLen[i1];
        double t = s1 - s0 < 1e-12 ? 0 : (along - s0) / (s1 - s0);
        return new Point3d(
            points[i0].X + (points[i1].X - points[i0].X) * t,
            points[i0].Y + (points[i1].Y - points[i0].Y) * t,
            points[i0].Z + (points[i1].Z - points[i0].Z) * t);
    }

    private static Point2d TangentAt(Point3d[] points, double[] cumLen, double along)
    {
        int idx = Array.BinarySearch(cumLen, along);
        if (idx < 0) idx = Math.Max(0, ~idx - 1);
        if (idx >= points.Length - 1) idx = points.Length - 2;
        Point3d a = points[idx];
        Point3d b = points[idx + 1];
        return Unit(new Point2d(b.X - a.X, b.Y - a.Y));
    }

    private static double DistancePointPolyline2D(Point3d p, Point3d[] poly)
    {
        double best = double.MaxValue;
        for (int i = 0; i < poly.Length - 1; i++)
        {
            double d = DistancePointSegment2D(p, poly[i], poly[i + 1]);
            if (d < best) best = d;
        }
        return best;
    }

    private static double DistancePointSegment2D(Point3d p, Point3d a, Point3d b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-18) return Distance2D(p, a);
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        t = Math.Clamp(t, 0, 1);
        double cx = a.X + t * dx;
        double cy = a.Y + t * dy;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }

    private static bool Solve2x2(double a11, double a12, double a21, double a22, double b1, double b2, out double x1, out double x2)
    {
        x1 = 0;
        x2 = 0;
        double det = a11 * a22 - a12 * a21;
        if (Math.Abs(det) < 1e-12) return false;
        x1 = (b1 * a22 - b2 * a12) / det;
        x2 = (a11 * b2 - a21 * b1) / det;
        return true;
    }

    private static double Distance2D(Point3d a, Point3d b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Point2d Unit(Point2d v)
    {
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len < 1e-12) return new Point2d(1, 0);
        return new Point2d(v.X / len, v.Y / len);
    }
}
