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
        RailDetailSimplified,
        SelfIntersectingRail,
        MixedOpenClosed,
        NoPair,
        AmbiguousPair,
        PairDistanceRejected,
        InvalidStationMapping,
        SubToleranceWidth,
        CrossingWalls,
        CornerResolved,
        CornerRejected,
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
        public Point3d? Location { get; }
        public IReadOnlyList<Line> FocusSegments { get; }
        public IReadOnlyList<int> RelatedCurves { get; }

        public ReportEntry(
            ReportLevel level,
            ReportReason reason,
            string message,
            int? curveA = null,
            int? curveB = null,
            int? pairIndex = null,
            Point3d? location = null,
            IReadOnlyList<Line>? focusSegments = null,
            IReadOnlyList<int>? relatedCurves = null)
        {
            Level = level;
            Reason = reason;
            Message = message;
            CurveA = curveA;
            CurveB = curveB;
            PairIndex = pairIndex;
            Location = location;
            FocusSegments = focusSegments?.ToArray() ?? Array.Empty<Line>();
            RelatedCurves = relatedCurves?.Distinct().ToArray() ?? new[] { curveA, curveB }
                .Where(index => index.HasValue)
                .Select(index => index!.Value)
                .Distinct()
                .ToArray();
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
        public Point3d[]? RepairPoints { get; }
        public double[] CumLen { get; private set; }
        public double Length { get; private set; }

        public PreparedCurve(
            int workIndex,
            int sourceIndex,
            int fragmentIndex,
            Point3d[] points,
            bool isClosed,
            Point3d[]? repairPoints = null)
        {
            WorkIndex = workIndex;
            SourceIndex = sourceIndex;
            FragmentIndex = fragmentIndex;
            IsClosed = isClosed;
            Points = points;
            RepairPoints = repairPoints;
            CumLen = Array.Empty<double>();
            RebuildLengths();
        }

        public PreparedCurve Clone(bool useRepair = false) => new(
            WorkIndex,
            SourceIndex,
            FragmentIndex,
            (Point3d[])(useRepair && RepairPoints != null ? RepairPoints : Points).Clone(),
            IsClosed);

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

    private enum StationMappingFailure
    {
        None,
        EndsDoNotMatch,
        ReversesDirection
    }

    private readonly record struct StationMappingCheck(
        bool IsValid,
        StationMappingFailure Failure,
        Point3d Location,
        Point3d PartnerLocation);

    private readonly record struct CandidateStats(
        int Index,
        double Mean,
        double Iqr,
        double Max,
        double Cost,
        StationMappingCheck StationMapping,
        bool UsesSourceRepair = false,
        bool UsesTargetRepair = false)
    {
        public bool HasOrderedFullCoverage => StationMapping.IsValid;
    }
    private readonly record struct CenterlineSample(Point3d Point, double MinZ, double MaxZ);
    private readonly record struct WallCrossing(
        Point3d Location,
        Line FirstSegment,
        Line SecondSegment,
        double AngleDegrees,
        bool VerticalRangesOverlap);
    private readonly record struct CurveEnd(Point3d Point, Point2d Direction);

    /// <summary>
    /// A tessellated wall rail: the planner's native-free input. <see cref="IsClosed"/> carries the
    /// source curve's closure flag; nearly-closed point sequences are also detected downstream.
    /// </summary>
    internal readonly record struct RailPolyline(Point3d[] Points, bool IsClosed);

    /// <summary>
    /// Curve-based entry point: tessellates each curve (the only step that needs the Rhino native
    /// runtime besides solid Brep generation) and hands the polylines to <see cref="PlanPolylines"/>.
    /// </summary>
    public static PlanResult Plan(
        IReadOnlyList<Curve> curves,
        double maxWallWidth,
        double? curveParsingTolerance = null,
        double? curveCleanupTolerance = null)
    {
        double resolvedMaxWallWidth = Math.Max(maxWallWidth, 1e-9);
        double geometryTolerance = ResolveGeometryTolerance(resolvedMaxWallWidth, curveParsingTolerance);
        double chordTol = Math.Max(geometryTolerance, 1e-9);
        double angleTol = 5.0 * Math.PI / 180.0;

        var report = new List<ReportEntry>();
        var rails = new RailPolyline?[curves.Count];
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
                using PolylineCurve? poly = curve.ToPolyline(chordTol, angleTol, 0.0, 0.0);
                if (poly == null || !poly.TryGetPolyline(out polyline))
                {
                    report.Add(new ReportEntry(ReportLevel.Warning, ReportReason.TessellationFailed, $"Curve {i}: tessellation failed; skipped.", curveA: i));
                    continue;
                }
            }

            rails[i] = new RailPolyline(polyline.ToArray(), curve.IsClosed);
        }

        return PlanPolylines(
            rails,
            maxWallWidth,
            curveParsingTolerance,
            curveCleanupTolerance,
            buildSolids: true,
            seedReport: report);
    }

    /// <summary>
    /// Polyline-based planner: pure managed math (no Rhino native runtime needed) unless
    /// <paramref name="buildSolids"/> is true, in which case solid wall Breps are generated for the
    /// accepted pairs. Tests exercise this entry point directly so the pairing/corner/width logic
    /// runs in hosts without the native runtime.
    /// </summary>
    internal static PlanResult PlanPolylines(
        IReadOnlyList<RailPolyline?> rails,
        double maxWallWidth,
        double? curveParsingTolerance = null,
        double? curveCleanupTolerance = null,
        bool buildSolids = true,
        List<ReportEntry>? seedReport = null)
    {
        var report = seedReport ?? new List<ReportEntry>();
        var totalTimer = Stopwatch.StartNew();
        double resolvedMaxWallWidth = Math.Max(maxWallWidth, 1e-9);
        double geometryTolerance = ResolveGeometryTolerance(resolvedMaxWallWidth, curveParsingTolerance);
        double cleanupTolerance = Math.Max(curveCleanupTolerance ?? geometryTolerance, geometryTolerance);

        var preprocessTimer = Stopwatch.StartNew();
        var prepared = PrepareCurves(rails, geometryTolerance, cleanupTolerance, report);
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
        ResolveCorners(pairs, resolvedMaxWallWidth, geometryTolerance, report);
        DetectCrossings(pairs, geometryTolerance, report);
        interactionTimer.Stop();

        var wallTimer = Stopwatch.StartNew();
        List<PlannedWall> walls = BuildWalls(pairs, resolvedMaxWallWidth, geometryTolerance, buildSolids, report);
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

    private static double ResolveGeometryTolerance(double resolvedMaxWallWidth, double? curveParsingTolerance) =>
        Math.Max(curveParsingTolerance ?? Math.Min(resolvedMaxWallWidth * 0.01, 0.001), 1e-9);

    private static List<PreparedCurve> PrepareCurves(
        IReadOnlyList<RailPolyline?> rails,
        double geometryTolerance,
        double cleanupTolerance,
        List<ReportEntry> report)
    {
        var result = new List<PreparedCurve>();

        for (int i = 0; i < rails.Count; i++)
        {
            if (rails[i] is not RailPolyline rail || rail.Points.Length == 0)
                continue;

            bool isClosed = rail.IsClosed || IsNearlyClosed(rail.Points, geometryTolerance);
            Point3d[] points = CleanupPolyline(rail.Points, geometryTolerance);
            if (isClosed && points.Length > 1 && Distance2D(points[0], points[^1]) <= DuplicateTolerance(geometryTolerance))
                points = points.Take(points.Length - 1).ToArray();

            int minPoints = isClosed ? 3 : 2;
            if (points.Length < minPoints)
            {
                string shape = isClosed ? "closed" : "open";
                report.Add(new ReportEntry(ReportLevel.Warning, ReportReason.TooShort, $"Curve {i}: too short after cleanup for {shape} wall input; skipped.", curveA: i));
                continue;
            }

            if (HasSelfIntersection(points, isClosed, geometryTolerance, out Point3d crossing, out Line firstSegment, out Line secondSegment))
            {
                Point3d[]? repaired = !isClosed
                    ? CreateRepairCandidate(points, cleanupTolerance)
                    : null;
                if (repaired != null &&
                    !HasSelfIntersection(repaired, false, geometryTolerance, out _, out _, out _))
                {
                    report.Add(CreateRailCleanupReport(i, points, repaired, crossing));
                    points = repaired;
                }
                else
                {
                    report.Add(new ReportEntry(
                        ReportLevel.Warning,
                        ReportReason.SelfIntersectingRail,
                        $"Curve {i} crosses itself in plan near the marker. Split or redraw it so the wall rail follows one continuous path without crossing itself.",
                        curveA: i,
                        location: crossing,
                        focusSegments: new[] { firstSegment, secondSegment }));
                    continue;
                }
            }

            if (!isClosed && points.Length > 512)
                points = SimplifyOpenPolyline(points, geometryTolerance);

            Point3d[]? repairPoints = !isClosed
                ? CreateRepairCandidate(points, cleanupTolerance)
                : null;
            result.Add(new PreparedCurve(result.Count, i, 0, points, isClosed, repairPoints));
        }

        return result;
    }

    private static List<Pair> PairCurves(List<PreparedCurve> curves, double maxWallWidth, double geometryTolerance, List<ReportEntry> report)
    {
        Dictionary<int, CurveBounds> boundsByCurve = BuildCurveBounds(curves, maxWallWidth);
        var candidatesByCurve = BuildPairingCandidates(curves, boundsByCurve, maxWallWidth, geometryTolerance);
        var byWorkIndex = curves.ToDictionary(curve => curve.WorkIndex);
        var bestFor = new Dictionary<int, (CandidateStats Best, CandidateStats? Second)>();
        var invalidMappingFor = new Dictionary<int, CandidateStats>();

        foreach (PreparedCurve curve in curves)
        {
            if (curve.Length < 2.0 * geometryTolerance)
                continue;

            List<CandidateStats> scoredCandidates = candidatesByCurve[curve.WorkIndex]
                .Select(candidate => ScoreCandidate(curve, candidate, maxWallWidth))
                .OrderBy(candidate => candidate.Cost)
                .ToList();
            List<CandidateStats> candidates = scoredCandidates
                .Where(candidate => candidate.HasOrderedFullCoverage)
                .ToList();

            if (candidates.Count > 0)
                bestFor[curve.WorkIndex] = (candidates[0], candidates.Count > 1 ? candidates[1] : null);
            else if (scoredCandidates.Count > 0 && PassesDistanceChecks(scoredCandidates[0], maxWallWidth))
                invalidMappingFor[curve.WorkIndex] = scoredCandidates[0];
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
                CandidateStats reportedBest = IsAmbiguous(aBest, entry.Value.Second, maxWallWidth) ? aBest : bBest;
                CandidateStats reportedSecond = (IsAmbiguous(aBest, entry.Value.Second, maxWallWidth) ? entry.Value.Second : bEntry.Second)!.Value;
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.AmbiguousPair,
                    $"Pair ({aLabel}, {bLabel}) skipped: another candidate (cost {reportedSecond.Cost:G4}) is within the ambiguity margin " +
                    $"{AmbiguityMargin(reportedBest, maxWallWidth):G4} of the best candidate (cost {reportedBest.Cost:G4}, " +
                    $"margin = max({AmbiguityCostRatio:0.###}x best cost, max wall width)).",
                    baseA.SourceIndex,
                    baseB.SourceIndex));
                rejected.Add(aWorkIndex);
                rejected.Add(bWorkIndex);
                continue;
            }

            bool repairA = aBest.UsesSourceRepair || bBest.UsesTargetRepair;
            bool repairB = aBest.UsesTargetRepair || bBest.UsesSourceRepair;
            PreparedCurve a = baseA.Clone(repairA);
            PreparedCurve b = baseB.Clone(repairB);
            if ((repairA && HasSelfIntersection(a.Points, false, geometryTolerance, out Point3d crossingA, out Line crossingAFirst, out Line crossingASecond)) ||
                (repairB && HasSelfIntersection(b.Points, false, geometryTolerance, out crossingA, out crossingAFirst, out crossingASecond)))
            {
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.SelfIntersectingRail,
                    $"Curves {aLabel} and {bLabel} still cross after bounded cleanup; the authored rails were left unchanged.",
                    baseA.SourceIndex,
                    baseB.SourceIndex,
                    location: crossingA,
                    focusSegments: new[] { crossingAFirst, crossingASecond }));
                rejected.Add(aWorkIndex);
                rejected.Add(bWorkIndex);
                continue;
            }

            CandidateStats finalA = ScoreCandidateGeometry(a, b, maxWallWidth, usesSourceRepair: repairA, usesTargetRepair: repairB);
            CandidateStats finalB = ScoreCandidateGeometry(b, a, maxWallWidth, usesSourceRepair: repairB, usesTargetRepair: repairA);

            if (!finalA.HasOrderedFullCoverage || !finalB.HasOrderedFullCoverage)
            {
                StationMappingCheck mapping = !finalA.HasOrderedFullCoverage ? finalA.StationMapping : finalB.StationMapping;
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.InvalidStationMapping,
                    $"Curves {aLabel} and {bLabel} still do not form one continuous end-to-end wall after bounded cleanup; they were left unchanged.",
                    baseA.SourceIndex,
                    baseB.SourceIndex,
                    location: mapping.Location,
                    focusSegments: new[] { new Line(mapping.Location, mapping.PartnerLocation) }));
                rejected.Add(aWorkIndex);
                rejected.Add(bWorkIndex);
                continue;
            }

            if (!PassesDistanceChecks(finalA, maxWallWidth) || !PassesDistanceChecks(finalB, maxWallWidth))
            {
                double mean = Math.Max(finalA.Mean, finalB.Mean);
                double max = Math.Max(finalA.Max, finalB.Max);
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

            if (repairA && baseA.RepairPoints != null)
                report.Add(CreateRailCleanupReport(baseA.SourceIndex, baseA.Points, baseA.RepairPoints));
            if (repairB && baseB.RepairPoints != null)
                report.Add(CreateRailCleanupReport(baseB.SourceIndex, baseB.Points, baseB.RepairPoints));

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
        var reportedInvalidMappings = new HashSet<(int A, int B)>();
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
                    $"Curve {FormatCurveRef(curve)}: no pair selected because another candidate is within the ambiguity margin " +
                    $"(max({AmbiguityCostRatio:0.###}x best cost, max wall width)) of candidate {FormatCurveRef(other)}.",
                    curve.SourceIndex,
                    other.SourceIndex));
                continue;
            }

            if (invalidMappingFor.TryGetValue(curve.WorkIndex, out CandidateStats invalidMapping))
            {
                PreparedCurve other = byWorkIndex[invalidMapping.Index];
                var mappingKey = (Math.Min(curve.WorkIndex, other.WorkIndex), Math.Max(curve.WorkIndex, other.WorkIndex));
                if (!reportedInvalidMappings.Add(mappingKey))
                    continue;

                StationMappingCheck mapping = invalidMapping.StationMapping;
                string message = mapping.Failure == StationMappingFailure.EndsDoNotMatch
                    ? $"Curves {FormatCurveRef(curve)} and {FormatCurveRef(other)} do not overlap from end to end near the marker. Trim, extend, or split the rails so both start and finish together."
                    : $"Curve {FormatCurveRef(curve)} doubles back relative to curve {FormatCurveRef(other)} near the marker. Split or redraw the rail so both curves run once in the same direction.";
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.InvalidStationMapping,
                    message,
                    curve.SourceIndex,
                    other.SourceIndex,
                    location: mapping.Location,
                    focusSegments: new[] { new Line(mapping.Location, mapping.PartnerLocation) }));
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
                Point3d location = PointAt(curve.Points, curve.CumLen, curve.Length, curve.IsClosed, curve.Length * 0.5);
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.NoPair,
                    $"Curve {curve.SourceIndex} has no matching wall rail. Include its partner curve, move it within Max Wall Width, or remove this curve from Wall Curves if it is not part of a wall.",
                    curveA: curve.SourceIndex,
                    location: location));
            }
        }

        return pairs;
    }

    private static List<PlannedWall> BuildWalls(
        List<Pair> pairs,
        double maxWallWidth,
        double tolerance,
        bool buildSolids,
        List<ReportEntry> report)
    {
        var walls = new List<PlannedWall>();
        int pairIndex = 0;
        foreach (Pair pair in pairs)
        {
            pairIndex++;
            if (pair.Failed)
                continue;

            double minWidth = EstimateMinGap(pair, tolerance);

            // The narrow-pair rejection must be wall-width scale: rails closer than a tenth of the
            // max wall width are almost certainly duplicate/offset artefacts of the SAME rail, not a
            // deliberate thin wall. The geometry tolerance (~mm) is far too small a threshold — it
            // would accept any spacing above a tenth of a millimetre.
            double minAllowedWidth = Math.Max(maxWallWidth * 0.1, MinAllowedWidth(tolerance));
            if (minWidth < minAllowedWidth)
            {
                report.Add(new ReportEntry(
                    ReportLevel.Warning,
                    ReportReason.SubToleranceWidth,
                    $"Pair ({pair.A.SourceIndex}, {pair.B.SourceIndex}) skipped: minimum rail spacing {minWidth:G4} is below {minAllowedWidth:G4} (a tenth of the max wall width).",
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

            Brep? brep = null;
            if (buildSolids)
            {
                brep = RetainingWallBrepBuilder.Build(toePts, topPts, tolerance, pair.IsClosed);
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
        CandidateStats original = ScoreCandidateGeometry(a, b, tolerance);
        if (original.HasOrderedFullCoverage || a.IsClosed || b.IsClosed)
            return original;

        CandidateStats best = original;
        foreach ((bool repairA, bool repairB) in new[]
                 {
                     (true, false),
                     (false, true),
                     (true, true)
                 })
        {
            if ((repairA && a.RepairPoints == null) || (repairB && b.RepairPoints == null))
                continue;

            PreparedCurve candidateA = a.Clone(repairA);
            PreparedCurve candidateB = b.Clone(repairB);
            CandidateStats candidate = ScoreCandidateGeometry(candidateA, candidateB, tolerance, repairA, repairB);
            if (!candidate.HasOrderedFullCoverage)
                continue;
            if (!best.HasOrderedFullCoverage || candidate.Cost < best.Cost)
                best = candidate;
        }

        return best;
    }

    private static CandidateStats ScoreCandidateGeometry(
        PreparedCurve a,
        PreparedCurve b,
        double tolerance,
        bool usesSourceRepair = false,
        bool usesTargetRepair = false)
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
        return new CandidateStats(
            b.WorkIndex,
            mean,
            iqr,
            max,
            cost,
            CheckOrderedFullCoverage(a, b),
            usesSourceRepair,
            usesTargetRepair);
    }

    private static StationMappingCheck CheckOrderedFullCoverage(PreparedCurve a, PreparedCurve b)
    {
        if (a.IsClosed || b.IsClosed)
            return new StationMappingCheck(true, StationMappingFailure.None, Point3d.Unset, Point3d.Unset);

        const int sampleCount = 17;
        const double stationSlack = 0.04;
        const double endCoverage = 0.15;
        var mapped = new double[sampleCount];
        var sourcePoints = new Point3d[sampleCount];
        var partnerPoints = new Point3d[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double fraction = (double)i / (sampleCount - 1);
            Point3d point = PointAt(a.Points, a.CumLen, a.Length, false, a.Length * fraction);
            sourcePoints[i] = point;
            mapped[i] = ClosestFractionOnPolyline2D(point, b, out partnerPoints[i]);
        }

        bool forward;
        if (Math.Abs(mapped[^1] - mapped[0]) > stationSlack)
        {
            forward = mapped[^1] > mapped[0];
        }
        else
        {
            int firstDirectionalStep = Enumerable.Range(1, mapped.Length - 1)
                .FirstOrDefault(i => Math.Abs(mapped[i] - mapped[i - 1]) > stationSlack);
            forward = firstDirectionalStep == 0 || mapped[firstDirectionalStep] > mapped[firstDirectionalStep - 1];
        }

        for (int i = 1; i < mapped.Length; i++)
        {
            bool reverses = forward
                ? mapped[i] + stationSlack < mapped[i - 1]
                : mapped[i] - stationSlack > mapped[i - 1];
            if (reverses)
            {
                return new StationMappingCheck(
                    false,
                    StationMappingFailure.ReversesDirection,
                    sourcePoints[i],
                    partnerPoints[i]);
            }
        }

        bool coversStart = forward ? mapped[0] <= endCoverage : mapped[0] >= 1.0 - endCoverage;
        bool coversEnd = forward ? mapped[^1] >= 1.0 - endCoverage : mapped[^1] <= endCoverage;
        if (!coversStart || !coversEnd)
        {
            int failingIndex;
            if (!coversStart && !coversEnd)
            {
                double startError = forward ? mapped[0] : 1.0 - mapped[0];
                double endError = forward ? 1.0 - mapped[^1] : mapped[^1];
                failingIndex = startError >= endError ? 0 : sampleCount - 1;
            }
            else
            {
                failingIndex = coversStart ? sampleCount - 1 : 0;
            }

            return new StationMappingCheck(
                false,
                StationMappingFailure.EndsDoNotMatch,
                sourcePoints[failingIndex],
                partnerPoints[failingIndex]);
        }

        return new StationMappingCheck(true, StationMappingFailure.None, Point3d.Unset, Point3d.Unset);
    }

    private static double ClosestFractionOnPolyline2D(Point3d point, PreparedCurve curve, out Point3d closestPoint)
    {
        double bestDistanceSquared = double.MaxValue;
        double bestAlong = 0.0;
        closestPoint = curve.Points[0];
        for (int i = 1; i < curve.Points.Length; i++)
        {
            Point3d a = curve.Points[i - 1];
            Point3d b = curve.Points[i];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lengthSquared = (dx * dx) + (dy * dy);
            double t = lengthSquared <= 1e-18
                ? 0.0
                : Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared, 0.0, 1.0);
            double px = a.X + (dx * t);
            double py = a.Y + (dy * t);
            double ex = point.X - px;
            double ey = point.Y - py;
            double distanceSquared = (ex * ex) + (ey * ey);
            if (distanceSquared >= bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            bestAlong = curve.CumLen[i - 1] + ((curve.CumLen[i] - curve.CumLen[i - 1]) * t);
            closestPoint = new Point3d(px, py, a.Z + ((b.Z - a.Z) * t));
        }

        return curve.Length <= 1e-12 ? 0.0 : Math.Clamp(bestAlong / curve.Length, 0.0, 1.0);
    }

    /// <summary>
    /// A pairing is ambiguous when the second-best candidate is either within
    /// <see cref="AmbiguityCostRatio"/>x of the best candidate's cost, or costs less than one max
    /// wall width outright (i.e. it is ALSO a plausible wall mate, however much better the best one
    /// scores). The second criterion deliberately rejects e.g. three near-parallel rails: the planner
    /// cannot know which two the user meant. Note this is conservative for terraced sites — stacked
    /// parallel walls whose adjacent rails approach within a wall width are skipped with a report
    /// rather than guessed at.
    /// </summary>
    private static bool IsAmbiguous(CandidateStats best, CandidateStats? second, double maxWallWidth) =>
        second.HasValue && second.Value.Cost < AmbiguityMargin(best, maxWallWidth);

    private static double AmbiguityMargin(CandidateStats best, double maxWallWidth) =>
        Math.Max(best.Cost * AmbiguityCostRatio, maxWallWidth);

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

            CenterlineSample[] centerline = BuildCenterline(pair);
            for (int j = i + 1; j < pairs.Count; j++)
            {
                Pair other = pairs[j];
                if (other.Failed)
                    continue;

                CenterlineSample[] otherCenterline = BuildCenterline(other);
                if (CenterlinesCross(centerline, pair.IsClosed, otherCenterline, other.IsClosed, tolerance, out WallCrossing crossing))
                {
                    ReportLevel level = crossing.VerticalRangesOverlap ? ReportLevel.Warning : ReportLevel.Info;
                    string message = crossing.VerticalRangesOverlap
                        ? $"Wall pairs ({pair.A.SourceIndex}, {pair.B.SourceIndex}) and ({other.A.SourceIndex}, {other.B.SourceIndex}) overlap in plan and elevation near the marker at {crossing.AngleDegrees:0.#} degrees; both walls remain enabled. Review whether a junction is intended."
                        : $"Wall pairs ({pair.A.SourceIndex}, {pair.B.SourceIndex}) and ({other.A.SourceIndex}, {other.B.SourceIndex}) cross in plan near the marker at {crossing.AngleDegrees:0.#} degrees but are vertically separated; both walls remain enabled.";
                    report.Add(new ReportEntry(
                        level,
                        ReportReason.CrossingWalls,
                        message,
                        pair.A.SourceIndex,
                        pair.B.SourceIndex,
                        location: crossing.Location,
                        focusSegments: new[] { crossing.FirstSegment, crossing.SecondSegment },
                        relatedCurves: new[]
                        {
                            pair.A.SourceIndex,
                            pair.B.SourceIndex,
                            other.A.SourceIndex,
                            other.B.SourceIndex
                        }));
                    break;
                }
            }
        }
    }

    private static CenterlineSample[] BuildCenterline(Pair pair)
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
                return new CenterlineSample(
                    new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5),
                    Math.Min(a.Z, b.Z),
                    Math.Max(a.Z, b.Z));
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

    private static bool CenterlinesCross(
        CenterlineSample[] a,
        bool aClosed,
        CenterlineSample[] b,
        bool bClosed,
        double tolerance,
        out WallCrossing crossing)
    {
        crossing = default;
        int aSegments = aClosed ? a.Length : a.Length - 1;
        int bSegments = bClosed ? b.Length : b.Length - 1;
        for (int i = 0; i < aSegments; i++)
        {
            int iNext = (i + 1) % a.Length;
            Line la = new(a[i].Point, a[iNext].Point);
            for (int j = 0; j < bSegments; j++)
            {
                int jNext = (j + 1) % b.Length;
                Line lb = new(b[j].Point, b[jNext].Point);
                if (!TrySegmentIntersection(la, lb, out double t, out double u, out double candidateAngle))
                    continue;

                const double segmentEpsilon = 1e-8;
                if (t < -segmentEpsilon || t > 1.0 + segmentEpsilon ||
                    u < -segmentEpsilon || u > 1.0 + segmentEpsilon)
                {
                    continue;
                }

                if (candidateAngle <= 10.0)
                    continue;

                bool aEndpoint = t <= 1e-5 || t >= 1.0 - 1e-5;
                bool bEndpoint = u <= 1e-5 || u >= 1.0 - 1e-5;
                if (aEndpoint && bEndpoint)
                    continue;

                Point3d pointA = la.PointAt(Math.Clamp(t, 0.0, 1.0));
                double minA = Lerp(a[i].MinZ, a[iNext].MinZ, t);
                double maxA = Lerp(a[i].MaxZ, a[iNext].MaxZ, t);
                double minB = Lerp(b[j].MinZ, b[jNext].MinZ, u);
                double maxB = Lerp(b[j].MaxZ, b[jNext].MaxZ, u);
                bool overlapsVertically = Math.Max(minA, minB) <= Math.Min(maxA, maxB) + tolerance;
                crossing = new WallCrossing(pointA, la, lb, candidateAngle, overlapsVertically);
                return true;
            }
        }

        return false;
    }

    private static void ResolveCorners(List<Pair> pairs, double maxWallWidth, double tolerance, List<ReportEntry> report)
    {
        // Corner candidates are wall ends whose end midpoints sit within ONE WALL WIDTH of each
        // other. Two walls meeting at a right-angle corner have end midpoints ~0.7x the wall width
        // apart by construction (each midpoint sits half a width inside its own rail pair), so a
        // geometry-tolerance gate would only ever fire on rails the user had already mitered —
        // exactly when resolution is unnecessary. Spurious near-misses that are not real corners are
        // still rejected by the miter-budget and width checks in TryResolveRailCorner.
        double cornerSearchRadius = Math.Max(maxWallWidth, tolerance);
        var endpointCandidateCounts = new Dictionary<(int PairIndex, bool Start), int>();
        for (int i = 0; i < pairs.Count; i++)
        {
            if (pairs[i].Failed || pairs[i].IsClosed)
                continue;

            foreach (bool start in new[] { true, false })
            {
                int count = 0;
                for (int j = 0; j < pairs.Count; j++)
                {
                    if (i == j || pairs[j].Failed || pairs[j].IsClosed)
                        continue;
                    foreach (bool otherStart in new[] { true, false })
                    {
                        if (Distance2D(pairs[i].EndMid(start), pairs[j].EndMid(otherStart)) <= cornerSearchRadius)
                            count++;
                    }
                }

                endpointCandidateCounts[(i, start)] = count;
            }
        }

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
                    if (endpointCandidateCounts.GetValueOrDefault((i, pairStart)) != 1)
                        continue;
                    foreach (bool otherStart in new[] { true, false })
                    {
                        if (endpointCandidateCounts.GetValueOrDefault((j, otherStart)) != 1)
                            continue;
                        if (Distance2D(pair.EndMid(pairStart), other.EndMid(otherStart)) > cornerSearchRadius)
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

        ResolveCornerElevations(pairA.Point.Z, match0.Point.Z, tolerance, out double pairZ0, out double otherZ0);
        ResolveCornerElevations(pairB.Point.Z, match1.Point.Z, tolerance, out double pairZ1, out double otherZ1);
        Point3d pairResolved0 = new(corner0.X, corner0.Y, pairZ0);
        Point3d pairResolved1 = new(corner1.X, corner1.Y, pairZ1);
        Point3d otherResolved0 = new(corner0.X, corner0.Y, otherZ0);
        Point3d otherResolved1 = new(corner1.X, corner1.Y, otherZ1);

        pair.A.SetEnd(pairStart, pairResolved0);
        pair.B.SetEnd(pairStart, pairResolved1);
        if (directMatch)
        {
            other.A.SetEnd(otherStart, otherResolved0);
            other.B.SetEnd(otherStart, otherResolved1);
        }
        else
        {
            other.B.SetEnd(otherStart, otherResolved0);
            other.A.SetEnd(otherStart, otherResolved1);
        }

        return true;
    }

    private static void ResolveCornerElevations(double first, double second, double tolerance, out double resolvedFirst, out double resolvedSecond)
    {
        if (Math.Abs(first - second) <= tolerance)
        {
            resolvedFirst = resolvedSecond = (first + second) * 0.5;
            return;
        }

        // XY miters are allowed to meet, but authored rail elevations remain authoritative unless
        // they were already coincident within model tolerance.
        resolvedFirst = first;
        resolvedSecond = second;
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

    private static Point2d Unit(Point2d vector)
    {
        double length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
        return length <= 1e-12 ? new Point2d(1.0, 0.0) : new Point2d(vector.X / length, vector.Y / length);
    }

    private static Point3d[] CleanupPolyline(Point3d[] polyline, double tolerance)
    {
        double dedupTol = DuplicateTolerance(tolerance);
        var points = new List<Point3d>(polyline.Length);
        for (int i = 0; i < polyline.Length; i++)
        {
            if (points.Count == 0 || points[^1].DistanceTo(polyline[i]) > dedupTol)
                points.Add(polyline[i]);
        }

        if (points.Count > 1 && points[0].DistanceTo(points[^1]) <= dedupTol)
            points.RemoveAt(points.Count - 1);

        return points.ToArray();
    }

    private static Point3d[]? CreateRepairCandidate(Point3d[] points, double tolerance)
    {
        if (points.Length <= 2 || tolerance <= 0.0)
            return null;

        Point3d[] simplified = SimplifyOpenPolyline(points, tolerance, preserveZExtrema: true);
        if (simplified.Length >= points.Length)
            return null;

        // RDP deviation alone cannot see a collinear reversal: 0 -> 10 -> 9 -> 20 lies exactly on
        // the replacement chord. Bound the removed detour length as well so a cleanup candidate can
        // erase only a genuinely tiny spur, never a long retrace whose points happen to be collinear.
        double removedDetour = Math.Max(0.0, OpenPolylineLength3D(points) - OpenPolylineLength3D(simplified));
        return removedDetour <= (2.0 * tolerance) + 1e-9 ? simplified : null;
    }

    private static double OpenPolylineLength3D(Point3d[] points)
    {
        double length = 0.0;
        for (int i = 1; i < points.Length; i++)
            length += points[i - 1].DistanceTo(points[i]);
        return length;
    }

    private static ReportEntry CreateRailCleanupReport(
        int sourceIndex,
        Point3d[] original,
        Point3d[] repaired,
        Point3d? preferredLocation = null)
    {
        double maxDeviationSquared = 0.0;
        Point3d location = preferredLocation is Point3d preferred && preferred.IsValid
            ? preferred
            : original[original.Length / 2];
        foreach (Point3d point in original)
        {
            double distanceSquared = DistancePointPolylineSquared3D(point, repaired);
            if (distanceSquared <= maxDeviationSquared)
                continue;

            maxDeviationSquared = distanceSquared;
            if (!preferredLocation.HasValue)
                location = point;
        }

        int removed = original.Length - repaired.Length;
        double removedDetour = Math.Max(0.0, OpenPolylineLength3D(original) - OpenPolylineLength3D(repaired));
        return new ReportEntry(
            ReportLevel.Info,
            ReportReason.RailDetailSimplified,
            $"Curve {sourceIndex}: simplified {removed} tiny rail point{(removed == 1 ? string.Empty : "s")} that prevented a clean wall path; maximum offset {Math.Sqrt(maxDeviationSquared):G4}, removed detour {removedDetour:G4}.",
            curveA: sourceIndex,
            location: location);
    }

    private static double DistancePointPolylineSquared3D(Point3d point, Point3d[] polyline)
    {
        double best = double.MaxValue;
        for (int i = 1; i < polyline.Length; i++)
            best = Math.Min(best, DistancePointSegmentSquared3D(point, polyline[i - 1], polyline[i]));
        return best;
    }

    private static Point3d[] SimplifyOpenPolyline(Point3d[] points, double tolerance, bool preserveZExtrema = false)
    {
        if (points.Length <= 2)
            return points;

        double toleranceSquared = tolerance * tolerance;
        var keep = new bool[points.Length];
        keep[0] = true;
        keep[^1] = true;
        var mandatory = new SortedSet<int> { 0, points.Length - 1 };
        if (preserveZExtrema)
        {
            int minZ = 0;
            int maxZ = 0;
            for (int i = 1; i < points.Length; i++)
            {
                if (points[i].Z < points[minZ].Z)
                    minZ = i;
                if (points[i].Z > points[maxZ].Z)
                    maxZ = i;
            }
            mandatory.Add(minZ);
            mandatory.Add(maxZ);
        }

        var ranges = new Stack<(int Start, int End)>();
        int[] mandatoryIndices = mandatory.ToArray();
        foreach (int index in mandatoryIndices)
            keep[index] = true;
        for (int i = 1; i < mandatoryIndices.Length; i++)
            ranges.Push((mandatoryIndices[i - 1], mandatoryIndices[i]));
        while (ranges.Count > 0)
        {
            (int start, int end) = ranges.Pop();
            int furthest = -1;
            double maxDistanceSquared = toleranceSquared;
            for (int i = start + 1; i < end; i++)
            {
                double distanceSquared = DistancePointSegmentSquared3D(points[i], points[start], points[end]);
                if (distanceSquared <= maxDistanceSquared)
                    continue;

                maxDistanceSquared = distanceSquared;
                furthest = i;
            }

            if (furthest < 0)
                continue;

            keep[furthest] = true;
            ranges.Push((start, furthest));
            ranges.Push((furthest, end));
        }

        var simplified = new List<Point3d>();
        for (int i = 0; i < points.Length; i++)
        {
            if (keep[i])
                simplified.Add(points[i]);
        }
        return simplified.ToArray();
    }

    private static double DistancePointSegmentSquared3D(Point3d point, Point3d a, Point3d b)
    {
        Vector3d segment = b - a;
        double lengthSquared = segment.SquareLength;
        double t = lengthSquared <= 1e-18
            ? 0.0
            : Math.Clamp(((point - a) * segment) / lengthSquared, 0.0, 1.0);
        Point3d closest = a + (segment * t);
        return point.DistanceToSquared(closest);
    }

    private static bool IsNearlyClosed(Point3d[] polyline, double tolerance) =>
        polyline.Length >= 3 && polyline[0].DistanceTo(polyline[^1]) <= DuplicateTolerance(tolerance);

    private static bool HasSelfIntersection(
        Point3d[] points,
        bool isClosed,
        double tolerance,
        out Point3d intersection,
        out Line firstSegment,
        out Line secondSegment)
    {
        intersection = Point3d.Unset;
        firstSegment = Line.Unset;
        secondSegment = Line.Unset;
        int segmentCount = isClosed ? points.Length : points.Length - 1;
        if (segmentCount < 3)
            return false;

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        for (int i = 0; i < points.Length; i++)
        {
            minX = Math.Min(minX, points[i].X);
            minY = Math.Min(minY, points[i].Y);
            maxX = Math.Max(maxX, points[i].X);
            maxY = Math.Max(maxY, points[i].Y);
        }

        // The former all-pairs scan was O(n^2) and dominated planning for survey rails with tens of
        // thousands of points. Bucket segment bounds into roughly sqrt(n) cells along the longest
        // axis, then run the exact same intersection predicate only for spatial neighbours.
        double span = Math.Max(maxX - minX, maxY - minY);
        double cellSize = Math.Max(
            span / Math.Max(8.0, Math.Sqrt(segmentCount)),
            Math.Max(tolerance * 4.0, 1e-9));
        var cells = new Dictionary<long, List<int>>();
        var seenAtStamp = new int[segmentCount];
        int stamp = 0;

        for (int i = 0; i < segmentCount; i++)
        {
            int iNext = (i + 1) % points.Length;
            GetSegmentCellRange(points[i], points[iNext], minX, minY, cellSize, tolerance,
                out int ix0, out int iy0, out int ix1, out int iy1);

            stamp++;
            for (int ix = ix0; ix <= ix1; ix++)
            {
                for (int iy = iy0; iy <= iy1; iy++)
                {
                    long key = CellKey(ix, iy);
                    if (!cells.TryGetValue(key, out List<int>? candidates))
                        continue;

                    foreach (int j in candidates)
                    {
                        if (seenAtStamp[j] == stamp)
                            continue;
                        seenAtStamp[j] = stamp;

                        int jNext = (j + 1) % points.Length;
                        bool adjacent = iNext == j || jNext == i;
                        if (isClosed && i == segmentCount - 1 && j == 0)
                            adjacent = true;
                        if (adjacent)
                            continue;

                        if (TrySegmentsIntersect2D(points[i], points[iNext], points[j], points[jNext], tolerance, out intersection))
                        {
                            firstSegment = new Line(points[i], points[iNext]);
                            secondSegment = new Line(points[j], points[jNext]);
                            return true;
                        }
                    }
                }
            }

            for (int ix = ix0; ix <= ix1; ix++)
            {
                for (int iy = iy0; iy <= iy1; iy++)
                {
                    long key = CellKey(ix, iy);
                    if (!cells.TryGetValue(key, out List<int>? bucket))
                    {
                        bucket = new List<int>(4);
                        cells.Add(key, bucket);
                    }
                    bucket.Add(i);
                }
            }
        }

        return false;
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * Math.Clamp(t, 0.0, 1.0));

    private static void GetSegmentCellRange(
        Point3d a,
        Point3d b,
        double originX,
        double originY,
        double cellSize,
        double tolerance,
        out int ix0,
        out int iy0,
        out int ix1,
        out int iy1)
    {
        double padding = Math.Max(tolerance, 1e-9);
        ix0 = (int)Math.Floor((Math.Min(a.X, b.X) - padding - originX) / cellSize);
        iy0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - padding - originY) / cellSize);
        ix1 = (int)Math.Floor((Math.Max(a.X, b.X) + padding - originX) / cellSize);
        iy1 = (int)Math.Floor((Math.Max(a.Y, b.Y) + padding - originY) / cellSize);
    }

    private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;

    private static bool TrySegmentsIntersect2D(
        Point3d a0,
        Point3d a1,
        Point3d b0,
        Point3d b1,
        double tolerance,
        out Point3d intersection)
    {
        intersection = Point3d.Unset;
        Point2d r = new(a1.X - a0.X, a1.Y - a0.Y);
        Point2d s = new(b1.X - b0.X, b1.Y - b0.Y);
        if (!Solve2x2(r.X, -s.X, r.Y, -s.Y, b0.X - a0.X, b0.Y - a0.Y, out double t, out double u))
            return false;

        double eps = Math.Max(tolerance * 1e-6, 1e-9);
        if (t <= eps || t >= 1.0 - eps || u <= eps || u >= 1.0 - eps)
            return false;

        Point3d onA = a0 + ((a1 - a0) * t);
        intersection = onA;
        return true;
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
