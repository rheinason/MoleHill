// Recovers a small set of editable curves and spot points that rebuild a terrain within a tolerance.
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Routing;

namespace MoleHill.Core.Processing;

/// <summary>
/// Turns a dense terrain (a DEM or point-cloud mesh) back into the kind of input a person would draw: a
/// border, spot points on peaks and pits, and contours — as few as rebuild it within a stated vertical
/// tolerance. Simplify (B6) reduces the mesh; this inverts it, so the output is input that stays editable.
///
/// <para>The loop is greedy. Rebuild a TIN from what has been chosen so far, find the terrain vertex it
/// misses by most, and add the contour through that vertex (simplified in plan within
/// <see cref="Options.PlanTolerance"/>). A worst vertex the contour does not catch — a peak's contour
/// shrinks to nothing — becomes a spot point instead. Stop once every terrain vertex is within
/// <see cref="Options.VerticalTolerance"/>, or the curve budget is spent.</para>
///
/// <para>Before any contour, landform lines are offered: valley lines where downhill flow gathers and ridge
/// lines where uphill flow does (the same accumulation run on the inverted terrain). A crease costs many
/// contours, each kinking across it, but one line along it; each candidate is kept only if it measurably
/// reduces the miss, so a smooth valley that contours already describe adds nothing.</para>
///
/// <para>The deviation is measured at the terrain's vertices. Both surfaces are linear between their own
/// vertices, so a miss between them is possible where the rebuild's edges cross the terrain's; the result
/// says it is vertex-sampled. On a large terrain several contours are added per round, at worst-missed
/// vertices far enough apart not to share one contour.</para>
/// </summary>
public static partial class TerrainCurveRecovery
{
    public sealed class Options
    {
        /// <summary>The largest vertical miss allowed at any terrain vertex.</summary>
        public required double VerticalTolerance { get; init; }

        /// <summary>How far a simplified contour or border may move in plan. Defaults to the vertical tolerance.</summary>
        public double PlanTolerance { get; init; }

        /// <summary>Stop after adding this many contours, met tolerance or not.</summary>
        public int MaximumCurves { get; init; } = 200;

        public TerrainRecoveryPolicy? Policy { get; init; }

        /// <summary>A peak or pit must be the highest (lowest) vertex this many rings out to seed a spot point.</summary>
        public int ExtremumRings { get; init; } = 3;

        /// <summary>Offer ridge and valley lines before contours.</summary>
        public bool LandformLines { get; init; } = true;

        /// <summary>Contours added per rebuild; 0 picks by size (one below 5,000 vertices, then one more per 5,000, up to eight).</summary>
        public int ContoursPerRound { get; init; }

        /// <summary>Measured breaklines held in every reconstruction, including landform-line trials.</summary>
        public IReadOnlyList<double[]> FixedBreaklines { get; init; } = Array.Empty<double[]>();

        /// <summary>Null measures the full terrain; otherwise contours, spots and deviation stay in this region.</summary>
        public ITerrainRecoveryRegion? Region { get; init; }

        /// <summary>
        /// Ground measured at <see cref="SecondaryTolerance"/> where only spot heights may be added: scan paving,
        /// which its draped outline breaklines already describe. Interval recovery only.
        /// </summary>
        public ITerrainRecoveryRegion? SecondaryRegion { get; init; }

        public double SecondaryTolerance { get; init; }

        /// <summary>Fit light curves before reconstruction so the measured result describes their sampled geometry.</summary>
        public bool FitContours { get; init; }

        public Func<bool>? ShouldCancel { get; init; }

        /// <summary>Called after each round with (round, worst deviation).</summary>
        public Action<int, double>? ReportProgress { get; init; }
    }

    public enum Termination
    {
        ToleranceSatisfied,
        CurveBudgetSpent,
        NoProgress,
        TriangulationFailed,
        Cancelled,
        UnsupportedInput
    }

    public sealed class RecoveredContour
    {
        public required double Level { get; init; }

        /// <summary>Flat XYZ points, every Z the level. A closed contour repeats no point; see <see cref="IsClosed"/>.</summary>
        public required double[] PointsXyz { get; init; }

        public required bool IsClosed { get; init; }
        public FittedRouteCurve? Curve { get; init; }
    }

    public sealed class Result
    {
        /// <summary>The terrain's border loops, simplified, as flat XYZ (closed, first point not repeated).</summary>
        public required IReadOnlyList<double[]> Borders { get; init; }

        public required IReadOnlyList<RecoveredContour> Contours { get; init; }

        /// <summary>Ridge and valley lines kept, as open flat XYZ polylines on the terrain.</summary>
        public required IReadOnlyList<double[]> LandformLines { get; init; }

