using MoleHill.Core.Analysis;
using MoleHill.Core.Geometry;
using MoleHill.Core.Processing;

namespace MoleHill.Core.Scan;

/// <summary>Fits each shared class interface once, then assembles closed family loops from it.</summary>
internal static class ScanSurfaceGraph
{
    private sealed record Interface(ScanGridEdges.Edge Edge, ScanSurfaceKind Left, ScanSurfaceKind Right);
    private sealed record Chain(List<ScanGridEdges.Edge> Edges, double[] Points, bool Closed, double Tolerance, ScanCurveGeometry Geometry);

    /// <summary>
    /// Traces the class masks on <paramref name="lattice"/>, which may be finer than <paramref name="grid"/>;
    /// heights always come from the clean reference or the ground grid. <paramref name="softRegions"/>, when given,
    /// splits soft ground into separately outlined areas (see <see cref="ScanSoftNecks"/>).
    /// </summary>
    public static IReadOnlyList<ScanSurfaceAreas.Boundary> Build(ScanGrid grid, ScanColourLattice lattice, bool[] paved, bool[] soft,
        ScanSurfaceAreas.Options options, MeshHeightProjector? projector, int[]? softRegions = null)
    {
        int columns = lattice.Columns, rows = lattice.Rows;
        var edges = new Dictionary<(int A, int B), Interface>();
        foreach (var kind in new[] { ScanSurfaceKind.Paved, ScanSurfaceKind.Soft })
            foreach (var loop in Trace(kind))
                foreach (var edge in loop.Edges)
                {
                    var key = Key(edge.Start, edge.End);
                    if (!edges.ContainsKey(key)) edges.Add(key, new(edge, kind, Kind(edge.Outside)));
                }
        var incident = new Dictionary<int, List<(int A, int B)>>();
        foreach (var key in edges.Keys)
        { Add(key.A, key); Add(key.B, key); }
        var used = new HashSet<(int A, int B)>();
        var chains = new List<Chain>();
        var edgeMap = new Dictionary<(int A, int B), (int Chain, int Start)>();
        // Lattice steps are below the plan tolerance; a short average turns each into a sub-node line.
        int window = Math.Max(1, lattice.Factor / 2);
        double spacing = Math.Min(grid.CellSize * .5, options.PlanTolerance * .25);
        foreach (var entry in edges)
        {
            if (used.Contains(entry.Key)) continue;
            var run = new List<ScanGridEdges.Edge>(); var current = entry.Value.Edge;
            // Walk to a junction or the canonical start of a closed homogeneous interface.
            int start = current.Start;
            var walked = new HashSet<int>();
            while (incident[start].Count == 2 && walked.Add(start))
            {
                var next = incident[start].First(k => k != Key(current.Start, current.End));
                if (!SamePair(entry.Value, edges[next])) break;
                var e = edges[next].Edge;
                current = e.End == start ? e : new(e.End, e.Start, e.Outside, e.Inside);
                start = current.Start;
            }
            int vertex = start;
            while (true)
            {
                var next = incident[vertex].FirstOrDefault(k => !used.Contains(k) && SamePair(entry.Value, edges[k]));
                if (!edges.ContainsKey(next)) break;
                var e = edges[next].Edge;
                var directed = e.Start == vertex ? e : new ScanGridEdges.Edge(e.End, e.Start, e.Outside, e.Inside);
                used.Add(next); run.Add(directed); vertex = directed.End;
                if (vertex == start || incident[vertex].Count != 2) break;
            }
            if (run.Count == 0) continue;
            // A loop touching another interface still has a held junction at its seam.
            bool closed = run[^1].End == run[0].Start && incident[run[0].Start].Count == 2;
            double[] points = Outline(run, closed);
            bool built = entry.Value.Left == ScanSurfaceKind.Paved || entry.Value.Right == ScanSurfaceKind.Paved;
            double tolerance = built ? options.BuiltEdgePlanTolerance ?? options.PlanTolerance : options.PlanTolerance;
            ScanCurveGeometry geometry = ScanOutlineFitter.Fit(points, closed, built, grid.CellSize, tolerance,
                options.VerticalTolerance, Height, options.CancellationToken);
            int id = chains.Count; chains.Add(new(run, points, closed, tolerance, geometry));
            foreach (var e in run) edgeMap[Key(e.Start, e.End)] = (id, e.Start);
        }
        // An arc or a spline's corner cut can cross a narrow notch of its own trace or a neighbouring interface.
        // Interfaces are shared, so a crossing chain falls back for every loop using it: first to straight
        // lines through its thinned trace, then to the trace itself, which never crosses.
        for (int round = 0; round < 2; round++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var crossing = PolylineCrossings.Find(chains.Select(c => (Samples(c.Geometry), false)).ToList());
            foreach (int id in crossing)
                chains[id] = chains[id] with { Geometry = ScanOutlineFitter.Lines(chains[id].Points, chains[id].Closed, round == 0, chains[id].Tolerance, options.VerticalTolerance) };
            if (crossing.Count == 0) break;
        }
        var output = new List<ScanSurfaceAreas.Boundary>();
        var drawnClosedInterfaces = new HashSet<int>();
        foreach (var kind in new[] { ScanSurfaceKind.Paved, ScanSurfaceKind.Soft })
            foreach (var loop in Trace(kind))
            {
                if (!loop.Closed || loop.Edges.Count < 4) continue;
                int offset = 0;
                for (int i = 0; i < loop.Edges.Count; i++)
                {
                    int previous = (i + loop.Edges.Count - 1) % loop.Edges.Count;
                    if (edgeMap[Key(loop.Edges[i].Start, loop.Edges[i].End)].Chain != edgeMap[Key(loop.Edges[previous].Start, loop.Edges[previous].End)].Chain)
                    { offset = i; break; }
                }
                var pieces = new List<ScanCurvePiece>(); int lastChain = -1;
                for (int k = 0; k < loop.Edges.Count; k++)
                {
                    var e = loop.Edges[(k + offset) % loop.Edges.Count]; var map = edgeMap[Key(e.Start, e.End)];
                    if (map.Chain == lastChain) continue;
                    lastChain = map.Chain;
                    var geometry = chains[map.Chain].Geometry;
                    pieces.AddRange(e.Start == map.Start ? geometry.Pieces : Reverse(geometry.Pieces));
                }
                if (pieces.Count == 0) continue;
                // Sharing the same junction samples makes joins exact, including the closed seam.
                var curve = new ScanCurveGeometry(pieces, options.PlanTolerance, options.VerticalTolerance);
                double[] points = TerrainCurveRecovery.SimplifyOpen(curve.PointsXyz, options.PlanTolerance * .05, options.VerticalTolerance * .05);
                if (points.Length >= 6) Array.Copy(points, 0, points, points.Length - 3, 3);
                // Two interfaces between the same junctions can collapse a tiny pocket to a sliver of no area.
                if (Math.Abs(Area(points)) < options.PlanTolerance * options.PlanTolerance) continue;
                bool singleInterface = loop.Edges.All(e => edgeMap[Key(e.Start, e.End)].Chain == lastChain);
                bool duplicate = singleInterface && !drawnClosedInterfaces.Add(lastChain);
                output.Add(new(kind, points, curve) { IsSharedDuplicate = duplicate });
            }
        return output;

        double[] Outline(List<ScanGridEdges.Edge> run, bool closed)
        {
            int count = run.Count + (closed ? 0 : 1);
            var xs = new double[count]; var ys = new double[count];
            for (int i = 0; i < count; i++)
                (xs[i], ys[i]) = Corner(i < run.Count ? run[i].Start : run[^1].End);
            if (count > 2 * window + 1)
            {
                var sx = (double[])xs.Clone(); var sy = (double[])ys.Clone();
                // Junction ends are shared by other interfaces and stay exactly where they are.
                for (int i = closed ? 0 : 1; i < (closed ? count : count - 1); i++)
                {
                    double x = 0, y = 0; int n = 0;
                    for (int k = -window; k <= window; k++)
                    {
                        int j = i + k;
                        if (closed) j = (j % count + count) % count;
                        else if (j < 0 || j >= count) continue;
                        x += xs[j]; y += ys[j]; n++;
                    }
                    sx[i] = x / n; sy[i] = y / n;
                }
                xs = sx; ys = sy;
            }
            var result = new List<double>();
            double carried = 0;
            Emit(xs[0], ys[0]);
            int segments = closed ? count : count - 1;
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % count;
                double dx = xs[j] - xs[i], dy = ys[j] - ys[i], length = Math.Sqrt(dx * dx + dy * dy);
                double t = spacing - carried;
                while (t < length)
                {
                    Emit(xs[i] + dx * t / length, ys[i] + dy * t / length);
                    t += spacing;
                }
                carried = length - (t - spacing);
            }
            // The final sample is the exact end (or the closing start) so shared junctions match.
            if (result.Count >= 6 && Math.Abs(result[^3] - xs[closed ? 0 : count - 1]) + Math.Abs(result[^2] - ys[closed ? 0 : count - 1]) < spacing * .25)
                result.RemoveRange(result.Count - 3, 3);
            Emit(xs[closed ? 0 : count - 1], ys[closed ? 0 : count - 1]);
            // A sample just past sparse reference coverage takes its neighbour's height.
            for (int pass = 0; pass < 2; pass++)
                for (int i = 2; i < result.Count; i += 3)
                {
                    int k = pass == 0 ? i : result.Count - 1 - (i - 2);
                    int neighbour = pass == 0 ? k - 3 : k + 3;
                    if (double.IsNaN(result[k]) && neighbour >= 2 && neighbour < result.Count) result[k] = result[neighbour];
                }
            return result.ToArray();

            void Emit(double x, double y) { result.Add(x); result.Add(y); result.Add(Height(x, y) ?? double.NaN); }
        }

