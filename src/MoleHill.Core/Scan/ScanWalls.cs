using MoleHill.Core.Processing;
using MoleHill.Core.Routing;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Scan;

/// <summary>
/// Traces persistent steps in inferred ground as paired measured top/toe breaklines. Local slopes on
/// both sides are subtracted before testing a rise, so a steep plane is not mistaken for a wall.
/// </summary>
public static class ScanWalls
{
    public sealed record Options
    {
        public required double MinimumHeight { get; init; }
        public required double MinimumLength { get; init; }
        public required double PlanTolerance { get; init; }
        public required double VerticalTolerance { get; init; }
        /// <summary>Keep rails away from a coarser contouring mesh's truncated outer rim.</summary>
        public double BorderMargin { get; init; }
        /// <summary>Largest plan width over which a step may be smeared; zero uses four raster cells.</summary>
        public double MaximumWidth { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public Action<string>? ReportRejected { get; init; }
    }

    public sealed class Wall
    {
        public required ScanCurveGeometry TopCurve { get; init; }
        public required ScanCurveGeometry ToeCurve { get; init; }
        /// <summary>Light polylines of the fitted output curves, used as fixed TIN edges.</summary>
        public required double[] Top { get; init; }
        public required double[] Toe { get; init; }
        public required double Length { get; init; }
        public required double MeanHeight { get; init; }
        public double MinimumHeight { get; init; }
        public double MaximumHeight { get; init; }
    }

    private readonly record struct Crossing(int Start, int End, int Top, int Toe, double Height);

    public static IReadOnlyList<Wall> Trace(ScanGround.Result ground, Options options, IndexedTriMesh? contouringMesh = null) =>
        Trace(ground.Ground, options, contouringMesh, ground.IsGround);

    public static IReadOnlyList<Wall> Trace(ScanGrid ground, Options options, IndexedTriMesh? contouringMesh = null) =>
        Trace(ground, options, contouringMesh, null);

