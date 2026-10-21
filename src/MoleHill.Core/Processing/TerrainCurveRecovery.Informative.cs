using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Processing;

public static partial class TerrainCurveRecovery
{
    /// <summary>
    /// Measures the accepted output against another reference using the same fixed inputs. Ground in
    /// <see cref="Options.Unmeasured"/> counts no miss, as in the recovery itself: a scan's gap fills were
    /// never measured, so the original ground there is the fill's guess, not a height to be within.
    /// </summary>
    public static double MeasureMaximumDeviation(Result recovered, IndexedTriMesh reference, Options options)
    {
        var rebuilt = Rebuild(recovered.Borders.ToList(), recovered.LandformLines.ToList(), recovered.Contours.ToList(),
            recovered.SpotPoints.ToList(), options.PlanTolerance, out _, options.FixedBreaklines);
        if (rebuilt == null) return double.NaN;
        double[] deviations = Deviations(reference.Vertices, reference.VertexCount, rebuilt, options.VerticalTolerance, options.Region);
        double maximum = 0;
        for (int v = 0; v < deviations.Length; v++)
            if (options.Unmeasured?.Invoke(reference.Vertices[v * 3], reference.Vertices[v * 3 + 1]) != true)
                maximum = Math.Max(maximum, deviations[v]);
        return maximum;
    }

    private readonly record struct RecoveryError(double Maximum, double Excess, double Area);