        /// <summary>Spot points as flat XYZ.</summary>
        public required double[] SpotPoints { get; init; }

        /// <summary>The largest vertical miss at a terrain vertex by the final rebuild.</summary>
        public required double MaximumDeviation { get; init; }

        public required int Rounds { get; init; }

        public required Termination Termination { get; init; }

        public required string Diagnostic { get; init; }
        public int EvaluatedVertexCount { get; init; }

        public int SpotPointCount => SpotPoints.Length / 3;
        public double ExcessErrorVolume { get; init; }
        public double? OriginalMaximumDeviation { get; set; }
        public double AreaOutsideTolerance { get; init; }
        public int PrunedInputCount { get; init; }
        public int RejectedCandidateCount { get; init; }

        /// <summary>The worst miss in <see cref="Options.SecondaryRegion"/>, when one was measured.</summary>
        public double? SecondaryMaximumDeviation { get; init; }
    }

    public static Result Recover(IndexedTriMesh terrain, Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Recover(terrain, options, options.LandformLines);
    }

    private static Result Recover(IndexedTriMesh terrain, Options options, bool useLandforms)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateBreaklines(options.FixedBreaklines);
        if (options.Policy is { ContourInterval: > 0 }) return RecoverInterval(terrain, options);
        if (options.Policy != null) return RecoverInformative(terrain, options, useLandforms);
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = terrain;
        double verticalTolerance = options.VerticalTolerance;
        if (!double.IsFinite(verticalTolerance) || verticalTolerance <= 0.0 || vertexCount < 3 || faceCount < 1)
            return Unsupported("A positive vertical tolerance and a terrain of at least one face are required.");

        double planTolerance = options.PlanTolerance > 0.0 ? options.PlanTolerance : verticalTolerance;
        if (!MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(faces, faceCount, out List<int[]> loops) || loops.Count == 0)
            return Unsupported("The terrain's border is not a set of simple loops.");

        // Borders first: they bound the rebuild, so a contour can only refine inside them.
        var borders = new List<double[]>(loops.Count);
        foreach (int[] loop in loops)
        {
            double[] loopXyz = new double[loop.Length * 3];
            for (int i = 0; i < loop.Length; i++)
                Array.Copy(vertices, loop[i] * 3, loopXyz, i * 3, 3);
            double[] simplified = SimplifyClosed(loopXyz, planTolerance, verticalTolerance);
            if (simplified.Length >= 9)
                borders.Add(simplified);
        }

        if (borders.Count == 0)
            return Unsupported("The terrain's border collapsed when simplified.");

        int evaluated = vertexCount;
        if (options.Region != null)
        {
            evaluated = 0;
            for (int v = 0; v < vertexCount; v++)
                if (options.Region.Contains(vertices[v * 3], vertices[v * 3 + 1]))
                    evaluated++;
            if (evaluated == 0)
                return new Result
                {
                    Borders = borders,
                    Contours = Array.Empty<RecoveredContour>(),
                    LandformLines = Array.Empty<double[]>(),
                    SpotPoints = Array.Empty<double>(),
                    MaximumDeviation = 0,
                    Rounds = 0,
                    Termination = Termination.ToleranceSatisfied,
                    Diagnostic = "No terrain vertices fall in the requested region; no contours or spots were requested.",
                    EvaluatedVertexCount = 0,
                };
        }

        MeshVertexAdjacency adjacency = MeshVertexAdjacency.Build(new List<int>(faces[..(faceCount * 3)]), faceCount, vertexCount);
        var onBorder = new bool[vertexCount];
        foreach (int[] loop in loops)
            foreach (int v in loop)
                onBorder[v] = true;

        var spots = new List<double>();
        var spotted = new HashSet<int>();
        foreach (int extremum in FindExtrema(vertices, vertexCount, adjacency, Math.Max(1, options.ExtremumRings), onBorder))
        {
            if (options.Region != null && !options.Region.Contains(vertices[extremum * 3], vertices[extremum * 3 + 1]))
                continue;
            spotted.Add(extremum);
            spots.Add(vertices[extremum * 3]);
            spots.Add(vertices[extremum * 3 + 1]);
            spots.Add(vertices[extremum * 3 + 2]);
        }

        var contours = new List<RecoveredContour>();
        List<double[]> landformLines = useLandforms
            ? SelectLandformLines(vertices, vertexCount, adjacency, onBorder, borders, spots, verticalTolerance, planTolerance, options.FixedBreaklines, options.Region)
            : new List<double[]>();