    private static IReadOnlyList<Wall> Trace(ScanGrid ground, Options options, IndexedTriMesh? contouringMesh, bool[]? observed)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(options);
        if (!Positive(options.MinimumHeight) || !Positive(options.MinimumLength) ||
            !Positive(options.PlanTolerance) || !Positive(options.VerticalTolerance) ||
            !double.IsFinite(options.BorderMargin) || options.BorderMargin < 0 ||
            !double.IsFinite(options.MaximumWidth) || options.MaximumWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Wall dimensions and tolerances must be finite and positive.");
        options.CancellationToken.ThrowIfCancellationRequested();
        MeshHeightProjector? footprint = contouringMesh is IndexedTriMesh mesh
            ? new(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount) : null;
        int columns = ground.Columns, rows = ground.Rows;
        int band = options.MaximumWidth > 0
            ? (int)Math.Clamp(Math.Ceiling(options.MaximumWidth / ground.CellSize), 1, Math.Max(1, Math.Max(columns, rows))) : 4;
        int margin = (int)Math.Clamp(Math.Ceiling(options.BorderMargin / ground.CellSize), band + 2, Math.Max(band + 2, Math.Max(columns, rows)));
        var crossings = new List<Crossing>();
        int cornerColumns = columns + 1;
        for (int row = margin; row < rows - margin - 1; row++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            for (int column = margin; column < columns - margin - 1; column++)
            {
                int a = row * columns + column;
                // The directed dual edge has its high ground on the left. This makes stair-stepped
                // diagonal walls chain deterministically without joining unrelated crossing branches.
                Add(a, a + 1, 1, (row + 1) * cornerColumns + column + 1, row * cornerColumns + column + 1);
                Add(a, a + columns, columns, (row + 1) * cornerColumns + column, (row + 1) * cornerColumns + column + 1);
            }
        }

        var outgoing = new Dictionary<int, List<int>>();
        var incoming = new Dictionary<int, int>();
        for (int i = 0; i < crossings.Count; i++)
        {
            Crossing edge = crossings[i];
            if (!outgoing.TryGetValue(edge.Start, out List<int>? list))
                outgoing[edge.Start] = list = new List<int>();
            list.Add(i);
            incoming[edge.End] = incoming.GetValueOrDefault(edge.End) + 1;
        }

        var used = new bool[crossings.Count];
        var chains = new List<List<Crossing>>();
        for (int i = 0; i < crossings.Count; i++)
        {
            if (incoming.GetValueOrDefault(crossings[i].Start) != 1 || outgoing[crossings[i].Start].Count != 1)
                Follow(i);
        }
        for (int i = 0; i < crossings.Count; i++)
            Follow(i); // Closed chains have no endpoint.
        // Fitting dominates (tens of milliseconds a chain) and every chain fits alone; rejections are reported
        // afterwards, in chain order, because the callback counts.
        List<List<Crossing>> linked = LinkAcrossBand(chains, cornerColumns, band);
        var fitted = new Wall?[linked.Count];
        var rejected = new List<string>?[linked.Count];
        Parallel.For(0, linked.Count, new ParallelOptions { CancellationToken = options.CancellationToken }, i =>
        {
            List<string>? reasons = null;
            fitted[i] = Fit(linked[i], options.ReportRejected == null ? null : reason => (reasons ??= new()).Add(reason));
            rejected[i] = reasons;
        });
        var result = new List<Wall>();
        for (int i = 0; i < linked.Count; i++)
        {
            foreach (string reason in rejected[i] ?? Enumerable.Empty<string>())
                options.ReportRejected!(reason);
            if (fitted[i] is Wall wall)
                result.Add(wall);
        }
        return WithoutRepeats(result, band * ground.CellSize, options.MinimumHeight * 0.5);

        void Add(int a, int b, int stride, int start, int end)
        {
            double za = ground.Values[a], zb = ground.Values[b];
            if (observed != null && (!observed[a] || !observed[b])) return;
            // Estimate the trend beyond the whole allowed transition, not within a partly rasterised
            // wall. Then follow its adjacent rises as one band, keeping the actual outer top/toe nodes.
            double before = ground.Values[a - band * stride], before2 = ground.Values[a - (band + 1) * stride];
            double after = ground.Values[b + band * stride], after2 = ground.Values[b + (band + 1) * stride];
            if (!double.IsFinite(za + zb + before + before2 + after + after2))
                return;
            double slope = (before - before2 + after2 - after) * 0.5;
            double first = zb - za - slope;
            double noise = options.MinimumHeight * 0.25;
            if (Math.Abs(first) < noise)
                return;
            double previous = za - ground.Values[a - stride] - slope;
            if (previous * first > 0 && Math.Abs(previous) >= noise)
                return; // An earlier crossing owns this same transition band.
            int last = b, width = 1;
            while (width < band)
            {
                double next = ground.Values[last + stride] - ground.Values[last] - slope;
                if (!double.IsFinite(next) || next * first <= 0 || Math.Abs(next) < noise)
                    break;
                last += stride;
                width++;
            }
            double following = ground.Values[last + stride] - ground.Values[last] - slope;
            if (width == band && following * first > 0 && Math.Abs(following) >= noise)
                return; // A broader ramp is not a measured wall.
            zb = ground.Values[last];
            if (observed != null && !observed[last]) return;
            double residual = zb - za - slope * width;
            if (Math.Abs(residual) < options.MinimumHeight || residual * (zb - za) <= 0)
                return;
            crossings.Add(residual > 0 ? new(start, end, last, a, Math.Abs(residual)) : new(end, start, a, last, Math.Abs(residual)));
        }

        void Follow(int first)
        {
            if (used[first])
                return;
            options.CancellationToken.ThrowIfCancellationRequested();
            var chain = new List<Crossing>();
            int current = first;
            while (!used[current])
            {
                used[current] = true;
                Crossing edge = crossings[current];
                chain.Add(edge);
                if (incoming.GetValueOrDefault(edge.End) != 1 || !outgoing.TryGetValue(edge.End, out List<int>? next) || next.Count != 1)
                    break;
                current = next[0];
            }
            chains.Add(chain);
        }

        Wall? Fit(List<Crossing> chain, Action<string>? reportRejected)
        {
            double length = chain.Count * ground.CellSize;
            if (length < options.MinimumLength || chain.Count < 2)
                return null;
            var top = new List<double>();
            var toe = new List<double>();
            foreach (Crossing edge in chain)
            {
                Append(top, edge.Top);
                Append(toe, edge.Toe);
            }
            bool closed = chain[^1].End == chain[0].Start;
            if (closed)
            {
                top.AddRange(top.Take(3).ToArray());
                toe.AddRange(toe.Take(3).ToArray());
            }
            if (top.Count < 6 || toe.Count < 6)
                return null;
            double plan = options.PlanTolerance;
            if (!ScanAlignmentFitter.FitPair(top.ToArray(), toe.ToArray(), closed, ground.CellSize,
                plan, options.VerticalTolerance, options.CancellationToken, out var topCurve, out var toeCurve, reportRejected))
                return null;
            if (!Inside(topCurve.PointsXyz) || !Inside(toeCurve.PointsXyz))
                return null;
            if (Folded(topCurve.PointsXyz) || Folded(toeCurve.PointsXyz))
            {
                reportRejected?.Invoke("Wall rail folds: it climbs in place where a joined chain turned back.");
                return null;
            }
            length = (PlanLength(topCurve.PointsXyz) + PlanLength(toeCurve.PointsXyz)) * 0.5;
            if (length < options.MinimumLength)
                return null;
            return new Wall
            {
                TopCurve = topCurve,
                ToeCurve = toeCurve,
                Top = TerrainCurveRecovery.SimplifyOpen(topCurve.PointsXyz, plan * 0.1, options.VerticalTolerance * 0.1),
                Toe = TerrainCurveRecovery.SimplifyOpen(toeCurve.PointsXyz, plan * 0.1, options.VerticalTolerance * 0.1),
                Length = length,
                MeanHeight = chain.Average(edge => edge.Height),
                MinimumHeight = chain.Min(edge => edge.Height),
                MaximumHeight = chain.Max(edge => edge.Height),
            };
        }

        // A rail that changes height without moving in plan: the rebuild merges its coincident vertices and keeps one
        // height (Varðin: a toe stood 0.64 m tall in one spot where a joint turned back, and paving missed by 0.66 m).
        bool Folded(double[] points)
        {
            double still = ground.CellSize * 0.05;
            for (int start = 0; start < points.Length; start += 3)
                for (int i = start + 3; i < points.Length; i += 3)
                {
                    double dx = points[i] - points[start], dy = points[i + 1] - points[start + 1];
                    if (dx * dx + dy * dy > still * still)
                        break;
                    if (Math.Abs(points[i + 2] - points[start + 2]) > options.VerticalTolerance)
                        return true;
                }
            return false;
        }

        bool Inside(double[] points)
        {
            if (footprint == null)
                return true;
            for (int i = 0; i < points.Length; i += 3)
                if (!footprint.TryGetHeightExtremes(points[i], points[i + 1], ground.CellSize * 1e-6, out _, out _))
                    return false;
            return true;
        }

        void Append(List<double> points, int node)
        {
            double x = ground.NodeX(node % columns), y = ground.NodeY(node / columns);
            points.Add(x);
            points.Add(y);
            points.Add(ground.Values[node]);
        }
    }