    private static Result RecoverInformative(IndexedTriMesh terrain, Options options, bool useLandforms)
    {
        var policy = options.Policy!;
        if (policy.PreferredContours < 1 || policy.MaximumInputs < 1 || policy.PruneInterval < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Recovery limits must be positive.");
        double tolerance = options.VerticalTolerance, plan = options.PlanTolerance > 0 ? options.PlanTolerance : tolerance;
        if (!double.IsFinite(tolerance) || tolerance <= 0 || terrain.VertexCount < 3 || terrain.FaceCount < 1)
            return Unsupported("A positive tolerance and connected terrain are required.");
        var vertices = terrain.Vertices; var faces = terrain.Faces; int count = terrain.VertexCount;
        if (!MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(faces, terrain.FaceCount, out var loops) || loops.Count == 0)
            return Unsupported("The terrain border is not simple.");
        var borders = loops.Select(loop => SimplifyClosed(loop.SelectMany(v => new[] {
            vertices[v * 3], vertices[v * 3 + 1], vertices[v * 3 + 2] }).ToArray(), plan, tolerance)).Where(p => p.Length >= 9).ToList();
        if (borders.Count == 0) return Unsupported("The terrain border collapsed.");
        var weights = new double[count];
        for (int f = 0; f < terrain.FaceCount; f++)
        {
            int a = faces[f * 3] * 3, b = faces[f * 3 + 1] * 3, c = faces[f * 3 + 2] * 3;
            double area = Math.Abs((vertices[b] - vertices[a]) * (vertices[c + 1] - vertices[a + 1]) -
                (vertices[b + 1] - vertices[a + 1]) * (vertices[c] - vertices[a])) / 6;
            weights[a / 3] += area; weights[b / 3] += area; weights[c / 3] += area;
        }
        int evaluated = 0;
        for (int i = 0; i < count; i++)
            if (options.Region != null && !options.Region.Contains(vertices[i * 3], vertices[i * 3 + 1])) weights[i] = 0;
            else evaluated++;
        var adjacency = MeshVertexAdjacency.Build(new List<int>(faces[..(terrain.FaceCount * 3)]), terrain.FaceCount, count);
        var borderNodes = new bool[count]; foreach (var loop in loops) foreach (int v in loop) borderNodes[v] = true;
        var contours = new List<RecoveredContour>(); var spots = new List<double>(); var spotIds = new List<int>();
        var lines = useLandforms ? SelectLandformLines(vertices, count, adjacency, borderNodes, borders, spots,
            tolerance, plan, options.FixedBreaklines, options.Region).Take(policy.MaximumInputs).ToList() : new List<double[]>();
        // Establish broad terrain coverage before competing local trials. Starting with isolated
        // benefit-per-control picks alone can spend hundreds of spots on a large scan while leaving
        // useful contour coverage behind the existing recovery strategy. The seed uses the same
        // fitted output, region and mandatory lines, and remains subject to pruning and the total cap.
        bool seeded = policy.PreferredContours >= 16 && (long)policy.MaximumInputs >= (long)policy.PreferredContours * 2;
        if (seeded && !Cancelled())
        {
            policy.ReportActivity?.Invoke("Selecting the initial useful contour coverage");
            var seed = Recover(terrain, new Options
            {
                VerticalTolerance = tolerance, PlanTolerance = plan, MaximumCurves = policy.PreferredContours,
                ExtremumRings = options.ExtremumRings, ContoursPerRound = options.ContoursPerRound,
                FixedBreaklines = options.FixedBreaklines.Concat(lines).ToArray(), Region = options.Region,
                FitContours = options.FitContours, LandformLines = false, ShouldCancel = options.ShouldCancel,
                ReportProgress = options.ReportProgress
            }, false);
            foreach (var contour in seed.Contours)
                if (contours.Count + lines.Count < policy.MaximumInputs) contours.Add(contour);
            for (int i = 0; i < seed.SpotPoints.Length && contours.Count + lines.Count + spotIds.Count < policy.MaximumInputs; i += 3)
            {
                int id = -1; double nearest = double.PositiveInfinity;
                for (int v = 0; v < count; v++)
                {
                    double dx = vertices[v * 3] - seed.SpotPoints[i], dy = vertices[v * 3 + 1] - seed.SpotPoints[i + 1];
                    double distance = dx * dx + dy * dy;
                    if (distance < nearest) { nearest = distance; id = v; }
                }
                if (id >= 0 && !spotIds.Contains(id))
                { spots.Add(vertices[id * 3]); spots.Add(vertices[id * 3 + 1]); spots.Add(vertices[id * 3 + 2]); spotIds.Add(id); }
            }
        }
        var projector = new MeshHeightProjector(vertices, count, faces, terrain.FaceCount);
        double? Height(double x, double y) => projector.TryGetHeightExtremes(x, y, tolerance * 1e-3, out _, out double z) ? z : null;
        var trials = new LocalTrials(vertices, count, weights, tolerance, plan);
        double epsilon = Math.Max(1e-15, weights.Sum() * tolerance * 1e-10);
        double peakArea = 0;
        var blocked = new HashSet<int>(); int round = 0, pruned = 0, rejected = 0, accepted = 0, pruneCursor = 0, sincePrune = 0;
        int checkpoint = policy.PreferredContours + (seeded ? 100 : 0), unproductiveRounds = 0;
        double[] deviations = Array.Empty<double>();
        int[] ranked = Array.Empty<int>();
        RecoveryError error = default;
        Termination termination = Termination.NoProgress;
        while (true)
        {
            if (Cancelled()) { termination = Termination.Cancelled; break; }
            if (!Evaluate()) { termination = Termination.TriangulationFailed; break; }
            if (seeded) { Prune(false); seeded = false; }
            options.ReportProgress?.Invoke(round, error.Maximum);
            if (error.Maximum <= tolerance) { termination = Termination.ToleranceSatisfied; Prune(true); break; }
            if (InputCount() >= policy.MaximumInputs)
            {
                Prune(true);
                if (InputCount() >= policy.MaximumInputs) { termination = Termination.CurveBudgetSpent; break; }
            }
            if (contours.Count >= checkpoint)
            {
                Prune(true);
                policy.ReportActivity?.Invoke($"{contours.Count} contours: removed {pruned} redundant inputs; continuing while useful.");
                checkpoint += 100;
            }
            // Candidates in separate windows cannot change each other's reconstruction, so one round tries
            // many and confirms them with a single global rebuild.
            var candidates = ErrorRegionPicks(deviations, weights, adjacency, blocked, PicksPerRound, tolerance);
            if (candidates.Count == 0) { termination = Termination.NoProgress; break; }
            var traced = TraceContours(terrain, candidates, plan, tolerance, Height);
            var claimed = new List<Box>();
            int additions = 0;
            for (int k = 0; k < candidates.Count && InputCount() < policy.MaximumInputs; k++)
            {
                if (Cancelled()) break;
                int v = candidates[k];
                var pieces = ClipContour(traced[k], options.Region, vertices[v * 3], vertices[v * 3 + 1])
                    .Where(c => !contours.Any(e => Math.Abs(e.Level - c.Level) <= tolerance * 1e-6 && SameStart(e, c))).ToList();
                Box input = new(vertices[v * 3], vertices[v * 3 + 1], vertices[v * 3], vertices[v * 3 + 1]);
                foreach (var piece in pieces) input = input.Union(Box.Of(piece.PointsXyz));
                var (measured, context) = trials.Window(input);
                if (claimed.Any(c => c.Overlaps(context))) continue;
                if (trials.Measure(measured, context, Chains(), spots) is not LocalError before) { blocked.Add(v); rejected++; continue; }
                double outside = MaximumOutside(measured);
                RecoveredContour? bestCurve = null; bool bestSpot = false; double bestScore = 0;
                foreach (var piece in pieces)
                {
                    if (trials.Measure(measured, context, Chains().Append((piece.PointsXyz, piece.IsClosed)), spots) is not LocalError after) continue;
                    double score = Score(before, after, outside, 1 + .05 * piece.PointsXyz.Length / 3);
                    if (score > bestScore) { bestScore = score; bestCurve = piece; }
                }
                if (!spotIds.Contains(v) && trials.Measure(measured, context, Chains(),
                    spots.Concat(new[] { vertices[v * 3], vertices[v * 3 + 1], vertices[v * 3 + 2] })) is LocalError spotted)
                {
                    double score = Score(before, spotted, outside, 1);
                    if (score > bestScore) { bestScore = score; bestSpot = true; bestCurve = null; }
                }
                if (bestCurve != null)
                    contours.Add(options.FitContours ? FitContour(bestCurve, plan, tolerance, Height, options.Region) : bestCurve);
                else if (bestSpot) AppendSpot(v);
                else { blocked.Add(v); rejected++; continue; }
                claimed.Add(context);
                additions++; accepted++; sincePrune++;
            }
            if (Cancelled()) { termination = Termination.Cancelled; break; }
            if (additions == 0)
            {
                if (++unproductiveRounds >= 8) { termination = Termination.NoProgress; break; }
                continue;
            }
            unproductiveRounds = 0; round++;
            if (sincePrune >= policy.PruneInterval)
            {
                sincePrune = 0;
                if (!Evaluate()) { termination = Termination.TriangulationFailed; break; }
                Prune(false);
                blocked.Clear();
                policy.ReportActivity?.Invoke($"{contours.Count} contours, {spotIds.Count} spots; {pruned} redundant inputs removed.");
            }
        }
        if (Cancelled()) termination = Termination.Cancelled;
        Evaluate();
        string diagnostic = termination == Termination.ToleranceSatisfied
            ? $"Within {options.Length(tolerance)} at reference vertices; removed {pruned} redundant inputs."
            : $"{Describe(termination)}: {contours.Count} contours, {spotIds.Count} spots; {options.Length(error.Maximum)} miss remains. " +
              $"Removed {pruned} redundant inputs.";
        return new Result
        {
            Borders = borders,
            Contours = contours,
            LandformLines = lines,
            SpotPoints = spots.ToArray(),
            MaximumDeviation = error.Maximum,
            Rounds = round,
            Termination = termination,
            Diagnostic = diagnostic,
            EvaluatedVertexCount = evaluated,
            ExcessErrorVolume = error.Excess,
            AreaOutsideTolerance = error.Area,
            PrunedInputCount = pruned,
            RejectedCandidateCount = rejected
        };

        bool Cancelled() => options.ShouldCancel?.Invoke() == true;
        int InputCount() => contours.Count + spotIds.Count + lines.Count;
        void AppendSpot(int v) { spots.Add(vertices[v * 3]); spots.Add(vertices[v * 3 + 1]); spots.Add(vertices[v * 3 + 2]); spotIds.Add(v); }

        IEnumerable<(double[] Points, bool Closed)> Chains(int skipContour = -1, int skipLine = -1)
        {
            foreach (double[] border in borders) yield return (border, true);
            foreach (double[] line in options.FixedBreaklines) yield return (line, false);
            for (int i = 0; i < lines.Count; i++) if (i != skipLine) yield return (lines[i], false);
            for (int i = 0; i < contours.Count; i++) if (i != skipContour) yield return (contours[i].PointsXyz, contours[i].IsClosed);
        }

        IEnumerable<double> SpotsWithout(int skip)
        {
            for (int i = 0; i < spots.Count; i++) if (i / 3 != skip) yield return spots[i];
        }

        // One global rebuild: the measure every local trial is compared against, and the face windows they use.
        bool Evaluate()
        {
            var tin = Rebuild(borders, lines, contours, spots, plan, out _, options.FixedBreaklines);
            if (tin == null) { error = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity); return false; }
            deviations = Deviations(vertices, count, tin, tolerance);
            double max = 0, excess = 0, area = 0;
            for (int i = 0; i < count; i++) if (weights[i] > 0)
            { max = Math.Max(max, deviations[i]); excess += weights[i] * Math.Max(0, deviations[i] - tolerance); if (deviations[i] > tolerance) area += weights[i]; }
            error = new(max, excess, area);
            peakArea = Math.Max(weights.Sum() / count, area * .01);
            ranked = Enumerable.Range(0, count).Where(i => weights[i] > 0 && deviations[i] > tolerance)
                .OrderByDescending(i => deviations[i]).Take(512).ToArray();
            trials.SetReconstruction(tin);
            return true;
        }

        double MaximumOutside(Box measured)
        {
            foreach (int i in ranked)
                if (!measured.Contains(vertices[i * 3], vertices[i * 3 + 1])) return deviations[i];
            return ranked.Length < 512 ? tolerance : deviations[ranked[^1]];
        }

        double Score(LocalError before, LocalError after, double outside, double complexity)
        {
            double trialMaximum = Math.Max(outside, after.Maximum);
            double maximumGain = error.Maximum - trialMaximum, volumeGain = before.Excess - after.Excess;
            // Correcting a real peak can redistribute neighbouring interpolation error. Requiring
            // both metrics to improve traps recovery even when a spot removes the worst miss.
            bool usefulPeak = maximumGain >= tolerance * .05;
            bool usefulArea = volumeGain > epsilon && trialMaximum <= error.Maximum + tolerance * .1;
            if (!usefulPeak && !usefulArea) return 0;
            return (Math.Max(0, volumeGain) + Math.Max(0, maximumGain) * peakArea) / complexity;
        }

        // Removes inputs whose own neighbourhood reconstructs no worse without them. Trials in separate
        // windows are independent; the batch is confirmed globally and undone if it made anything worse.
        void Prune(bool final)
        {
            int total = InputCount(), checks = final ? total : Math.Min(total, 32);
            var claimed = new List<Box>();
            var removed = new List<(RecoveredContour? Curve, double[]? Spot, int Id, double[]? Line)>();
            RecoveryError previous = error;
            for (int i = 0; i < checks && InputCount() > 0 && !Cancelled(); i++)
            {
                int index = pruneCursor % InputCount();
                double[] geometry = index < contours.Count ? contours[index].PointsXyz
                    : index < contours.Count + spotIds.Count ? spots.GetRange((index - contours.Count) * 3, 3).ToArray()
                    : lines[index - contours.Count - spotIds.Count];
                var (measured, context) = trials.Window(Box.Of(geometry));
                if (claimed.Any(c => c.Overlaps(context))) { pruneCursor++; continue; }
                int contour = index < contours.Count ? index : -1;
                int spot = contour < 0 && index < contours.Count + spotIds.Count ? index - contours.Count : -1;
                int line = contour < 0 && spot < 0 ? index - contours.Count - spotIds.Count : -1;
                bool remove = trials.Measure(measured, context, Chains(), spots) is LocalError with &&
                    trials.Measure(measured, context, Chains(contour, line), SpotsWithout(spot)) is LocalError without &&
                    Math.Max(without.Maximum, MaximumOutside(measured)) <= (previous.Maximum <= tolerance ? tolerance : previous.Maximum + tolerance * 1e-7) &&
                    without.Excess <= with.Excess + epsilon;
                if (!remove) { pruneCursor++; continue; }
                if (contour >= 0) { removed.Add((contours[contour], null, -1, null)); contours.RemoveAt(contour); }
                else if (spot >= 0) { removed.Add((null, spots.GetRange(spot * 3, 3).ToArray(), spotIds[spot], null)); spots.RemoveRange(spot * 3, 3); spotIds.RemoveAt(spot); }
                else { removed.Add((null, null, -1, lines[line])); lines.RemoveAt(line); }
                claimed.Add(context);
            }
            if (removed.Count == 0) return;
            if (Evaluate() && error.Maximum <= (previous.Maximum <= tolerance ? tolerance : previous.Maximum + tolerance * 1e-7) &&
                error.Excess <= previous.Excess + epsilon * removed.Count)
            { pruned += removed.Count; return; }
            foreach (var item in removed)
            {
                if (item.Curve != null) contours.Add(item.Curve);
                else if (item.Spot != null) { spots.AddRange(item.Spot); spotIds.Add(item.Id); }
                else lines.Add(item.Line!);
            }
            Evaluate();
        }
    }