        // A contour simplified across steep ground drifts off its height there, and two can cross; judging
        // each dropped point by the terrain under the chord keeps the contour within the vertical tolerance.
        var referenceProjector = new MeshHeightProjector(vertices, vertexCount, faces, faceCount);
        double? TerrainAt(double x, double y) =>
            referenceProjector.TryGetHeightExtremes(x, y, verticalTolerance * 1e-3, out _, out double high) ? high : null;

        int perRound = options.ContoursPerRound > 0
            ? options.ContoursPerRound
            : Math.Clamp(vertexCount / 5000 + 1, 1, 8);
        double pickRadius = PlanDiagonal(vertices, vertexCount) * 0.1;
        var attempted = new HashSet<int>();
        double worstDeviation = double.PositiveInfinity;
        int round = 0;
        Termination termination;
        string diagnostic;
        while (true)
        {
            if (options.ShouldCancel?.Invoke() == true)
            {
                termination = Termination.Cancelled;
                diagnostic = "Cancelled.";
                break;
            }

            TinResult? rebuild = Rebuild(borders, landformLines, contours, spots, planTolerance, out string? error, options.FixedBreaklines);
            if (rebuild == null)
            {
                termination = Termination.TriangulationFailed;
                diagnostic = error ?? "The chosen curves could not be triangulated.";
                break;
            }

            double[] deviations = Deviations(vertices, vertexCount, rebuild, verticalTolerance, options.Region);
            worstDeviation = 0.0;
            foreach (double deviation in deviations)
            {
                if (deviation > worstDeviation)
                    worstDeviation = deviation;
            }

            options.ReportProgress?.Invoke(round, worstDeviation);
            if (worstDeviation <= verticalTolerance)
            {
                termination = Termination.ToleranceSatisfied;
                diagnostic = options.Region == null
                    ? FormattableString.Invariant($"Within {verticalTolerance:G4} at every terrain vertex.")
                    : FormattableString.Invariant($"Within {verticalTolerance:G4} at every terrain vertex in the requested region.");
                break;
            }

            if (contours.Count >= options.MaximumCurves)
            {
                termination = Termination.CurveBudgetSpent;
                diagnostic = FormattableString.Invariant($"Stopped at {contours.Count} contours with a {worstDeviation:G4} miss left.");
                break;
            }

            round++;
            List<int> picks = PickWorst(deviations, vertices, verticalTolerance, perRound, pickRadius);
            var fresh = picks.Where(v => !attempted.Contains(v)).ToList();
            RecoveredContour?[] traced = TraceContours(terrain, fresh, planTolerance, verticalTolerance, TerrainAt);
            int added = 0;
            for (int i = 0; i < fresh.Count; i++)
            {
                attempted.Add(fresh[i]);
                RecoveredContour? contour = traced[i];
                int before = contours.Count;
                foreach (RecoveredContour clipped in ClipContour(contour, options.Region,
                    vertices[fresh[i] * 3], vertices[fresh[i] * 3 + 1]))
                {
                    RecoveredContour piece = options.FitContours
                        ? FitContour(clipped, planTolerance, verticalTolerance, TerrainAt, options.Region) : clipped;
                    if (contours.Count < options.MaximumCurves && !contours.Any(existing =>
                        Math.Abs(existing.Level - piece.Level) < 1e-12 && SameStart(existing, piece)))
                    {
                        contours.Add(piece);
                        added++;
                    }
                }
                if (before == contours.Count && AddSpot(fresh[i]))
                {
                    added++;
                }
            }

            // A vertex a contour was already traced through and still misses becomes a spot point.
            foreach (int v in picks.Where(v => !fresh.Contains(v)))
            {
                if (AddSpot(v))
                    added++;
            }

            if (added == 0)
            {
                termination = Termination.NoProgress;
                diagnostic = FormattableString.Invariant($"The worst vertices could not be captured; a {worstDeviation:G4} miss is left.");
                break;
            }
        }

        return new Result
        {
            Borders = borders,
            Contours = contours,
            LandformLines = landformLines,
            SpotPoints = spots.ToArray(),
            MaximumDeviation = double.IsFinite(worstDeviation) ? worstDeviation : double.NaN,
            Rounds = round,
            Termination = termination,
            Diagnostic = diagnostic,
            EvaluatedVertexCount = evaluated
        };