    /// <summary>
    /// Joins chains that are one step seen twice. A row scan and a column scan each keep the first crossing of a
    /// smeared step, and on a diagonal wall the two land up to a band apart, so the dual-edge chain broke every
    /// cell or two: on Vardin every quay and road-edge wall fell apart into pieces under a metre while
    /// axis-aligned walls traced whole. A chain's end is joined to the nearest chain start within the band
    /// ahead of it, running the same way (high ground on the same side) at a comparable height.
    /// </summary>
    /// <summary>Cosine of the sharpest turn between joined chains (60 degrees). A 45-degree wall's pieces each run
    /// along a raster axis, so anything stricter rejects the joints this exists for (Varðin: 19 walls to 11).</summary>
    private const double TurnCosine = 0.5;

    private static List<List<Crossing>> LinkAcrossBand(List<List<Crossing>> chains, int cornerColumns, int band)
    {
        int count = chains.Count;
        var byStart = new Dictionary<long, List<int>>(IndexedMeshTools.CellKeyComparer.Instance);
        (long X, long Y) Cell(int corner) => (corner % cornerColumns / band, corner / cornerColumns / band);
        long Key(long x, long y) => (x << 32) | (uint)y;
        bool Closed(List<Crossing> chain) => chain[^1].End == chain[0].Start;
        for (int i = 0; i < count; i++)
        {
            if (Closed(chains[i]))
                continue;
            (long x, long y) = Cell(chains[i][0].Start);
            if (!byStart.TryGetValue(Key(x, y), out List<int>? list))
                byStart[Key(x, y)] = list = new List<int>();
            list.Add(i);
        }

        (double X, double Y) At(int corner) => (corner % cornerColumns, corner / cornerColumns);
        (double X, double Y) Heading(List<Crossing> chain, bool atEnd)
        {
            int k = Math.Min(chain.Count, band + 1);
            (double X, double Y) a = atEnd ? At(chain[^k].Start) : At(chain[0].Start);
            (double X, double Y) b = atEnd ? At(chain[^1].End) : At(chain[k - 1].End);
            double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
            return length > 0 ? (dx / length, dy / length) : (0, 0);
        }

        var next = new int[count];
        var hasPrevious = new bool[count];
        Array.Fill(next, -1);
        var root = Enumerable.Range(0, count).ToArray();
        int Find(int i)
        {
            while (root[i] != i)
                i = root[i] = root[root[i]];
            return i;
        }
        var heights = chains.Select(chain => chain.Average(edge => edge.Height)).ToArray();
        for (int i = 0; i < count; i++)
        {
            List<Crossing> chain = chains[i];
            if (Closed(chain))
                continue;
            (double X, double Y) end = At(chain[^1].End);
            (double X, double Y) heading = Heading(chain, atEnd: true);
            int best = -1;
            double bestDistance = double.PositiveInfinity;
            (long hx, long hy) = Cell(chain[^1].End);
            for (long cx = hx - 1; cx <= hx + 1; cx++)
                for (long cy = hy - 1; cy <= hy + 1; cy++)
                {
                    if (!byStart.TryGetValue(Key(cx, cy), out List<int>? candidates))
                        continue;
                    foreach (int j in candidates)
                    {
                        if (j == i || hasPrevious[j] || Find(i) == Find(j))
                            continue;
                        (double X, double Y) start = At(chains[j][0].Start);
                        double gx = start.X - end.X, gy = start.Y - end.Y;
                        double distance = Math.Max(Math.Abs(gx), Math.Abs(gy));
                        if (distance > band || distance >= bestDistance)
                            continue;
                        // Ahead of the end, never behind it: a joint reaching back folded the rails into a cusp on
                        // Vardalag, where the rebuilt terrain read 0.9 m off at its tip.
                        if (gx * heading.X + gy * heading.Y < 0)
                            continue;
                        (double X, double Y) onward = Heading(chains[j], atEnd: false);
                        if (onward.X * heading.X + onward.Y * heading.Y < TurnCosine)
                            continue;
                        if (heights[j] > heights[i] * 2 || heights[i] > heights[j] * 2)
                            continue;
                        best = j;
                        bestDistance = distance;
                    }
                }
            if (best < 0)
                continue;
            next[i] = best;
            hasPrevious[best] = true;
            root[Find(best)] = Find(i);
        }

        var linked = new List<List<Crossing>>();
        for (int i = 0; i < count; i++)
        {
            if (hasPrevious[i])
                continue;
            var merged = new List<Crossing>(chains[i]);
            for (int j = next[i]; j >= 0; j = next[j])
                merged.AddRange(chains[j]);
            linked.Add(merged);
        }
        return linked;
    }