    private const int PicksPerRound = 16;

    private static List<int> ErrorRegionPicks(double[] miss, double[] weights, MeshVertexAdjacency adjacency,
        HashSet<int> blocked, int maximum, double tolerance)
    {
        var seen = new bool[miss.Length]; var regions = new List<(int Vertex, double Error)>();
        for (int seed = 0; seed < miss.Length; seed++)
        {
            if (seen[seed] || weights[seed] <= 0 || miss[seed] <= tolerance) continue;
            var queue = new Queue<int>(); queue.Enqueue(seed); seen[seed] = true;
            int worst = -1; double total = 0;
            while (queue.TryDequeue(out int v))
            {
                total += weights[v] * (miss[v] - tolerance);
                if (!blocked.Contains(v) && (worst < 0 || miss[v] > miss[worst])) worst = v;
                foreach (int next in adjacency.NeighborsOf(v))
                    if (!seen[next] && weights[next] > 0 && miss[next] > tolerance) { seen[next] = true; queue.Enqueue(next); }
            }
            if (worst >= 0) regions.Add((worst, total));
        }
        // Always reserve one candidate for the worst miss: a small but important peak must not
        // starve behind a large region whose current candidates cannot improve its interpolation.
        int peak = Enumerable.Range(0, miss.Length).Where(v => weights[v] > 0 && miss[v] > tolerance && !blocked.Contains(v))
            .OrderByDescending(v => miss[v]).ThenBy(v => v).DefaultIfEmpty(-1).First();
        return (peak < 0 ? Enumerable.Empty<int>() : new[] { peak })
            .Concat(regions.OrderByDescending(r => r.Error).ThenBy(r => r.Vertex).Select(r => r.Vertex))
            .Distinct().Take(maximum).ToList();
    }
}
