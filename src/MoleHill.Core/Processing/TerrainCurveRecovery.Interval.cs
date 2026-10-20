using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Processing;

/// <summary>
/// Interval recovery: contours at a fixed height interval within the region, redundant ones removed, then
/// spot heights only where the reconstruction still misses. Chasing individual errors instead left slopes
/// covered in short contour fragments and over a thousand spots on the reference scan. A secondary region
/// (scan paving, described by its outline breaklines) is measured at its own tolerance and takes spots only.
/// </summary>
public static partial class TerrainCurveRecovery
{
    private static Result RecoverInterval(IndexedTriMesh terrain, Options options)
    {
        var policy = options.Policy!;
        double interval = policy.ContourInterval, tolerance = options.VerticalTolerance;
        double plan = options.PlanTolerance > 0 ? options.PlanTolerance : tolerance;
        if (!double.IsFinite(interval) || policy.MaximumInputs < 1 || !double.IsFinite(policy.InputsPerArea) || policy.InputsPerArea < 0 ||
            options.SecondaryRegion != null &&
            !(double.IsFinite(options.SecondaryTolerance) && options.SecondaryTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(options), "Interval recovery needs a finite interval, a positive cap and a positive secondary tolerance.");
        if (!double.IsFinite(tolerance) || tolerance <= 0 || terrain.VertexCount < 3 || terrain.FaceCount < 1)
            return Unsupported("A positive tolerance and connected terrain are required.");
        double[] vertices = terrain.Vertices; int[] faces = terrain.Faces; int count = terrain.VertexCount;
        if (!MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(faces, terrain.FaceCount, out var loops) || loops.Count == 0)
            return Unsupported("The terrain border is not simple.");
        var borders = loops.Select(loop => SimplifyClosed(loop.SelectMany(v => new[] {
            vertices[v * 3], vertices[v * 3 + 1], vertices[v * 3 + 2] }).ToArray(), plan, tolerance)).Where(p => p.Length >= 9).ToList();
        if (borders.Count == 0) return Unsupported("The terrain border collapsed.");

        // Each measured vertex carries its own tolerance; unmeasured vertices weigh nothing.
        var weights = new double[count]; var tolerances = new double[count]; var secondary = new bool[count];
        for (int f = 0; f < terrain.FaceCount; f++)
        {
            int a = faces[f * 3] * 3, b = faces[f * 3 + 1] * 3, c = faces[f * 3 + 2] * 3;
            double area = Math.Abs((vertices[b] - vertices[a]) * (vertices[c + 1] - vertices[a + 1]) -
                (vertices[b + 1] - vertices[a + 1]) * (vertices[c] - vertices[a])) / 6;
            weights[a / 3] += area; weights[b / 3] += area; weights[c / 3] += area;
        }
        int evaluated = 0;
        double low = double.PositiveInfinity, high = double.NegativeInfinity, unmeasured = 0;
        for (int v = 0; v < count; v++)
        {
            double x = vertices[v * 3], y = vertices[v * 3 + 1];
            tolerances[v] = tolerance;
            if (options.Unmeasured?.Invoke(x, y) == true && (options.Region == null || options.Region.Contains(x, y) ||
                options.SecondaryRegion?.Contains(x, y) == true))
            {
                // Contours still cover it, so its heights still set the levels traced.
                if (options.Region == null || options.Region.Contains(x, y))
                { low = Math.Min(low, vertices[v * 3 + 2]); high = Math.Max(high, vertices[v * 3 + 2]); }
                unmeasured += weights[v]; weights[v] = 0;
                continue;
            }
            if (options.Region == null || options.Region.Contains(x, y))
            { evaluated++; low = Math.Min(low, vertices[v * 3 + 2]); high = Math.Max(high, vertices[v * 3 + 2]); }
            else if (options.SecondaryRegion?.Contains(x, y) == true) { secondary[v] = true; tolerances[v] = options.SecondaryTolerance; evaluated++; }
            else weights[v] = 0;
        }
        int cap = policy.InputsPerArea > 0
            ? (int)Math.Clamp(Math.Ceiling(weights.Sum() * policy.InputsPerArea), 1, int.MaxValue) : policy.MaximumInputs;
        var adjacency = MeshVertexAdjacency.Build(new List<int>(faces[..(terrain.FaceCount * 3)]), terrain.FaceCount, count);
        var projector = new MeshHeightProjector(vertices, count, faces, terrain.FaceCount);
        double? Height(double x, double y) => projector.TryGetHeightExtremes(x, y, tolerance * 1e-3, out _, out double z) ? z : null;
        bool Cancelled() => options.ShouldCancel?.Invoke() == true;

        // Contours at the interval, clipped to the region, simplified against the terrain below them.
        policy.ReportActivity?.Invoke($"Tracing contours every {interval:G4}");
        var contours = new List<RecoveredContour>();
        var traced = new List<double[]>();
        double minimumLength = policy.MinimumContourLength > 0 ? policy.MinimumContourLength : plan * 8;
        if (high >= low)
        {
            var levels = new List<double>();
            for (double z = Math.Ceiling(low / interval) * interval; z <= high; z += interval)
                levels.Add(z - Math.Max(Math.Abs(z), 1.0) * 1e-9);
            foreach (ContourLevel level in ContourGenerator.Generate(terrain, levels, plan * 1e-3))
            {
                double levelZ = Math.Round(level.Z / interval) * interval + 0.0; // + 0.0 turns -0 into 0: "Contour -0.000"
                foreach (ContourPolyline polyline in level.Polylines)
                {
                    double[] points = polyline.PointsXyz;
                    if (polyline.IsClosed && points.Length >= 6 && points[0] == points[^3] && points[1] == points[^2]) points = points[..^3];
                    var parts = options.Region == null
                        ? new[] { new RegionInputClipper.InputPolyline(points, points.Length / 3, polyline.IsClosed) }
                        : options.Region.Clip(points, polyline.IsClosed);
                    foreach (var part in parts)
                    {
                        double[] simple = part.IsClosed ? SimplifyClosed(part.Points, plan, tolerance, Height) : SimplifyOpen(part.Points, plan, tolerance, Height);
                        if (simple.Length / 3 < (part.IsClosed ? 3 : 2) || ChainLength(simple, part.IsClosed) < minimumLength) continue;
                        for (int k = 2; k < simple.Length; k += 3) simple[k] = levelZ;
                        contours.Add(new RecoveredContour { Level = levelZ, PointsXyz = simple, IsClosed = part.IsClosed });
                        traced.Add(part.Points);
                    }
                }
            }
        }
        // Simplification can cut across a hairpin of the same contour or a close neighbour. Those contours are
        // simplified again at half the plan tolerance, and finally kept as traced; traced contours never cross.
        for (int pass = 0; ; pass++)
        {
            var crossing = PolylineCrossings.Find(contours.Select(c => (c.PointsXyz, c.IsClosed)).ToList());
            if (crossing.Count == 0) break;
            double finer = plan * Math.Pow(.5, pass + 1);
            foreach (int i in crossing)
            {
                RecoveredContour contour = contours[i];
                double[] simple = pass >= 3 ? (double[])traced[i].Clone()
                    : contour.IsClosed ? SimplifyClosed(traced[i], finer, tolerance, Height) : SimplifyOpen(traced[i], finer, tolerance, Height);
                for (int k = 2; k < simple.Length; k += 3) simple[k] = contour.Level;
                contours[i] = new RecoveredContour { Level = contour.Level, PointsXyz = simple, IsClosed = contour.IsClosed };
            }
            if (pass >= 3) break;
        }
        int tracedCount = contours.Count;

        var spots = new List<double>(); var spotIds = new HashSet<int>();
        var trials = new LocalTrials(vertices, count, weights, tolerances, plan);
        double epsilon = Math.Max(1e-15, weights.Sum() * tolerance * 1e-10);
        double[] deviations = Array.Empty<double>();
        var uncovered = new bool[count];
        TinResult? last = null;
        double excess = 0, ratio = 0;
        if (!Evaluate()) return Failed();

        // Remove contours whose own neighbourhood reconstructs no worse without them, shortest first.
        // Trials in separate windows are independent; each sweep is confirmed globally and undone if worse.
        policy.ReportActivity?.Invoke($"Removing redundant contours from {tracedCount}");
        int pruned = 0;
        var untested = new HashSet<RecoveredContour>(contours);
        while (untested.Count > 0 && !Cancelled())
        {
            var claimed = new List<Box>(); var removed = new List<RecoveredContour>();
            double previousExcess = excess, previousRatio = ratio;
            foreach (RecoveredContour contour in contours.Where(untested.Contains).OrderBy(c => ChainLength(c.PointsXyz, c.IsClosed)).ToList())
            {
                if (Cancelled()) break;
                var (measured, context) = trials.Window(Box.Of(contour.PointsXyz));
                if (claimed.Any(c => c.Overlaps(context))) continue;
                untested.Remove(contour);
                int index = contours.IndexOf(contour);
                if (trials.Measure(measured, context, Chains(), spots) is LocalError with &&
                    trials.Measure(measured, context, Chains(index), spots) is LocalError without &&
                    without.Excess <= with.Excess + epsilon && without.Ratio <= Math.Max(1, with.Ratio) + 1e-9)
                { contours.RemoveAt(index); removed.Add(contour); claimed.Add(context); }
            }
            if (!Evaluate()) return Failed();
            if (removed.Count > 0 && (excess > previousExcess + epsilon * removed.Count || ratio > Math.Max(1, previousRatio) + 1e-9))
            {
                contours.AddRange(removed);
                if (!Evaluate()) return Failed();
            }
            else pruned += removed.Count;
            options.ReportProgress?.Invoke(0, MaximumOf(primary: true));
        }

        if (options.FitContours)
        {
            policy.ReportActivity?.Invoke($"Fitting {contours.Count} contours");
            // Each contour fits alone against read-only terrain, so they fit in parallel.
            var fitted = new RecoveredContour[contours.Count];
            Parallel.For(0, contours.Count, (i, state) =>
            {
                if (Cancelled()) { state.Stop(); return; }
                fitted[i] = SmoothContour(contours[i], plan, tolerance, Height, options.Region) ?? FitContour(contours[i], plan, tolerance, Height, options.Region);
            });
            for (int i = 0; i < fitted.Length; i++) fitted[i] ??= contours[i];
            // A corner cut can cross a close neighbour; such contours keep the polylines they were measured with.
            for (int pass = 0; pass < 3; pass++)
            {
                var crossing = PolylineCrossings.Find(fitted.Select(c => (c.Curve?.PointsXyz ?? c.PointsXyz, c.IsClosed && c.Curve == null)).ToList());
                if (crossing.Count == 0) break;
                foreach (int i in crossing) fitted[i] = contours[i];
            }
            contours = fitted.ToList();
            if (!Evaluate()) return Failed();
        }

        // Spot heights only where a vertex still misses its own tolerance: one per separated error region.
        Termination termination = Termination.ToleranceSatisfied;
        int round = 0;
        var blocked = new HashSet<int>();
        while (true)
        {
            if (Cancelled()) { termination = Termination.Cancelled; break; }
            options.ReportProgress?.Invoke(++round, MaximumOf(primary: true));
            if (ratio <= 1) { termination = Termination.ToleranceSatisfied; break; }
            if (contours.Count + spotIds.Count >= cap) { termination = Termination.CurveBudgetSpent; break; }
            var scaled = new double[count];
            for (int v = 0; v < count; v++) scaled[v] = deviations[v] / tolerances[v] * tolerance;
            var picks = ErrorRegionPicks(scaled, weights, adjacency, blocked, 256, tolerance);
            int added = 0;
            foreach (int v in picks)
            {
                if (contours.Count + spotIds.Count >= cap) break;
                if (!spotIds.Add(v)) { blocked.Add(v); continue; }
                spots.Add(vertices[v * 3]); spots.Add(vertices[v * 3 + 1]); spots.Add(vertices[v * 3 + 2]); added++;
            }
            if (added == 0) { termination = Termination.NoProgress; break; }
            if (!Evaluate()) return Failed();
        }

        double primaryMaximum = MaximumOf(primary: true);
        double? secondaryMaximum = options.SecondaryRegion == null ? null : MaximumOf(primary: false);
        double outside = 0, uncoveredArea = 0;
        for (int v = 0; v < count; v++)
        {
            if (weights[v] > 0 && deviations[v] > tolerances[v]) outside += weights[v];
            if (weights[v] > 0 && uncovered[v]) uncoveredArea += weights[v];
        }
        string accuracy = $"within {tolerance:G4}: worst {primaryMaximum:G4}" +
            (secondaryMaximum is double s ? $"; secondary within {options.SecondaryTolerance:G4}: worst {s:G4}" : string.Empty);
        return new Result
        {
            Borders = borders,
            Contours = contours,
            LandformLines = Array.Empty<double[]>(),
            SpotPoints = spots.ToArray(),
            MaximumDeviation = primaryMaximum,
            SecondaryMaximumDeviation = secondaryMaximum,
            Rounds = round,
            Termination = termination,
            Diagnostic = $"{(termination == Termination.ToleranceSatisfied ? "Met" : termination.ToString())} (cap {cap}): contours every {interval:G4} " +
                $"({pruned} of {tracedCount} redundant removed), {accuracy}.",
            EvaluatedVertexCount = evaluated,
            ExcessErrorVolume = excess,
            AreaOutsideTolerance = outside,
            PrunedInputCount = pruned,
            UncoveredArea = uncoveredArea,
            UnmeasuredArea = unmeasured,
            Misses = MissRegions(),
        };

        IEnumerable<(double[] Points, bool Closed)> Chains(int skipContour = -1)
        {
            foreach (double[] border in borders) yield return (border, true);
            foreach (double[] line in options.FixedBreaklines) yield return (line, false);
            for (int i = 0; i < contours.Count; i++) if (i != skipContour) yield return (contours[i].PointsXyz, contours[i].IsClosed);
        }

        bool Evaluate()
        {
            var tin = Rebuild(borders, new List<double[]>(), contours, spots, plan, out _, options.FixedBreaklines);
            if (tin == null) return false;
            last = tin;
            Array.Clear(uncovered);
            deviations = Deviations(vertices, count, tin, tolerance, uncovered: uncovered);
            excess = 0; ratio = 0;
            for (int v = 0; v < count; v++)
                if (weights[v] > 0)
                { excess += weights[v] * Math.Max(0, deviations[v] - tolerances[v]); ratio = Math.Max(ratio, deviations[v] / tolerances[v]); }
            trials.SetReconstruction(tin);
            return true;
        }

        double MaximumOf(bool primary)
        {
            double maximum = 0;
            for (int v = 0; v < count; v++) if (weights[v] > 0 && secondary[v] != primary) maximum = Math.Max(maximum, deviations[v]);
            return maximum;
        }

        // Misses connected through vertices that miss the same way: a step missed on both sides is two regions.
        List<MissRegion> MissRegions()
        {
            var rebuilt = last == null ? null : new MeshHeightProjector(last.Vertices, last.VertexCount, last.Faces, last.FaceCount);
            var signed = new double[count];
            for (int v = 0; v < count; v++)
            {
                if (weights[v] <= 0 || deviations[v] <= tolerances[v]) continue;
                double x = vertices[v * 3], y = vertices[v * 3 + 1], z = vertices[v * 3 + 2];
                signed[v] = rebuilt != null && rebuilt.TryGetHeightExtremes(x, y, tolerance * 1e-3, out double lo, out double hi)
                    ? Math.Abs(z - lo) >= Math.Abs(z - hi) ? z - lo : z - hi : double.NaN;
            }
            var seen = new bool[count]; var misses = new List<MissRegion>();
            for (int seed = 0; seed < count; seed++)
            {
                if (seen[seed] || weights[seed] <= 0 || deviations[seed] <= tolerances[seed]) continue;
                var queue = new Queue<int>(); queue.Enqueue(seed); seen[seed] = true;
                int worst = seed, members = 0; double area = 0;
                while (queue.TryDequeue(out int v))
                {
                    members++; area += weights[v];
                    if (deviations[v] > deviations[worst]) worst = v;
                    foreach (int next in adjacency.NeighborsOf(v))
                        if (!seen[next] && weights[next] > 0 && deviations[next] > tolerances[next] && secondary[next] == secondary[seed] &&
                            !(signed[next] * signed[seed] < 0))
                        { seen[next] = true; queue.Enqueue(next); }
                }
                misses.Add(new MissRegion(vertices[worst * 3], vertices[worst * 3 + 1], vertices[worst * 3 + 2], deviations[worst],
                    signed[worst], area, members, secondary[seed]));
            }
            return misses.OrderByDescending(m => m.Deviation).ToList();
        }

        Result Failed() => Unsupported("The recovered curves could not be triangulated.");
    }

    private static double ChainLength(double[] points, bool closed)
    {
        double length = PlanLength(points);
        if (closed && points.Length >= 6)
            length += Math.Sqrt(Sq(points[0] - points[^3]) + Sq(points[1] - points[^2]));
        return length;
    }
}
