using Rhino.Geometry;
using System.Diagnostics;

namespace MoleHill.Shared;

internal static class RetainingWallPlannerCore
{
    private const double PlannerTimingDiagnosticThresholdMs = 250.0;
    private const double AmbiguityCostRatio = 1.5;
    private const double MaxPairDistanceFactor = 1.5;

    internal enum ReportLevel
    {
        Info,
        Warning,
        Error
    }

    internal enum ReportReason
    {
        None,
        NullCurve,
        TessellationFailed,
        TooShort,
        SelfIntersectingRail,
        MixedOpenClosed,
        NoPair,
        AmbiguousPair,
        PairDistanceRejected,
        SubToleranceWidth,
        CrossingWalls,
        CornerResolved,
        CornerRejected,
        MappingRejected,
        SolidFailed,
        Timing
    }

    internal sealed class ReportEntry
    {
        public ReportLevel Level { get; }
        public ReportReason Reason { get; }
        public int? CurveA { get; }
        public int? CurveB { get; }
        public int? PairIndex { get; }
        public string Message { get; }

        public ReportEntry(
            ReportLevel level,
            ReportReason reason,
            string message,
            int? curveA = null,
            int? curveB = null,
            int? pairIndex = null)
        {
            Level = level;
            Reason = reason;
            Message = message;
            CurveA = curveA;
            CurveB = curveB;
            PairIndex = pairIndex;
        }

        public override string ToString() => $"[{Level}] {Message}";
    }

    internal sealed class WallRails
    {
        public Point3d[] ToePoints { get; }
        public Point3d[] TopPoints { get; }
        public bool IsClosed { get; }
        public double MinWidth { get; }

        public WallRails(Point3d[] toePoints, Point3d[] topPoints, bool isClosed, double minWidth)
        {
            ToePoints = toePoints;
            TopPoints = topPoints;
            IsClosed = isClosed;
            MinWidth = minWidth;
        }
    }

    internal sealed class PlannedWall
    {
        public WallRails Rails { get; }
        public Brep? Brep { get; }
        public int CurveA { get; }
        public int CurveB { get; }
        public Line PairLine { get; }

        public PlannedWall(WallRails rails, Brep? brep, int curveA, int curveB, Line pairLine)
        {
            Rails = rails;
            Brep = brep;
            CurveA = curveA;
            CurveB = curveB;
            PairLine = pairLine;
        }
    }

    internal sealed class PlanResult
    {
        public IReadOnlyList<PlannedWall> Walls { get; }
        public IReadOnlyList<Line> PairLines { get; }
        public IReadOnlyList<ReportEntry> Report { get; }
        public PlanTiming Timing { get; }

        public PlanResult(IReadOnlyList<PlannedWall> walls, IReadOnlyList<Line> pairLines, IReadOnlyList<ReportEntry> report, PlanTiming? timing = null)
        {
            Walls = walls;
            PairLines = pairLines;
            Report = report;
            Timing = timing ?? PlanTiming.Empty;
        }
    }

    internal sealed class PlanTiming
    {
        public static PlanTiming Empty { get; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        public PlanTiming(
            TimeSpan preprocess,
            TimeSpan pairing,
            TimeSpan interactions,
            TimeSpan walls,
            TimeSpan total)
        {
            Preprocess = preprocess;
            Pairing = pairing;
            Interactions = interactions;
            Walls = walls;
            Total = total;
        }

        public TimeSpan Preprocess { get; }

        public TimeSpan Pairing { get; }

        public TimeSpan Interactions { get; }

        public TimeSpan Walls { get; }

        public TimeSpan Total { get; }
    }

    private sealed class PreparedCurve
    {
        public int WorkIndex { get; }
        public int SourceIndex { get; }
        public int FragmentIndex { get; }
        public bool IsClosed { get; }
        public Point3d[] Points { get; private set; }
        public double[] CumLen { get; private set; }
        public double Length { get; private set; }

        public PreparedCurve(int workIndex, int sourceIndex, int fragmentIndex, Point3d[] points, bool isClosed)
        {
            WorkIndex = workIndex;
            SourceIndex = sourceIndex;
            FragmentIndex = fragmentIndex;
            IsClosed = isClosed;
            Points = points;
            CumLen = Array.Empty<double>();
            RebuildLengths();
        }

        public PreparedCurve Clone() => new(WorkIndex, SourceIndex, FragmentIndex, (Point3d[])Points.Clone(), IsClosed);

        public void Reverse()
        {
            Array.Reverse(Points);
            RebuildLengths();
        }

        public void RotateClosed(double along, double tolerance)
        {
            if (!IsClosed || Points.Length < 3)
                return;

            Points = RotateClosedPoints(Points, CumLen, Length, along, tolerance);
            RebuildLengths();
        }

        public void SetEnd(bool start, Point3d point)
        {
            Points[start ? 0 : Points.Length - 1] = point;
            RebuildLengths();
        }

        private void RebuildLengths()
        {
            CumLen = BuildCumLen(Points);
            Length = ComputeTotalLength(Points, IsClosed, CumLen);
        }
    }

    private sealed class Pair
    {
        public PreparedCurve A { get; }
        public PreparedCurve B { get; }
        public bool Failed { get; set; }
        public bool IsClosed => A.IsClosed && B.IsClosed;

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

        public Line GetPairLine() =>
            IsClosed
                ? new Line(A.Points[0], B.Points[0])
                : new Line(EndMid(true), EndMid(false));
    }

    private readonly record struct CurveBounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double GapTo(CurveBounds other)
        {
            double dx = AxisGap(MinX, MaxX, other.MinX, other.MaxX);
            double dy = AxisGap(MinY, MaxY, other.MinY, other.MaxY);
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static double AxisGap(double minA, double maxA, double minB, double maxB)
        {
            if (maxA < minB)
                return minB - maxA;
            if (maxB < minA)
                return minA - maxB;
            return 0.0;
        }
    }

    private readonly record struct CandidateStats(int Index, double Mean, double Iqr, double Max, double Cost);
    private readonly record struct CurveEnd(Point3d Point, Point2d Direction);