        (double X, double Y) Corner(int corner)
        {
            int column = corner % (columns + 1), row = corner / (columns + 1);
            return (lattice.OriginX + Math.Clamp(column - 0.5, 0, columns - 1) * lattice.CellSize,
                lattice.OriginY + Math.Clamp(row - 0.5, 0, rows - 1) * lattice.CellSize);
        }
        double? Height(double x, double y) => projector != null && projector.TryGetHeightExtremes(x, y, grid.CellSize * 1e-7, out _, out double z)
            ? z : ScanGridSampling.TryHeight(grid, Math.Clamp(x, grid.OriginX, grid.NodeX(grid.Columns - 1)), Math.Clamp(y, grid.OriginY, grid.NodeY(grid.Rows - 1)), out z) ? z : null;
        List<ScanGridEdges.Chain> Trace(ScanSurfaceKind kind) => kind == ScanSurfaceKind.Paved
            ? ScanGridEdges.Trace(paved, columns, rows, cancellationToken: options.CancellationToken)
            : ScanGridEdges.Trace(soft, columns, rows, regions: softRegions, cancellationToken: options.CancellationToken);
        ScanSurfaceKind Kind(int node) => node < 0 ? ScanSurfaceKind.Unknown : paved[node] ? ScanSurfaceKind.Paved : soft[node] ? ScanSurfaceKind.Soft : ScanSurfaceKind.Unknown;
        void Add(int vertex, (int A, int B) key) { if (!incident.TryGetValue(vertex, out var list)) incident[vertex] = list = new(); list.Add(key); }
    }
    internal static IEnumerable<ScanCurvePiece> Reverse(IReadOnlyList<ScanCurvePiece> pieces) => pieces.Reverse().Select(p =>
        new ScanCurvePiece(p.Degree, p.Knots.AsEnumerable().Reverse().Select(k => 1 - k).ToArray(),
            Enumerable.Range(0, p.ControlPointCount).Reverse().SelectMany(i => p.ControlPoints.Skip(i * 3).Take(3)).ToArray(), p.Weights.AsEnumerable().Reverse().ToArray()));
    /// <summary>Uniform samples per knot span, finer than the drawing samples: a cusp crosses within one of those chords.</summary>
    private static double[] Samples(ScanCurveGeometry geometry)
    {
        var points = new List<double>();
        foreach (var piece in geometry.Pieces)
        {
            double[] knots = piece.Knots.Distinct().OrderBy(k => k).ToArray();
            for (int span = 0; span + 1 < knots.Length; span++)
                for (int i = points.Count == 0 && span == 0 ? 0 : 1; i <= 32; i++)
                {
                    var q = piece.PointAt(knots[span] + (knots[span + 1] - knots[span]) * i / 32);
                    points.Add(q.X); points.Add(q.Y); points.Add(q.Z);
                }
        }
        return points.ToArray();
    }

    private static double Area(double[] xyz)
    {
        double area = 0;
        for (int i = 0, n = xyz.Length / 3; i < n; i++)
        {
            int j = (i + 1) % n;
            area += xyz[i * 3] * xyz[j * 3 + 1] - xyz[j * 3] * xyz[i * 3 + 1];
        }
        return area * .5;
    }
    private static (int A, int B) Key(int a, int b) => a < b ? (a, b) : (b, a);
    private static bool SamePair(Interface a, Interface b) => a.Left == b.Left && a.Right == b.Right || a.Left == b.Right && a.Right == b.Left;
}