    /// <summary>
    /// Drops a wall whose top rail mostly runs within <paramref name="reach"/> in plan and <paramref name="rise"/> in
    /// height of a longer wall's: once row- and
    /// column-scan pieces are joined, each set can complete the same step on its own (Vardalag: 15 such pairs in
    /// 202 walls), and two copies of one wall are two fixed breaklines fighting over it. Height matters: stacked
    /// terrace walls run that close in plan, and dropping one cost Varðin its paved accuracy.
    /// </summary>
    private static IReadOnlyList<Wall> WithoutRepeats(List<Wall> walls, double reach, double rise)
    {
        var kept = new bool[walls.Count];
        var cells = new Dictionary<long, List<(double X, double Y, double Z)>>(IndexedMeshTools.CellKeyComparer.Instance);
        long Key(double x, double y) => ((long)Math.Floor(x / reach) << 32) | (uint)(int)Math.Floor(y / reach);
        foreach (int i in Enumerable.Range(0, walls.Count).OrderByDescending(i => walls[i].Length))
        {
            double[] top = walls[i].Top;
            int near = 0, count = top.Length / 3;
            for (int k = 0; k < top.Length; k += 3)
                if (Near(top[k], top[k + 1], top[k + 2]))
                    near++;
            if (near * 2 > count)
                continue;
            kept[i] = true;
            // Densify so a sparse simplified rail still covers its whole length.
            for (int k = 3; k < top.Length; k += 3)
            {
                double dx = top[k] - top[k - 3], dy = top[k + 1] - top[k - 2], dz = top[k + 2] - top[k - 1];
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / (reach * 0.5)));
                for (int step = 0; step <= steps; step++)
                {
                    double x = top[k - 3] + dx * step / steps, y = top[k - 2] + dy * step / steps, z = top[k - 1] + dz * step / steps;
                    long key = Key(x, y);
                    if (!cells.TryGetValue(key, out List<(double X, double Y, double Z)>? list))
                        cells[key] = list = new List<(double X, double Y, double Z)>();
                    list.Add((x, y, z));
                }
            }
        }
        return walls.Where((_, i) => kept[i]).ToList();

        bool Near(double x, double y, double z)
        {
            long cx = (long)Math.Floor(x / reach), cy = (long)Math.Floor(y / reach);
            for (long i = cx - 1; i <= cx + 1; i++)
                for (long j = cy - 1; j <= cy + 1; j++)
                    if (cells.TryGetValue((i << 32) | (uint)(int)j, out List<(double X, double Y, double Z)>? list))
                        foreach ((double px, double py, double pz) in list)
                            if ((px - x) * (px - x) + (py - y) * (py - y) <= reach * reach && Math.Abs(pz - z) <= rise)
                                return true;
            return false;
        }
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static double PlanLength(double[] points)
    {
        double length = 0;
        for (int i = 3; i < points.Length; i += 3)
        {
            double dx = points[i] - points[i - 3], dy = points[i + 1] - points[i - 2];
            length += Math.Sqrt(dx * dx + dy * dy);
        }
        return length;
    }
}