    public static PlanResult Plan(
        IReadOnlyList<Curve> curves,
        double maxWallWidth,
        double? curveParsingTolerance = null)
    {
        var report = new List<ReportEntry>();
        var totalTimer = Stopwatch.StartNew();
        double resolvedMaxWallWidth = Math.Max(maxWallWidth, 1e-9);
        double geometryTolerance = Math.Max(curveParsingTolerance ?? Math.Min(resolvedMaxWallWidth * 0.01, 0.001), 1e-9);

        var preprocessTimer = Stopwatch.StartNew();
        var prepared = PrepareCurves(curves, geometryTolerance, report);
        preprocessTimer.Stop();
        if (prepared.Count == 0)
        {
            totalTimer.Stop();
            return new PlanResult(
                Array.Empty<PlannedWall>(),
                Array.Empty<Line>(),
                report,
                CreatePlanTiming(preprocessTimer.Elapsed, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, totalTimer.Elapsed));
        }

        var pairingTimer = Stopwatch.StartNew();
        List<Pair> pairs = PairCurves(prepared, resolvedMaxWallWidth, geometryTolerance, report);
        pairingTimer.Stop();

        var interactionTimer = Stopwatch.StartNew();
        ResolveCorners(pairs, geometryTolerance, report);
        DetectCrossings(pairs, geometryTolerance, report);
        interactionTimer.Stop();

        var wallTimer = Stopwatch.StartNew();
        List<PlannedWall> walls = BuildWalls(pairs, geometryTolerance, report);
        wallTimer.Stop();
        totalTimer.Stop();

        if (totalTimer.Elapsed.TotalMilliseconds >= PlannerTimingDiagnosticThresholdMs)
        {
            report.Add(new ReportEntry(
                ReportLevel.Info,
                ReportReason.Timing,
                $"Retaining wall planner timing: preprocess {preprocessTimer.Elapsed.TotalMilliseconds:0} ms, pairing {pairingTimer.Elapsed.TotalMilliseconds:0} ms, interactions {interactionTimer.Elapsed.TotalMilliseconds:0} ms, walls {wallTimer.Elapsed.TotalMilliseconds:0} ms, total {totalTimer.Elapsed.TotalMilliseconds:0} ms."));
        }

        return new PlanResult(
            walls,
            walls.Select(wall => wall.PairLine).ToList(),
            report,
            CreatePlanTiming(preprocessTimer.Elapsed, pairingTimer.Elapsed, interactionTimer.Elapsed, wallTimer.Elapsed, totalTimer.Elapsed));
    }

    private static PlanTiming CreatePlanTiming(
        TimeSpan preprocess,
        TimeSpan pairing,
        TimeSpan interactions,
        TimeSpan walls,
        TimeSpan total)
    {
        return new PlanTiming(preprocess, pairing, interactions, walls, total);
    }

    private static List<PreparedCurve> PrepareCurves(
        IReadOnlyList<Curve> curves,
        double geometryTolerance,
        List<ReportEntry> report)
    {
        var result = new List<PreparedCurve>();
        double chordTol = Math.Max(geometryTolerance, 1e-9);
        double angleTol = 5.0 * Math.PI / 180.0;
        double maxEdgeLength = 0.0;

        for (int i = 0; i < curves.Count; i++)
        {
            Curve? curve = curves[i];
            if (curve == null)
            {
                report.Add(new ReportEntry(ReportLevel.Warning, ReportReason.NullCurve, $"Curve {i}: null; skipped.", curveA: i));
                continue;
            }

            Polyline polyline;
            if (!curve.TryGetPolyline(out polyline))
            {
                using PolylineCurve? poly = curve.ToPolyline(chordTol, angleTol, 0.0, maxEdgeLength);
                if (poly == null || !poly.TryGetPolyline(out polyline))
                {
                    report.Add(new ReportEntry(ReportLevel.Warning, ReportReason.TessellationFailed, $"Curve {i}: tessellation failed; skipped.", curveA: i));
                    continue;
                }
            }

            bool isClosed = curve.IsClosed || IsNearlyClosed(polyline, geometryTolerance);
            Point3d[] points = CleanupPolyline(polyline, geometryTolerance);
            if (isClosed && points.Length > 1 && Distance2D(points[0], points[^1]) <= DuplicateTolerance(geometryTolerance))
                points = points.Take(points.Length - 1).ToArray();

            int minPoints = isClosed ? 3 : 2;
            if (points.Length < minPoints)
            {
                string shape = isClosed ? "closed" : "open";
                report.Add(new ReportEntry(ReportLevel.Warning, ReportReason.TooShort, $"Curve {i}: too short after cleanup for {shape} wall input; skipped.", curveA: i));
                continue;
            }

            if (HasSelfIntersection(points, isClosed, geometryTolerance))
            {
                report.Add(new ReportEntry(ReportLevel.Warning, ReportReason.SelfIntersectingRail, $"Curve {i}: possible self-intersecting wall rail; continuing.", curveA: i));
            }

            result.Add(new PreparedCurve(result.Count, i, 0, points, isClosed));
        }

        return result;
    }

    private static List<Pair> PairCurves(List<PreparedCurve> curves, double maxWallWidth, double geometryTolerance, List<ReportEntry> report)
    {
        Dictionary<int, CurveBounds> boundsByCurve = BuildCurveBounds(curves, maxWallWidth);
        var candidatesByCurve = BuildPairingCandidates(curves, boundsByCurve, maxWallWidth, geometryTolerance);
        var byWorkIndex = curves.ToDictionary(curve => curve.WorkIndex);
        var bestFor = new Dictionary<int, (CandidateStats Best, CandidateStats? Second)>();

        foreach (PreparedCurve curve in curves)
        {
            if (curve.Length < 2.0 * geometryTolerance)
                continue;

            List<CandidateStats> candidates = candidatesByCurve[curve.WorkIndex]
                .Select(candidate => ScoreCandidate(curve, candidate, maxWallWidth))
                .OrderBy(candidate => candidate.Cost)
                .ToList();

            if (candidates.Count > 0)
                bestFor[curve.WorkIndex] = (candidates[0], candidates.Count > 1 ? candidates[1] : null);
        }

        var pairs = new List<Pair>();
        var used = new HashSet<(int, int)>();
        var rejected = new HashSet<int>();
        foreach (var entry in bestFor)
        {
            int aWorkIndex = entry.Key;
            CandidateStats aBest = entry.Value.Best;
            int bWorkIndex = aBest.Index;
            if (!bestFor.TryGetValue(bWorkIndex, out var bEntry) || bEntry.Best.Index != aWorkIndex)
                continue;

            int lo = Math.Min(aWorkIndex, bWorkIndex);
            int hi = Math.Max(aWorkIndex, bWorkIndex);
            if (!used.Add((lo, hi)))
                continue;

            PreparedCurve baseA = byWorkIndex[aWorkIndex];
            PreparedCurve baseB = byWorkIndex[bWorkIndex];
            string aLabel = FormatCurveRef(baseA);
            string bLabel = FormatCurveRef(baseB);
            CandidateStats bBest = bEntry.Best;
            if (IsAmbiguous(aBest, entry.Value.Second, maxWallWidth) || IsAmbiguous(bBest, bEntry.Second, maxWallWidth))
            {
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.AmbiguousPair,
                    $"Pair ({aLabel}, {bLabel}) skipped: second-best pairing candidate is within {AmbiguityCostRatio:0.###}x of the best candidate.",
                    baseA.SourceIndex,
                    baseB.SourceIndex));
                rejected.Add(aWorkIndex);
                rejected.Add(bWorkIndex);
                continue;
            }