        bool AddSpot(int vertex)
        {
            if (!spotted.Add(vertex))
                return false;
            spots.Add(vertices[vertex * 3]);
            spots.Add(vertices[vertex * 3 + 1]);
            spots.Add(vertices[vertex * 3 + 2]);
            return true;
        }
    }

    /// <summary>
    /// Up to <paramref name="count"/> vertices missing by more than the tolerance, worst first, each further than
    /// <paramref name="radius"/> in plan from those already picked so they do not all land on one contour.
    /// </summary>
    private static List<int> PickWorst(double[] deviations, double[] vertices, double tolerance, int count, double radius)
    {
        var order = new List<int>();
        for (int v = 0; v < deviations.Length; v++)
        {
            if (deviations[v] > tolerance)
                order.Add(v);
        }

        order.Sort((a, b) => deviations[b].CompareTo(deviations[a]) != 0 ? deviations[b].CompareTo(deviations[a]) : a.CompareTo(b));
        var picks = new List<int>(count);
        double radiusSquared = radius * radius;
        foreach (int v in order)
        {
            bool clear = picks.All(p =>
                Sq(vertices[p * 3] - vertices[v * 3]) + Sq(vertices[p * 3 + 1] - vertices[v * 3 + 1]) > radiusSquared);
            if (!clear)
                continue;
            picks.Add(v);
            if (picks.Count >= count)
                break;
        }

        return picks;
    }

    private static double PlanDiagonal(double[] vertices, int vertexCount)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int v = 0; v < vertexCount; v++)
        {
            minX = Math.Min(minX, vertices[v * 3]);
            maxX = Math.Max(maxX, vertices[v * 3]);
            minY = Math.Min(minY, vertices[v * 3 + 1]);
            maxY = Math.Max(maxY, vertices[v * 3 + 1]);
        }

        return Math.Sqrt(Sq(maxX - minX) + Sq(maxY - minY));
    }

    /// <summary>
    /// Offers each ridge and valley line, longest first, and keeps it only when it earns its length: the rebuild
    /// with it must miss the terrain by less in total (summed excess over the tolerance) by at least one
    /// tolerance (<see cref="LandformEarnFactor"/> of them) for each terrain vertex the line passes. A relative test (a share of the total) never passes
    /// on a large rough terrain, where the border-only total is huge; this one asks the same of every line.
    /// A line along a crease pays for itself many times; one along a smooth swale the contours will describe
    /// anyway does not.
    /// </summary>
    private static List<double[]> SelectLandformLines(
        double[] vertices,
        int vertexCount,
        MeshVertexAdjacency adjacency,
        bool[] onBorder,
        List<double[]> borders,
        List<double> spots,
        double verticalTolerance,
        double planTolerance,
        IReadOnlyList<double[]> fixedBreaklines,
        ITerrainRecoveryRegion? region)
    {
        var accepted = new List<double[]>();
        List<double[]> candidates = ExtractLandformLines(vertices, vertexCount, adjacency, onBorder)
            .Select(line => SimplifyOpen(line, planTolerance, verticalTolerance))
            .SelectMany(line => region == null ? new[] { line } : region.Clip(line, false).Select(piece => piece.Points))
            .Where(line => line.Length >= 6 && PlanLength(line) > planTolerance * 10.0)
            .OrderByDescending(PlanLength)
            .Take(MaximumLandformCandidates)
            .ToList();
        if (candidates.Count == 0)
            return accepted;

        var noContours = new List<RecoveredContour>();
        TinResult? baseline = Rebuild(borders, accepted, noContours, spots, planTolerance, out _, fixedBreaklines);
        if (baseline == null)
            return accepted;
        double excess = Excess(Deviations(vertices, vertexCount, baseline, verticalTolerance, region), verticalTolerance);
        double cell = Math.Max(MeanSpacing(vertices, vertexCount), 1e-12);
        foreach (double[] candidate in candidates)
        {
            if (excess <= 0.0)
                break;
            accepted.Add(candidate);
            TinResult? trial = Rebuild(borders, accepted, noContours, spots, planTolerance, out _, fixedBreaklines);
            double trialExcess = trial == null
                ? double.PositiveInfinity
                : Excess(Deviations(vertices, vertexCount, trial, verticalTolerance, region), verticalTolerance);
            double required = LandformEarnFactor * verticalTolerance * PlanLength(candidate) / cell;
            if (excess - trialExcess >= required)
                excess = trialExcess;
            else
                accepted.RemoveAt(accepted.Count - 1);
        }

        return accepted;
    }

    private const int MaximumLandformCandidates = 40;

    /// <summary>
    /// How many tolerances of summed miss a line must remove per terrain vertex along it. Measured on a
    /// fractal 151 x 151 terrain: at 1 lines that saved nothing were kept; at 6 lines that saved contours on a
    /// creased one began to be refused. Rough natural ground rarely pays for lines either way, which is why
    /// mhTerrainToCurves can run both and keep the smaller result.
    /// </summary>
    private const double LandformEarnFactor = 3.0;

    /// <summary>Typical plan spacing of the terrain's vertices: the side of the square each would own.</summary>
    private static double MeanSpacing(double[] vertices, int vertexCount)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int v = 0; v < vertexCount; v++)
        {
            minX = Math.Min(minX, vertices[v * 3]);
            maxX = Math.Max(maxX, vertices[v * 3]);
            minY = Math.Min(minY, vertices[v * 3 + 1]);
            maxY = Math.Max(maxY, vertices[v * 3 + 1]);
        }

        return Math.Sqrt((maxX - minX) * (maxY - minY) / Math.Max(vertexCount, 1));
    }

    private static double Excess(double[] deviations, double tolerance)
    {
        double sum = 0.0;
        foreach (double deviation in deviations)
        {
            if (deviation > tolerance)
                sum += deviation - tolerance;
        }

        return sum;
    }

    /// <summary>
    /// Priority-flood depression filling of <paramref name="sign"/> x elevation, seeded from the border: each
    /// vertex ends at least a hair above the neighbour it was reached from, so every interior vertex has a
    /// strictly lower neighbour on the way out.
    /// </summary>
    private static void FillDepressions(double[] vertices, int vertexCount, MeshVertexAdjacency adjacency, bool[] onBorder, int sign, double[] filled)
    {
        var done = new bool[vertexCount];
        var queue = new PriorityQueue<int, double>();
        double scale = 0.0;
        for (int v = 0; v < vertexCount; v++)
            scale = Math.Max(scale, Math.Abs(vertices[v * 3 + 2]));
        double epsilon = Math.Max(scale, 1.0) * 1e-9;
        for (int v = 0; v < vertexCount; v++)
        {
            filled[v] = sign * vertices[v * 3 + 2];
            if (onBorder[v])
            {
                done[v] = true;
                queue.Enqueue(v, filled[v]);
            }
        }

        while (queue.TryDequeue(out int current, out _))
        {
            foreach (int n in adjacency.NeighborsOf(current))
            {
                if (done[n])
                    continue;
                done[n] = true;
                filled[n] = Math.Max(filled[n], filled[current] + epsilon);
                queue.Enqueue(n, filled[n]);
            }
        }
    }

    private static double PlanLength(double[] line)
    {
        double length = 0.0;
        for (int i = 3; i < line.Length; i += 3)
            length += Math.Sqrt(Sq(line[i] - line[i - 3]) + Sq(line[i + 1] - line[i - 2]));
        return length;
    }

    /// <summary>
    /// Valley lines (where steepest-descent flow from enough vertices gathers) and ridge lines (the same on the
    /// inverted terrain), as chains of terrain vertices in flat XYZ. Flow over a plain slope never gathers:
    /// each vertex passes on only the column above it, a few times the square root of the vertex count at most,
    /// so the threshold sits above that and only converging flow becomes a line. A line runs from its head down
    /// to an outlet, the border, or the line it joins, so the network comes out as branches that share ends.
    /// </summary>
    internal static List<double[]> ExtractLandformLines(
        double[] vertices,
        int vertexCount,
        MeshVertexAdjacency adjacency,
        bool[] onBorder)
    {
        var lines = new List<double[]>();
        double threshold = Math.Max(4.0 * Math.Sqrt(vertexCount), 16.0);
        var receiver = new int[vertexCount];
        var accumulation = new double[vertexCount];
        var order = Enumerable.Range(0, vertexCount).ToArray();
        var filled = new double[vertexCount];
        foreach (int sign in new[] { 1, -1 })
        {
            // Priority-flood from the border: a sampled crease is not monotone along its length, and plain
            // steepest descent stops in every false pit that leaves, breaking one line into short pieces.
            // Filling raises each pit to just above its spill height, so flow always reaches the border.
            FillDepressions(vertices, vertexCount, adjacency, onBorder, sign, filled);
            for (int v = 0; v < vertexCount; v++)
            {
                receiver[v] = -1;
                accumulation[v] = 1.0;
                if (onBorder[v])
                    continue;
                double z = filled[v];
                double steepest = 0.0;
                foreach (int n in adjacency.NeighborsOf(v))
                {
                    double run = Math.Sqrt(Sq(vertices[n * 3] - vertices[v * 3]) + Sq(vertices[n * 3 + 1] - vertices[v * 3 + 1]));
                    if (run <= 0.0)
                        continue;
                    double fall = (z - filled[n]) / run;
                    if (fall > steepest)
                    {
                        steepest = fall;
                        receiver[v] = n;
                    }
                }
            }

            Array.Sort(order, (a, b) => filled[b].CompareTo(filled[a]));
            foreach (int v in order)
            {
                if (receiver[v] >= 0)
                    accumulation[receiver[v]] += accumulation[v];
            }

            var upstream = new int[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                if (accumulation[v] >= threshold && receiver[v] >= 0 && accumulation[receiver[v]] >= threshold)
                    upstream[receiver[v]]++;
            }

            var visited = new bool[vertexCount];
            foreach (int head in order)
            {
                if (accumulation[head] < threshold || upstream[head] != 0 || visited[head])
                    continue;
                var chain = new List<int> { head };
                visited[head] = true;
                int current = head;
                while (receiver[current] >= 0)
                {
                    int next = receiver[current];
                    if (accumulation[next] < threshold && !onBorder[next])
                        break;
                    chain.Add(next);
                    if (visited[next] || onBorder[next])
                        break;
                    visited[next] = true;
                    current = next;
                }

                if (chain.Count < 2)
                    continue;
                var xyz = new double[chain.Count * 3];
                for (int i = 0; i < chain.Count; i++)
                    Array.Copy(vertices, chain[i] * 3, xyz, i * 3, 3);
                lines.Add(xyz);
            }
        }

        return lines;
    }

    private static bool SameStart(RecoveredContour a, RecoveredContour b) =>
        Math.Abs(a.PointsXyz[0] - b.PointsXyz[0]) < 1e-9 && Math.Abs(a.PointsXyz[1] - b.PointsXyz[1]) < 1e-9;

    /// <summary>
    /// For each vertex, the contour at its height passing nearest it, simplified in plan; every level is traced
    /// in one pass over the terrain. Null where it is too small to keep (a peak's or pit's contour shrinks to a
    /// point), so the vertex becomes a spot.
    /// </summary>
    private static RecoveredContour?[] TraceContours(
        IndexedTriMesh terrain,
        List<int> picks,
        double planTolerance,
        double verticalTolerance,
        Func<double, double, double?> terrainAt)
    {
        var results = new RecoveredContour?[picks.Count];
        if (picks.Count == 0)
            return results;
        double[] vertices = terrain.Vertices;

        // A level exactly at a vertex makes every face meeting it a tie; nudge it off.
        double[] levels = picks.Select(v => vertices[v * 3 + 2] - Math.Max(Math.Abs(vertices[v * 3 + 2]), 1.0) * 1e-9).ToArray();
        List<ContourLevel> traced = ContourGenerator.Generate(terrain, levels, planTolerance * 1e-3);
        for (int i = 0; i < picks.Count; i++)
        {
            int vertex = picks[i];
            double level = vertices[vertex * 3 + 2];
            double x = vertices[vertex * 3], y = vertices[vertex * 3 + 1];
            ContourLevel? match = traced.FirstOrDefault(candidate => candidate.Z == levels[i]);
            if (match == null)
                continue;

            ContourPolyline? nearest = null;
            double nearestDistance = double.PositiveInfinity;
            foreach (ContourPolyline polyline in match.Polylines)
            {
                double distance = PlanDistanceToPolyline(polyline.PointsXyz, polyline.IsClosed, x, y);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = polyline;
                }
            }

            if (nearest == null || nearestDistance > planTolerance * 4.0)
                continue;

            double[] points = nearest.PointsXyz;
            if (nearest.IsClosed && points.Length >= 6 &&
                Math.Abs(points[0] - points[^3]) < 1e-12 && Math.Abs(points[1] - points[^2]) < 1e-12)
                points = points[..^3];

            double[] simplified = nearest.IsClosed
                ? SimplifyClosed(points, planTolerance, verticalTolerance, terrainAt)
                : SimplifyOpen(points, planTolerance, verticalTolerance, terrainAt);
            if (simplified.Length / 3 < (nearest.IsClosed ? 3 : 2))
                continue;
            for (int k = 2; k < simplified.Length; k += 3)
                simplified[k] = level;
            results[i] = new RecoveredContour { Level = level, PointsXyz = simplified, IsClosed = nearest.IsClosed };
        }

        return results;
    }

    private static TinResult? Rebuild(
        List<double[]> borders,
        List<double[]> landformLines,
        List<RecoveredContour> contours,
        List<double> spots,
        double planTolerance,
        out string? error,
        IReadOnlyList<double[]>? fixedBreaklines = null)
    {
        var xy = new List<double>();
        var z = new List<double>();
        var segments = new List<int>();
        var index = new Dictionary<CellKey, int>();
        double cell = Math.Max(planTolerance * 1e-3, 1e-9);

        int Add(double px, double py, double pz)
        {
            var key = new CellKey((long)Math.Round(px / cell), (long)Math.Round(py / cell));
            if (index.TryGetValue(key, out int existing))
                return existing;
            int added = z.Count;
            index[key] = added;
            xy.Add(px);
            xy.Add(py);
            z.Add(pz);
            return added;
        }

        void AddChain(double[] points, bool closed)
        {
            int count = points.Length / 3;
            int first = -1, previous = -1;
            for (int i = 0; i < count; i++)
            {
                int current = Add(points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
                if (i == 0)
                    first = current;
                else if (current != previous)
                {
                    segments.Add(previous);
                    segments.Add(current);
                }

                previous = current;
            }

            if (closed && count > 2 && previous != first)
            {
                segments.Add(previous);
                segments.Add(first);
            }
        }

        foreach (double[] border in borders)
            AddChain(border, closed: true);
        if (fixedBreaklines != null)
            foreach (double[] line in fixedBreaklines)
                AddChain(line, closed: false);
        foreach (double[] line in landformLines)
            AddChain(line, closed: false);
        foreach (RecoveredContour contour in contours)
            AddChain(contour.PointsXyz, contour.IsClosed);
        for (int i = 0; i < spots.Count; i += 3)
            Add(spots[i], spots[i + 1], spots[i + 2]);

        return new TinEngine().Build(
            xy.ToArray(), z.ToArray(), segments.ToArray(), QualitySettings.None, out error,
            useConvexHull: false, includeEdgeTopology: false);
    }

    /// <summary>The rebuild's vertical miss at every terrain vertex; 0 where the simplified border left it outside.</summary>
    private static double[] Deviations(double[] vertices, int vertexCount, TinResult rebuild, double tolerance, ITerrainRecoveryRegion? region = null)
    {
        var projector = new MeshHeightProjector(rebuild.Vertices, rebuild.VertexCount, rebuild.Faces, rebuild.FaceCount);
        var deviations = new double[vertexCount];
        double lookup = tolerance * 1e-3;
        for (int v = 0; v < vertexCount; v++)
        {
            double x = vertices[v * 3], y = vertices[v * 3 + 1], zRef = vertices[v * 3 + 2];
            if (region != null && !region.Contains(x, y))
                continue;
            // A vertex the simplified border left outside is the border's plan tolerance at work, not a miss.
            if (projector.TryGetHeightExtremes(x, y, lookup, out double low, out double high))
                deviations[v] = Math.Max(Math.Abs(zRef - low), Math.Abs(zRef - high));
        }

        return deviations;
    }

    /// <summary>Interior vertices higher (or lower) than every vertex within <paramref name="rings"/> rings.</summary>
    private static List<int> FindExtrema(double[] vertices, int vertexCount, MeshVertexAdjacency adjacency, int rings, bool[] onBorder)
    {
        var extrema = new List<int>();
        var visited = new Dictionary<int, int>();
        var frontier = new List<int>();
        var next = new List<int>();
        for (int v = 0; v < vertexCount; v++)
        {
            if (onBorder[v] || !adjacency.HasNeighbors(v))
                continue;

            double z = vertices[v * 3 + 2];
            bool isMax = true, isMin = true;
            foreach (int n in adjacency.NeighborsOf(v))
            {
                double nz = vertices[n * 3 + 2];
                isMax &= z > nz;
                isMin &= z < nz;
            }

            if (!isMax && !isMin)
                continue;

            visited.Clear();
            visited[v] = 0;
            frontier.Clear();
            frontier.Add(v);
            for (int ring = 1; ring <= rings && (isMax || isMin); ring++)
            {
                next.Clear();
                foreach (int u in frontier)
                {
                    foreach (int n in adjacency.NeighborsOf(u))
                    {
                        if (visited.ContainsKey(n))
                            continue;
                        visited[n] = ring;
                        next.Add(n);
                        double nz = vertices[n * 3 + 2];
                        isMax &= z > nz;
                        isMin &= z < nz;
                    }
                }

                (frontier, next) = (next, frontier);
            }

            if (isMax || isMin)
                extrema.Add(v);
        }

        return extrema;
    }

    private static double PlanDistanceToPolyline(double[] points, bool closed, double x, double y)
    {
        int count = points.Length / 3;
        double best = double.PositiveInfinity;
        int segments = closed ? count : count - 1;
        for (int i = 0; i < segments; i++)
        {
            int j = (i + 1) % count;
            best = Math.Min(best, PlanDistanceToSegment(
                x, y, points[i * 3], points[i * 3 + 1], points[j * 3], points[j * 3 + 1], out _));
        }

        return count == 1 ? Math.Sqrt(Sq(points[0] - x) + Sq(points[1] - y)) : best;
    }

    private static double PlanDistanceToSegment(double px, double py, double ax, double ay, double bx, double by, out double t)
    {
        double dx = bx - ax, dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        t = lengthSquared > 0.0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0.0, 1.0) : 0.0;
        return Math.Sqrt(Sq(ax + dx * t - px) + Sq(ay + dy * t - py));
    }

    private static double Sq(double value) => value * value;

    /// <summary>
    /// Douglas–Peucker on a closed loop (first point not repeated): split at the point farthest from the
    /// first, simplify both halves, rejoin. A point is kept when it lies further than
    /// <paramref name="planTolerance"/> from the chord in plan or further than
    /// <paramref name="verticalTolerance"/> from the chord's height at that point — or, given
    /// <paramref name="terrainAt"/>, when the terrain under the chord there is further than that from the
    /// chord's height, which is what a contour simplified across steep ground gets wrong.
    /// </summary>
    internal static double[] SimplifyClosed(double[] loop, double planTolerance, double verticalTolerance,
        Func<double, double, double?>? terrainAt = null)
    {
        int count = loop.Length / 3;
        if (count <= 3)
            return (double[])loop.Clone();
        int far = 0;
        double farthest = -1.0;
        for (int i = 1; i < count; i++)
        {
            double d = Sq(loop[i * 3] - loop[0]) + Sq(loop[i * 3 + 1] - loop[1]);
            if (d > farthest)
            {
                farthest = d;
                far = i;
            }
        }

        var keep = new bool[count + 1];
        keep[0] = keep[far] = keep[count] = true;
        double[] wrapped = new double[(count + 1) * 3];
        Array.Copy(loop, wrapped, loop.Length);
        Array.Copy(loop, 0, wrapped, count * 3, 3);
        Mark(wrapped, 0, far, planTolerance, verticalTolerance, terrainAt, keep);
        Mark(wrapped, far, count, planTolerance, verticalTolerance, terrainAt, keep);
        var output = new List<double>();
        for (int i = 0; i < count; i++)
        {
            if (!keep[i])
                continue;
            output.Add(loop[i * 3]);
            output.Add(loop[i * 3 + 1]);
            output.Add(loop[i * 3 + 2]);
        }

        return output.ToArray();
    }

    internal static double[] SimplifyOpen(double[] points, double planTolerance, double verticalTolerance,
        Func<double, double, double?>? terrainAt = null)
    {
        int count = points.Length / 3;
        if (count <= 2)
            return (double[])points.Clone();
        var keep = new bool[count];
        keep[0] = keep[count - 1] = true;
        Mark(points, 0, count - 1, planTolerance, verticalTolerance, terrainAt, keep);
        var output = new List<double>();
        for (int i = 0; i < count; i++)
        {
            if (!keep[i])
                continue;
            output.Add(points[i * 3]);
            output.Add(points[i * 3 + 1]);
            output.Add(points[i * 3 + 2]);
        }

        return output.ToArray();
    }

    private static void Mark(double[] points, int first, int last, double planTolerance, double verticalTolerance,
        Func<double, double, double?>? terrainAt, bool[] keep)
    {
        var stack = new Stack<(int First, int Last)>();
        stack.Push((first, last));
        while (stack.Count > 0)
        {
            (int a, int b) = stack.Pop();
            if (b - a < 2)
                continue;
            double ax = points[a * 3], ay = points[a * 3 + 1], az = points[a * 3 + 2];
            double bx = points[b * 3], by = points[b * 3 + 1], bz = points[b * 3 + 2];
            int worst = -1;
            double worstScore = 1.0;
            for (int i = a + 1; i < b; i++)
            {
                double plan = PlanDistanceToSegment(points[i * 3], points[i * 3 + 1], ax, ay, bx, by, out double t);
                double vertical = Math.Abs(points[i * 3 + 2] - (az + (bz - az) * t));
                double chordZ = az + (bz - az) * t;
                if (terrainAt != null &&
                    terrainAt(ax + (bx - ax) * t, ay + (by - ay) * t) is { } ground)
                    vertical = Math.Max(vertical, Math.Abs(ground - chordZ));
                double score = Math.Max(plan / planTolerance,
                    double.IsPositiveInfinity(verticalTolerance) ? 0.0 : vertical / verticalTolerance);
                if (score > worstScore)
                {
                    worstScore = score;
                    worst = i;
                }
            }

            if (worst < 0)
                continue;
            keep[worst] = true;
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
    }

    private static Result Unsupported(string reason) => new()
    {
        Borders = Array.Empty<double[]>(),
        Contours = Array.Empty<RecoveredContour>(),
        LandformLines = Array.Empty<double[]>(),
        SpotPoints = Array.Empty<double>(),
        MaximumDeviation = double.NaN,
        Rounds = 0,
        Termination = Termination.UnsupportedInput,
        Diagnostic = reason
    };

    private readonly record struct CellKey(long X, long Y);
}