            if (!PassesDistanceChecks(aBest, maxWallWidth) || !PassesDistanceChecks(bBest, maxWallWidth))
            {
                double mean = Math.Max(aBest.Mean, bBest.Mean);
                double max = Math.Max(aBest.Max, bBest.Max);
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.PairDistanceRejected,
                    $"Pair ({aLabel}, {bLabel}) skipped: rail spacing exceeds max wall width; mean {mean:G4}, max {max:G4}, max wall width {maxWallWidth:G4}.",
                    baseA.SourceIndex,
                    baseB.SourceIndex));
                rejected.Add(aWorkIndex);
                rejected.Add(bWorkIndex);
                continue;
            }

            PreparedCurve a = baseA.Clone();
            PreparedCurve b = baseB.Clone();
            if (a.IsClosed)
            {
                AlignClosedPair(a, b, geometryTolerance);
                report.Add(new ReportEntry(
                    ReportLevel.Info,
                    ReportReason.None,
                    $"Pair ({aLabel}, {bLabel}): closed loops accepted as one continuous wall ring.",
                    a.SourceIndex,
                    b.SourceIndex));
            }
            else if (ShouldFlip(a, b))
            {
                b.Reverse();
            }

            pairs.Add(new Pair(a, b));
        }

        var paired = new HashSet<(int SourceIndex, int FragmentIndex)>(
            pairs.SelectMany(pair => new[]
            {
                (pair.A.SourceIndex, pair.A.FragmentIndex),
                (pair.B.SourceIndex, pair.B.FragmentIndex)
            }));
        foreach (PreparedCurve curve in curves)
        {
            if (curve.Length < 2.0 * geometryTolerance ||
                paired.Contains((curve.SourceIndex, curve.FragmentIndex)) ||
                rejected.Contains(curve.WorkIndex))
            {
                continue;
            }

            if (bestFor.TryGetValue(curve.WorkIndex, out var bestEntry) &&
                IsAmbiguous(bestEntry.Best, bestEntry.Second, maxWallWidth))
            {
                int otherWorkIndex = bestEntry.Best.Index;
                PreparedCurve other = byWorkIndex[otherWorkIndex];
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.AmbiguousPair,
                    $"Curve {FormatCurveRef(curve)}: no pair selected because its second-best candidate is within {AmbiguityCostRatio:0.###}x of candidate {FormatCurveRef(other)}.",
                    curve.SourceIndex,
                    other.SourceIndex));
                continue;
            }

            int mismatch = FindMismatchedClosureCandidate(curve, curves, maxWallWidth, geometryTolerance);
            if (mismatch >= 0)
            {
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.MixedOpenClosed,
                    $"Curve {curve.SourceIndex}: nearest candidate {mismatch} has mismatched closed/open state; skipped.",
                    curve.SourceIndex,
                    mismatch));
            }
            else
            {
                report.Add(new ReportEntry(ReportLevel.Warning, ReportReason.NoPair, $"Curve {curve.SourceIndex}: no mutual pair found.", curveA: curve.SourceIndex));
            }
        }

        return pairs;
    }

    private static List<PlannedWall> BuildWalls(List<Pair> pairs, double tolerance, List<ReportEntry> report)
    {
        var walls = new List<PlannedWall>();
        int pairIndex = 0;
        foreach (Pair pair in pairs)
        {
            pairIndex++;
            if (pair.Failed)
                continue;

            double minWidth = EstimateMinGap(pair, tolerance);
            double minAllowedWidth = MinAllowedWidth(tolerance);
            if (minWidth < minAllowedWidth)
            {
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.SubToleranceWidth,
                    $"Pair ({pair.A.SourceIndex}, {pair.B.SourceIndex}) skipped: minimum rail spacing {minWidth:G4} is below {minAllowedWidth:G4}.",
                    pair.A.SourceIndex,
                    pair.B.SourceIndex,
                    pairIndex));
                continue;
            }

            bool aIsToe = DetermineToeByMeanZ(pair.A, pair.B);
            PreparedCurve toeCurve = aIsToe ? pair.A : pair.B;
            PreparedCurve topCurve = aIsToe ? pair.B : pair.A;

            double maxHeight = EstimateMaxHeight(toeCurve, topCurve);
            double minAllowedHeight = MinAllowedHeight(tolerance);
            if (maxHeight < minAllowedHeight)
            {
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.SolidFailed,
                    $"Pair ({pair.A.SourceIndex}, {pair.B.SourceIndex}) skipped: maximum wall height {maxHeight:G4} is below {minAllowedHeight:G4}.",
                    pair.A.SourceIndex,
                    pair.B.SourceIndex,
                    pairIndex));
                continue;
            }

            Point3d[] toePts = (Point3d[])toeCurve.Points.Clone();
            Point3d[] topPts = (Point3d[])topCurve.Points.Clone();
            var rails = new WallRails(toePts, topPts, pair.IsClosed, minWidth);

            Brep? brep = RetainingWallBrepBuilder.Build(toePts, topPts, tolerance, pair.IsClosed);
            if (brep == null)
            {
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.SolidFailed,
                    $"Pair ({pair.A.SourceIndex}, {pair.B.SourceIndex}): solid wall Brep generation failed; breaklines will still be inserted.",
                    pair.A.SourceIndex,
                    pair.B.SourceIndex,
                    pairIndex));
            }
            else if (!brep.IsSolid)
            {
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.SolidFailed,
                    $"Pair ({pair.A.SourceIndex}, {pair.B.SourceIndex}): solid wall Brep is open, usually because the wall tapers to zero height at an end; breaklines will still be inserted.",
                    pair.A.SourceIndex,
                    pair.B.SourceIndex,
                    pairIndex));
            }

            walls.Add(new PlannedWall(rails, brep, pair.A.SourceIndex, pair.B.SourceIndex, pair.GetPairLine()));
        }

        return walls;
    }

    private static bool DetermineToeByMeanZ(PreparedCurve a, PreparedCurve b)
    {
        double meanA = MeanZ(a.Points);
        double meanB = MeanZ(b.Points);
        return meanA <= meanB;
    }

    private static double MeanZ(Point3d[] points)
    {
        if (points.Length == 0)
            return 0.0;

        double sum = 0.0;
        foreach (Point3d point in points)
            sum += point.Z;

        return sum / points.Length;
    }

    private static double EstimateMaxHeight(PreparedCurve toe, PreparedCurve top)
    {
        double low = double.MaxValue;
        double high = double.MinValue;
        foreach (Point3d point in toe.Points)
        {
            if (point.Z < low) low = point.Z;
            if (point.Z > high) high = point.Z;
        }

        foreach (Point3d point in top.Points)
        {
            if (point.Z < low) low = point.Z;
            if (point.Z > high) high = point.Z;
        }

        return high < low ? 0.0 : high - low;
    }

    private static CandidateStats ScoreCandidate(PreparedCurve a, PreparedCurve b, double tolerance)
    {
        int n = Math.Clamp((int)Math.Round(a.Length / Math.Max(tolerance, 1e-9)), 8, 64);
        Point3d[] sample = SampleByCount(a, n);
        var distances = new double[n];
        for (int i = 0; i < n; i++)
            distances[i] = DistancePointPolyline2D(sample[i], b.Points, b.IsClosed);

        double mean = distances.Average();
        double iqr = Iqr(distances);
        double max = distances.Max();
        double cost = mean + (0.5 * iqr) + (0.25 * max);
        return new CandidateStats(b.WorkIndex, mean, iqr, max, cost);
    }

    private static bool IsAmbiguous(CandidateStats best, CandidateStats? second, double tolerance) =>
        second.HasValue && second.Value.Cost < Math.Max(best.Cost * AmbiguityCostRatio, tolerance);

    private static string FormatCurveRef(PreparedCurve curve) =>
        curve.FragmentIndex == 0 ? curve.SourceIndex.ToString() : $"{curve.SourceIndex}.{curve.FragmentIndex}";

    private static bool PassesDistanceChecks(CandidateStats candidate, double tolerance) =>
        candidate.Mean <= tolerance &&
        candidate.Iqr <= tolerance &&
        candidate.Max <= MaxPairDistanceFactor * tolerance;

    private static void DetectCrossings(List<Pair> pairs, double tolerance, List<ReportEntry> report)
    {
        for (int i = 0; i < pairs.Count; i++)
        {
            Pair pair = pairs[i];
            if (pair.Failed)
                continue;

            Point3d[] centerline = BuildCenterline(pair);
            for (int j = i + 1; j < pairs.Count; j++)
            {
                Pair other = pairs[j];
                if (other.Failed)
                    continue;

                Point3d[] otherCenterline = BuildCenterline(other);
                if (CenterlinesCross(centerline, pair.IsClosed, otherCenterline, other.IsClosed, out double angleDeg))
                {
                    report.Add(new ReportEntry(
                        ReportLevel.Warning,
                        ReportReason.CrossingWalls,
                        $"Crossing wall centerlines detected between pairs ({pair.A.SourceIndex}, {pair.B.SourceIndex}) and ({other.A.SourceIndex}, {other.B.SourceIndex}) at {angleDeg:0.#} degrees; both pairs remain enabled.",
                        pair.A.SourceIndex,
                        pair.B.SourceIndex));
                    break;
                }
            }
        }
    }

    private static Point3d[] BuildCenterline(Pair pair)
    {
        var fractions = new SortedSet<double>(FractionComparer.Instance);
        if (!pair.IsClosed)
        {
            fractions.Add(0.0);
            fractions.Add(1.0);
        }

        AddCurveFractions(fractions, pair.A);
        AddCurveFractions(fractions, pair.B);
        if (fractions.Count == 0)
            fractions.Add(0.0);

        return fractions
            .Select(fraction =>
            {
                Point3d a = PointAt(pair.A.Points, pair.A.CumLen, pair.A.Length, pair.A.IsClosed, pair.A.Length * fraction);
                Point3d b = PointAt(pair.B.Points, pair.B.CumLen, pair.B.Length, pair.B.IsClosed, pair.B.Length * fraction);
                return new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            })
            .ToArray();
    }

    private static void AddCurveFractions(SortedSet<double> fractions, PreparedCurve curve)
    {
        if (curve.Length <= 1e-12)
            return;

        int start = curve.IsClosed ? 0 : 1;
        int end = curve.IsClosed ? curve.Points.Length : curve.Points.Length - 1;
        for (int i = start; i < end; i++)
        {
            double fraction = curve.CumLen[i] / curve.Length;
            fractions.Add(curve.IsClosed ? NormalizeFraction(fraction) : Math.Clamp(fraction, 0.0, 1.0));
        }
    }

    private static bool CenterlinesCross(Point3d[] a, bool aClosed, Point3d[] b, bool bClosed, out double angleDeg)
    {
        angleDeg = 0.0;
        int aSegments = aClosed ? a.Length : a.Length - 1;
        int bSegments = bClosed ? b.Length : b.Length - 1;
        for (int i = 0; i < aSegments; i++)
        {
            int iNext = (i + 1) % a.Length;
            Line la = new(a[i], a[iNext]);
            for (int j = 0; j < bSegments; j++)
            {
                int jNext = (j + 1) % b.Length;
                Line lb = new(b[j], b[jNext]);
                if (!TrySegmentIntersection(la, lb, out double t, out double u, out double candidateAngle))
                    continue;

                if (candidateAngle <= 10.0)
                    continue;

                bool aEndpoint = t <= 1e-5 || t >= 1.0 - 1e-5;
                bool bEndpoint = u <= 1e-5 || u >= 1.0 - 1e-5;
                if (aEndpoint && bEndpoint)
                    continue;

                angleDeg = candidateAngle;
                return true;
            }
        }

        return false;
    }

    private static void ResolveCorners(List<Pair> pairs, double tolerance, List<ReportEntry> report)
    {
        for (int i = 0; i < pairs.Count; i++)
        {
            Pair pair = pairs[i];
            if (pair.Failed || pair.IsClosed)
                continue;

            for (int j = i + 1; j < pairs.Count; j++)
            {
                Pair other = pairs[j];
                if (other.Failed || other.IsClosed)
                    continue;

                bool resolvedThisPair = false;
                foreach (bool pairStart in new[] { true, false })
                {
                    foreach (bool otherStart in new[] { true, false })
                    {
                        if (Distance2D(pair.EndMid(pairStart), other.EndMid(otherStart)) > tolerance)
                            continue;

                        if (TryResolveRailCorner(pair, pairStart, other, otherStart, tolerance))
                        {
                            report.Add(new ReportEntry(
                                ReportLevel.Info,
                                ReportReason.CornerResolved,
                                $"Corner resolved between pairs ({pair.A.SourceIndex}, {pair.B.SourceIndex}) and ({other.A.SourceIndex}, {other.B.SourceIndex}) with bounded miter.",
                                pair.A.SourceIndex,
                                pair.B.SourceIndex));
                            resolvedThisPair = true;
                        }
                        else
                        {
                            report.Add(new ReportEntry(
                                ReportLevel.Warning,
                                ReportReason.CornerRejected,
                                $"Corner join skipped between pairs ({pair.A.SourceIndex}, {pair.B.SourceIndex}) and ({other.A.SourceIndex}, {other.B.SourceIndex}): miter collapsed or inverted rail spacing.",
                                pair.A.SourceIndex,
                                pair.B.SourceIndex));
                        }

                        break;
                    }

                    if (resolvedThisPair)
                        break;
                }
            }
        }
    }

    private static bool TryResolveRailCorner(Pair pair, bool pairStart, Pair other, bool otherStart, double tolerance)
    {
        CurveEnd pairA = GetCurveEnd(pair.A, pairStart);
        CurveEnd pairB = GetCurveEnd(pair.B, pairStart);
        CurveEnd otherA = GetCurveEnd(other.A, otherStart);
        CurveEnd otherB = GetCurveEnd(other.B, otherStart);

        bool directMatch =
            Distance2D(pairA.Point, otherA.Point) + Distance2D(pairB.Point, otherB.Point) <=
            Distance2D(pairA.Point, otherB.Point) + Distance2D(pairB.Point, otherA.Point);

        CurveEnd match0 = directMatch ? otherA : otherB;
        CurveEnd match1 = directMatch ? otherB : otherA;
        double minWallWidth = Math.Min(EstimateMinGap(pair, tolerance), EstimateMinGap(other, tolerance));
        double budget = Math.Max(2.0 * minWallWidth, 4.0 * tolerance);

        Point2d corner0 = BoundedIntersection(pairA, match0, budget);
        Point2d corner1 = BoundedIntersection(pairB, match1, budget);
        double resolvedWidth = Distance2D(corner0, corner1);
        if (resolvedWidth < MinAllowedWidth(tolerance))
            return false;

        Point3d resolved0 = new(corner0.X, corner0.Y, (pairA.Point.Z + match0.Point.Z) * 0.5);
        Point3d resolved1 = new(corner1.X, corner1.Y, (pairB.Point.Z + match1.Point.Z) * 0.5);

        pair.A.SetEnd(pairStart, resolved0);
        pair.B.SetEnd(pairStart, resolved1);
        if (directMatch)
        {
            other.A.SetEnd(otherStart, resolved0);
            other.B.SetEnd(otherStart, resolved1);
        }
        else
        {
            other.B.SetEnd(otherStart, resolved0);
            other.A.SetEnd(otherStart, resolved1);
        }

        return true;
    }

    private static Point2d BoundedIntersection(CurveEnd a, CurveEnd b, double budget)
    {
        Point2d fallback = new((a.Point.X + b.Point.X) * 0.5, (a.Point.Y + b.Point.Y) * 0.5);
        if (!TryLineIntersection(a.Point, a.Direction, b.Point, b.Direction, out Point2d intersection))
            return fallback;

        if (Distance2D(intersection, new Point2d(a.Point.X, a.Point.Y)) > budget ||
            Distance2D(intersection, new Point2d(b.Point.X, b.Point.Y)) > budget)
        {
            return fallback;
        }

        return intersection;
    }

    private static void AlignClosedPair(PreparedCurve a, PreparedCurve b, double tolerance)
    {
        int sampleCount = Math.Clamp((int)Math.Round(Math.Min(a.Length, b.Length) / Math.Max(tolerance, 1e-9)), 16, 64);
        double bestGap = double.MaxValue;
        bool reverse = false;
        double bestOffset = 0.0;

        for (int direction = 0; direction < 2; direction++)
        {
            Point3d[] candidatePoints = (Point3d[])b.Points.Clone();
            if (direction == 1)
                Array.Reverse(candidatePoints);

            double[] candidateCumLen = BuildCumLen(candidatePoints);
            double candidateLength = ComputeTotalLength(candidatePoints, true, candidateCumLen);
            for (int shift = 0; shift < sampleCount; shift++)
            {
                double offset = candidateLength * shift / sampleCount;
                double gap = AverageAlignedGap(a, candidatePoints, candidateCumLen, candidateLength, offset, sampleCount);
                if (gap >= bestGap)
                    continue;

                bestGap = gap;
                reverse = direction == 1;
                bestOffset = offset;
            }
        }

        if (reverse)
            b.Reverse();

        b.RotateClosed(bestOffset, tolerance);
    }

    private static double AverageAlignedGap(
        PreparedCurve a,
        Point3d[] bPoints,
        double[] bCumLen,
        double bLength,
        double offset,
        int sampleCount)
    {
        double sum = 0.0;
        for (int i = 0; i < sampleCount; i++)
        {
            double alongA = a.Length * i / sampleCount;
            double alongB = ModLength(offset + (bLength * i / sampleCount), bLength);
            Point3d pa = PointAt(a.Points, a.CumLen, a.Length, true, alongA);
            Point3d pb = PointAt(bPoints, bCumLen, bLength, true, alongB);
            sum += Distance2D(pa, pb);
        }

        return sum / sampleCount;
    }

    private static int FindMismatchedClosureCandidate(PreparedCurve curve, List<PreparedCurve> curves, double maxWallWidth, double geometryTolerance)
    {
        int sampleCount = Math.Clamp((int)Math.Round(curve.Length / Math.Max(maxWallWidth, 1e-9)), 8, 32);
        Point3d[] sample = SampleByCount(curve, sampleCount);
        int bestIndex = -1;
        double bestMean = double.MaxValue;

        foreach (PreparedCurve other in curves)
        {
            if (other.SourceIndex == curve.SourceIndex || other.IsClosed == curve.IsClosed || other.Length < 2.0 * geometryTolerance)
                continue;

            double[] dist = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                dist[i] = DistancePointPolyline2D(sample[i], other.Points, other.IsClosed);

            double mean = dist.Average();
            double max = dist.Max();
            if (mean > maxWallWidth || max > MaxPairDistanceFactor * maxWallWidth || mean >= bestMean)
                continue;

            bestMean = mean;
            bestIndex = other.SourceIndex;
        }

        return bestIndex;
    }

    private static Dictionary<int, CurveBounds> BuildCurveBounds(IEnumerable<PreparedCurve> curves, double maxWallWidth)
    {
        double padding = Math.Max(maxWallWidth * 1.5, 1e-6);
        return curves.ToDictionary(curve => curve.WorkIndex, curve => ComputeCurveBounds(curve.Points, padding));
    }

    private static Dictionary<int, List<PreparedCurve>> BuildPairingCandidates(
        IReadOnlyList<PreparedCurve> curves,
        IReadOnlyDictionary<int, CurveBounds> boundsByCurve,
        double maxWallWidth,
        double geometryTolerance)
    {
        double maxCandidateGap = Math.Max(maxWallWidth * 3.0, 1e-6);
        var candidates = new Dictionary<int, List<PreparedCurve>>(curves.Count);
        foreach (PreparedCurve curve in curves)
        {
            var matches = new List<PreparedCurve>();
            if (curve.Length >= 2.0 * geometryTolerance)
            {
                CurveBounds curveBounds = boundsByCurve[curve.WorkIndex];
                foreach (PreparedCurve other in curves)
                {
                    if (other.SourceIndex == curve.SourceIndex || other.IsClosed != curve.IsClosed || other.Length < 2.0 * geometryTolerance)
                        continue;

                    if (curveBounds.GapTo(boundsByCurve[other.WorkIndex]) <= maxCandidateGap)
                        matches.Add(other);
                }
            }

            candidates[curve.WorkIndex] = matches;
        }

        return candidates;
    }

    private static CurveBounds ComputeCurveBounds(Point3d[] points, double padding)
    {
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach (Point3d point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return new CurveBounds(minX - padding, minY - padding, maxX + padding, maxY + padding);
    }

    private static bool ShouldFlip(PreparedCurve a, PreparedCurve b)
    {
        Point3d a0 = a.Points[0];
        Point3d a1 = a.Points[^1];
        Point3d b0 = b.Points[0];
        Point3d b1 = b.Points[^1];
        double forward = Distance2D(a0, b0) + Distance2D(a1, b1);
        double reverse = Distance2D(a0, b1) + Distance2D(a1, b0);
        return reverse < forward;
    }

    private static double EstimateMinGap(Pair pair, double tolerance)
    {
        int sampleCount = Math.Clamp((int)Math.Round(Math.Min(pair.A.Length, pair.B.Length) / Math.Max(tolerance, 1e-9)), 8, 64);
        double min = double.MaxValue;
        for (int i = 0; i < sampleCount; i++)
        {
            double t = pair.IsClosed ? (double)i / sampleCount : (double)i / Math.Max(1, sampleCount - 1);
            Point3d a = PointAt(pair.A.Points, pair.A.CumLen, pair.A.Length, pair.A.IsClosed, pair.A.Length * t);
            Point3d b = PointAt(pair.B.Points, pair.B.CumLen, pair.B.Length, pair.B.IsClosed, pair.B.Length * t);
            min = Math.Min(min, Distance2D(a, b));
        }

        return min < double.MaxValue ? min : tolerance;
    }

    private static double MinAllowedHeight(double tolerance) => Math.Max(tolerance * 0.01, 1e-6);

    private static bool TrySegmentIntersection(Line a, Line b, out double t, out double u, out double angleDeg)
    {
        t = 0.0;
        u = 0.0;
        angleDeg = 0.0;

        Point2d p = new(a.FromX, a.FromY);
        Point2d r = new(a.ToX - a.FromX, a.ToY - a.FromY);
        Point2d q = new(b.FromX, b.FromY);
        Point2d s = new(b.ToX - b.FromX, b.ToY - b.FromY);
        if (!Solve2x2(r.X, -s.X, r.Y, -s.Y, q.X - p.X, q.Y - p.Y, out t, out u))
            return false;

        double lr = Math.Sqrt((r.X * r.X) + (r.Y * r.Y));
        double ls = Math.Sqrt((s.X * s.X) + (s.Y * s.Y));
        if (lr < 1e-12 || ls < 1e-12)
            return false;

        double cos = Math.Clamp(((r.X * s.X) + (r.Y * s.Y)) / (lr * ls), -1.0, 1.0);
        angleDeg = Math.Acos(Math.Abs(cos)) * 180.0 / Math.PI;
        return true;
    }

    private static CurveEnd GetCurveEnd(PreparedCurve curve, bool start)
    {
        int index = start ? 0 : curve.Points.Length - 1;
        int next = start ? 1 : curve.Points.Length - 2;
        Point3d point = curve.Points[index];
        Point2d direction = Unit(new Point2d(curve.Points[next].X - point.X, curve.Points[next].Y - point.Y));
        return new CurveEnd(point, direction);
    }

    private static bool TryLineIntersection(Point3d a, Point2d da, Point3d b, Point2d db, out Point2d point)
    {
        point = Point2d.Unset;
        if (!Solve2x2(da.X, -db.X, da.Y, -db.Y, b.X - a.X, b.Y - a.Y, out double ta, out _))
            return false;

        point = new Point2d(a.X + (da.X * ta), a.Y + (da.Y * ta));
        return true;
    }

    private static Point2d TangentAt(PreparedCurve curve, double along)
    {
        if (curve.Points.Length < 2)
            return new Point2d(1.0, 0.0);

        if (!curve.IsClosed)
        {
            if (along <= 0.0)
                return Unit(new Point2d(curve.Points[1].X - curve.Points[0].X, curve.Points[1].Y - curve.Points[0].Y));
            if (along >= curve.Length)
                return Unit(new Point2d(curve.Points[^1].X - curve.Points[^2].X, curve.Points[^1].Y - curve.Points[^2].Y));
        }

        double normalized = curve.IsClosed ? ModLength(along, curve.Length) : Math.Clamp(along, 0.0, curve.Length);
        int idx = Array.BinarySearch(curve.CumLen, normalized);
        if (idx < 0)
            idx = Math.Max(0, ~idx - 1);

        int next = (idx + 1) % curve.Points.Length;
        if (!curve.IsClosed)
            next = Math.Min(curve.Points.Length - 1, idx + 1);

        if (idx == next)
            idx = Math.Max(0, next - 1);

        return Unit(new Point2d(curve.Points[next].X - curve.Points[idx].X, curve.Points[next].Y - curve.Points[idx].Y));
    }

    private static Point2d Unit(Point2d vector)
    {
        double length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
        return length <= 1e-12 ? new Point2d(1.0, 0.0) : new Point2d(vector.X / length, vector.Y / length);
    }

    private static Point3d[] CleanupPolyline(Polyline polyline, double tolerance)
    {
        double dedupTol = DuplicateTolerance(tolerance);
        var points = new List<Point3d>(polyline.Count);
        for (int i = 0; i < polyline.Count; i++)
        {
            if (points.Count == 0 || points[^1].DistanceTo(polyline[i]) > dedupTol)
                points.Add(polyline[i]);
        }

        if (points.Count > 1 && points[0].DistanceTo(points[^1]) <= dedupTol)
            points.RemoveAt(points.Count - 1);

        return points.ToArray();
    }

    private static bool IsNearlyClosed(Polyline polyline, double tolerance) =>
        polyline.Count >= 3 && polyline[0].DistanceTo(polyline[^1]) <= DuplicateTolerance(tolerance);

    private static bool HasSelfIntersection(Point3d[] points, bool isClosed, double tolerance)
    {
        int segmentCount = isClosed ? points.Length : points.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            int iNext = (i + 1) % points.Length;
            for (int j = i + 1; j < segmentCount; j++)
            {
                int jNext = (j + 1) % points.Length;
                bool adjacent = i == j || iNext == j || jNext == i;
                if (isClosed && i == 0 && jNext == 0)
                    adjacent = true;
                if (adjacent)
                    continue;

                if (TrySegmentsIntersect2D(points[i], points[iNext], points[j], points[jNext], tolerance))
                    return true;
            }
        }

        return false;
    }

    private static bool TrySegmentsIntersect2D(Point3d a0, Point3d a1, Point3d b0, Point3d b1, double tolerance)
    {
        Point2d r = new(a1.X - a0.X, a1.Y - a0.Y);
        Point2d s = new(b1.X - b0.X, b1.Y - b0.Y);
        if (!Solve2x2(r.X, -s.X, r.Y, -s.Y, b0.X - a0.X, b0.Y - a0.Y, out double t, out double u))
            return false;

        double eps = Math.Max(tolerance * 1e-6, 1e-9);
        return t > eps && t < 1.0 - eps && u > eps && u < 1.0 - eps;
    }

    private static Point3d[] SampleByCount(PreparedCurve curve, int count)
    {
        var sample = new Point3d[count];
        for (int i = 0; i < count; i++)
        {
            double t = curve.IsClosed ? (double)i / count : (double)i / Math.Max(1, count - 1);
            sample[i] = PointAt(curve.Points, curve.CumLen, curve.Length, curve.IsClosed, curve.Length * t);
        }

        return sample;
    }

    private static double[] BuildCumLen(Point3d[] points)
    {
        var cum = new double[points.Length];
        for (int i = 1; i < points.Length; i++)
            cum[i] = cum[i - 1] + Distance2D(points[i - 1], points[i]);

        return cum;
    }

    private static double ComputeTotalLength(Point3d[] points, bool isClosed, double[] cumLen)
    {
        if (points.Length == 0)
            return 0.0;

        double total = cumLen[^1];
        if (isClosed && points.Length > 1)
            total += Distance2D(points[^1], points[0]);

        return total;
    }

    private static Point3d PointAt(Point3d[] points, double[] cumLen, double totalLength, bool isClosed, double along)
    {
        if (points.Length == 0)
            return Point3d.Unset;

        if (!isClosed)
            return PointAtOpen(points, cumLen, along);

        if (totalLength <= 1e-12)
            return points[0];

        along = ModLength(along, totalLength);
        double openLength = cumLen[^1];
        if (along <= openLength)
            return PointAtOpen(points, cumLen, along);

        double closingLength = totalLength - openLength;
        if (closingLength <= 1e-12)
            return points[0];

        double t = (along - openLength) / closingLength;
        return new Point3d(
            points[^1].X + ((points[0].X - points[^1].X) * t),
            points[^1].Y + ((points[0].Y - points[^1].Y) * t),
            points[^1].Z + ((points[0].Z - points[^1].Z) * t));
    }

    private static Point3d PointAtOpen(Point3d[] points, double[] cumLen, double along)
    {
        if (along <= 0.0)
            return points[0];

        double total = cumLen[^1];
        if (along >= total)
            return points[^1];

        int idx = Array.BinarySearch(cumLen, along);
        if (idx >= 0)
            return points[idx];

        idx = ~idx;
        int i0 = Math.Max(0, idx - 1);
        int i1 = Math.Min(points.Length - 1, idx);
        double s0 = cumLen[i0];
        double s1 = cumLen[i1];
        double t = s1 - s0 < 1e-12 ? 0.0 : (along - s0) / (s1 - s0);
        return new Point3d(
            points[i0].X + ((points[i1].X - points[i0].X) * t),
            points[i0].Y + ((points[i1].Y - points[i0].Y) * t),
            points[i0].Z + ((points[i1].Z - points[i0].Z) * t));
    }

    private static double DistancePointPolyline2D(Point3d p, Point3d[] poly, bool isClosed)
    {
        if (poly.Length < 2)
            return double.MaxValue;

        double best = double.MaxValue;
        int segmentCount = isClosed ? poly.Length : poly.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            int next = (i + 1) % poly.Length;
            best = Math.Min(best, DistancePointSegment2D(p, poly[i], poly[next]));
        }

        return best;
    }

    private static double DistancePointSegment2D(Point3d p, Point3d a, Point3d b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = (dx * dx) + (dy * dy);
        if (lenSq < 1e-18)
            return Distance2D(p, a);

        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        double cx = a.X + (t * dx);
        double cy = a.Y + (t * dy);
        return Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy)));
    }

    private static Point3d[] RotateClosedPoints(Point3d[] points, double[] cumLen, double totalLength, double along, double tolerance)
    {
        if (points.Length < 3 || totalLength <= 1e-12)
            return points;

        double pointTol = DuplicateTolerance(tolerance);
        along = ModLength(along, totalLength);
        if (along <= pointTol)
            return points;

        int vertexIndex = FindClosedVertexIndex(cumLen, totalLength, along, pointTol);
        if (vertexIndex >= 0)
            return RotateArray(points, vertexIndex);

        Point3d seam = PointAt(points, cumLen, totalLength, true, along);
        int segmentIndex = FindClosedSegmentIndex(cumLen, totalLength, along);
        int next = (segmentIndex + 1) % points.Length;
        var rotated = new List<Point3d>(points.Length + 1) { seam };
        for (int i = 0; i < points.Length; i++)
            rotated.Add(points[(next + i) % points.Length]);

        return CleanupRotatedClosedPoints(rotated, pointTol);
    }

    private static int FindClosedVertexIndex(double[] cumLen, double totalLength, double along, double tolerance)
    {
        for (int i = 0; i < cumLen.Length; i++)
        {
            double vertexAlong = i == 0 ? 0.0 : cumLen[i];
            if (Math.Abs(ModLength(along - vertexAlong, totalLength)) <= tolerance ||
                Math.Abs(ModLength(vertexAlong - along, totalLength)) <= tolerance)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindClosedSegmentIndex(double[] cumLen, double totalLength, double along)
    {
        double openLength = cumLen[^1];
        if (along >= openLength)
            return cumLen.Length - 1;

        int idx = Array.BinarySearch(cumLen, along);
        if (idx >= 0)
            return idx;

        idx = ~idx;
        return Math.Max(0, idx - 1);
    }

    private static Point3d[] RotateArray(Point3d[] points, int startIndex)
    {
        var rotated = new Point3d[points.Length];
        for (int i = 0; i < points.Length; i++)
            rotated[i] = points[(startIndex + i) % points.Length];

        return rotated;
    }

    private static Point3d[] CleanupRotatedClosedPoints(List<Point3d> points, double tolerance)
    {
        var cleaned = new List<Point3d>(points.Count);
        foreach (Point3d point in points)
        {
            if (cleaned.Count == 0 || cleaned[^1].DistanceTo(point) > tolerance)
                cleaned.Add(point);
        }

        if (cleaned.Count > 1 && cleaned[0].DistanceTo(cleaned[^1]) <= tolerance)
            cleaned.RemoveAt(cleaned.Count - 1);

        return cleaned.ToArray();
    }

    private static double Iqr(double[] values)
    {
        if (values.Length == 0)
            return 0.0;

        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return Percentile(sorted, 0.75) - Percentile(sorted, 0.25);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1)
            return sorted[0];

        double x = p * (sorted.Length - 1);
        int i0 = (int)Math.Floor(x);
        int i1 = Math.Min(sorted.Length - 1, i0 + 1);
        double t = x - i0;
        return sorted[i0] + ((sorted[i1] - sorted[i0]) * t);
    }

    private static bool Solve2x2(double a11, double a12, double a21, double a22, double b1, double b2, out double x1, out double x2)
    {
        x1 = 0.0;
        x2 = 0.0;
        double det = (a11 * a22) - (a12 * a21);
        if (Math.Abs(det) < 1e-12)
            return false;

        x1 = ((b1 * a22) - (b2 * a12)) / det;
        x2 = ((a11 * b2) - (a21 * b1)) / det;
        return true;
    }

    private static double Distance2D(Point3d a, Point3d b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double Distance2D(Point2d a, Point2d b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool SamePoint3D(Point3d a, Point3d b, double tolerance)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        double dz = a.Z - b.Z;
        return ((dx * dx) + (dy * dy) + (dz * dz)) <= tolerance * tolerance;
    }

    private static double ModLength(double value, double modulus)
    {
        if (modulus <= 0.0)
            return 0.0;

        double result = value % modulus;
        if (result < 0.0)
            result += modulus;

        return result;
    }

    private static double DuplicateTolerance(double tolerance) => Math.Max(tolerance * 1e-3, 1e-8);

    private static double MinAllowedWidth(double tolerance) => Math.Max(tolerance * 0.1, 1e-6);

    private static double NormalizeFraction(double fraction)
    {
        double normalized = fraction % 1.0;
        if (normalized < 0.0)
            normalized += 1.0;

        return normalized;
    }

    private sealed class FractionComparer : IComparer<double>
    {
        public static FractionComparer Instance { get; } = new();

        public int Compare(double x, double y)
        {
            double delta = x - y;
            if (Math.Abs(delta) <= 1e-9)
                return 0;

            return delta < 0.0 ? -1 : 1;
        }
    }
}
