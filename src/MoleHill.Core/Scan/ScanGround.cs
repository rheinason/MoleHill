using MoleHill.Core.Engine;

namespace MoleHill.Core.Scan;

/// <summary>
/// The bare ground under a scan (docs/scan-to-terrain.md): rasterise the top
/// surface, find what stands on the ground (houses, cars, trees, bushes), take it away and rebuild the
/// ground beneath it smoothly from the ground around.
/// <para><b>Filter.</b> The progressive morphological filter (Zhang et al. 2003): open the surface with
/// windows doubling from <see cref="Options.SmallestObject"/> to <see cref="Options.LargestObject"/>; a
/// node the opening lowers by more than the window's height allowance stands on the ground. The
/// allowance grows with the window by <see cref="Options.GroundSlope"/>, since an opening on sloping
/// ground lowers it by about the slope times the window's half-width, and is capped at
/// <see cref="Options.MaximumThreshold"/>.</para>
/// <para><b>Objects, not steps.</b> On terraced ground the opening also lowers the top of a terrace
/// narrower than the window: it is "raised" only along its wall and runs on into the hill behind. What
/// stands on the ground is raised all round, so each raised region is kept as an object only when it
/// stands above its surroundings along at least <see cref="Options.RaisedPerimeterShare"/> of its edge;
/// the rest is ground, and its wall is left for the wall tracing of phase 2.</para>
/// <para><b>Fill.</b> Objects grow by <see cref="Options.Margin"/> (the foot of a wall rasterises halfway
/// up it), then everything but ground is filled by <see cref="HarmonicFill"/> over the scan's footprint:
/// its largest connected piece, with the gaps inside it closed.</para>
/// </summary>
public static class ScanGround
{
    public sealed class Options
    {
        /// <summary>Raster cell; 0 picks <see cref="ScanRasterizer.AutomaticCellSize"/>.</summary>
        public double CellSize { get; init; }

        /// <summary>The narrowest object the filter's first window removes (its full width).</summary>
        public required double SmallestObject { get; init; }

        /// <summary>The widest object to remove, full width: the last window. Wider than the largest building.</summary>
        public required double LargestObject { get; init; }

        /// <summary>The steepest ground, rise over run, the height allowance must not mistake for an object.</summary>
        public double GroundSlope { get; init; } = 0.6;

        /// <summary>Height allowance of the first window: what may stand proud of the ground and still be ground.</summary>
        public required double InitialThreshold { get; init; }

        /// <summary>The allowance never grows past this.</summary>
        public required double MaximumThreshold { get; init; }

        /// <summary>An object is kept only if it stands above its surroundings along this share of its edge.</summary>
        public double RaisedPerimeterShare { get; init; } = 0.5;

        /// <summary>
        /// The share a roof-smooth region needs instead (see <see cref="SmoothTopRoughness"/>). A shed built against a
        /// road has its roof flush with the road along one side: on Varðin a 270 m2 shed stood 6 m over the shore yet
        /// up along only 49.7% of its edge. Rock and trees in the same band are rough, and keep the stricter share.
        /// </summary>
        public double SmoothRaisedPerimeterShare { get; init; } = 0.45;

        /// <summary>
        /// Median absolute Laplacian of the top over half-metre steps, below which a raised region reads as a roof.
        /// Measured: roofs 0.03-0.11 m, trees and bushes 0.2-1.2 m, a rock outcrop 0.6 m.
        /// </summary>
        public double SmoothTopRoughness { get; init; } = 0.15;

        /// <summary>
        /// A drop this tall inside a raised region that failed as a whole parts it, and each part is judged on its
        /// own; zero judges regions whole.
        /// </summary>
        public double PartDrop { get; init; } = 1.5;

        /// <summary>How far each object grows before the ground is filled under it.</summary>
        public required double Margin { get; init; }

        /// <summary>Objects at least this large in plan are counted in <see cref="Result.ObjectCount"/>.</summary>
        public double MinimumObjectArea { get; init; }

        public CancellationToken CancellationToken { get; init; }
    }

    public sealed class Result
    {
        /// <summary>The rasterised top surface (DSM), NaN where the scan has no face.</summary>
        public required ScanGrid Surface { get; init; }

        /// <summary>The ground: the surface where it is ground, filled elsewhere; NaN outside <see cref="Domain"/>.</summary>
        public required ScanGrid Ground { get; init; }

        /// <summary>Nodes the ground covers: the scan's largest piece, its inner gaps closed.</summary>
        public required bool[] Domain { get; init; }

        /// <summary>Nodes whose surface was taken as ground (not filled).</summary>
        public required bool[] IsGround { get; init; }

        /// <summary>Objects removed (at least <see cref="Options.MinimumObjectArea"/> in plan).</summary>
        public required int ObjectCount { get; init; }

        /// <summary>Raised regions kept as ground because they were raised along too little of their edge.</summary>
        public required int StepCount { get; init; }

        /// <summary>Plan area of the domain, and of the part of it that was filled.</summary>
        public required double DomainArea { get; init; }

        public required double FilledArea { get; init; }

        public double FilledShare => DomainArea > 0.0 ? FilledArea / DomainArea : 0.0;

        public int ClassifiedGroundNodes { get; init; }
        public int ClassifiedGroundDisagreements { get; init; }
    }

    public static Result Build(IndexedTriMesh scan, Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        double cellSize = options.CellSize > 0.0 ? options.CellSize : ScanRasterizer.AutomaticCellSize(scan);
        ScanGrid surface = ScanRasterizer.Rasterize(scan, cellSize, options.CancellationToken).Top;
        return FromSurface(surface, options);
    }

    /// <summary>The ground of an already rasterised top surface.</summary>
    public static Result FromSurface(ScanGrid surface, Options options)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        options.CancellationToken.ThrowIfCancellationRequested();
        int columns = surface.Columns, rows = surface.Rows;
        double[] top = surface.Values;
        double cell = surface.CellSize;
        var covered = new bool[top.Length];
        for (int i = 0; i < top.Length; i++)
            covered[i] = !double.IsNaN(top[i]);

        bool[] raised = FilterObjects(top, columns, rows, cell, options);
        options.CancellationToken.ThrowIfCancellationRequested();

        // Keep the regions that stand up all round; give back the terrace tops.
        int[] labels = GridMorphology.Label(raised, columns, rows, out List<int> sizes, out _);
        var raisedEdges = new int[sizes.Count];
        var edges = new int[sizes.Count];
        for (int node = 0; node < top.Length; node++)
        {
            int label = labels[node];
            if (label < 0)
                continue;
            int i = node % columns, j = node / columns;
            Compare(i > 0 ? node - 1 : -1);
            Compare(i < columns - 1 ? node + 1 : -1);
            Compare(j > 0 ? node - columns : -1);
            Compare(j < rows - 1 ? node + columns : -1);

            void Compare(int neighbour)
            {
                if (neighbour < 0 || labels[neighbour] == label || !covered[neighbour])
                    return;
                edges[label]++;
                if (top[node] - top[neighbour] > options.InitialThreshold)
                    raisedEdges[label]++;
            }
        }

        var isObject = new bool[sizes.Count];
        int objectCount = 0, stepCount = 0;
        int minimumNodes = options.MinimumObjectArea > 0.0 ? (int)Math.Ceiling(options.MinimumObjectArea / surface.NodeArea) : 0;
        HashSet<int> smoothTops = SmoothTops(top, labels, sizes.Count, columns, rows, cell, options,
            label => edges[label] > 0 && raisedEdges[label] < options.RaisedPerimeterShare * edges[label] &&
                raisedEdges[label] >= options.SmoothRaisedPerimeterShare * edges[label]);
        for (int label = 0; label < sizes.Count; label++)
        {
            isObject[label] = edges[label] == 0 || raisedEdges[label] >= options.RaisedPerimeterShare * edges[label] ||
                smoothTops.Contains(label);
            if (!isObject[label])
                stepCount++;
            else if (sizes[label] >= minimumNodes)
                objectCount++;
        }

        var objects = new bool[top.Length];
        for (int node = 0; node < top.Length; node++)
            objects[node] = labels[node] >= 0 && isObject[labels[node]];
        if (options.PartDrop > 0)
            objectCount += StandingParts(top, covered, labels, isObject, objects, columns, rows, minimumNodes, options);
        int marginNodes = Math.Max(0, (int)Math.Round(options.Margin / cell));
        objects = GridMorphology.DilateMask(objects, columns, rows, marginNodes);

        bool[] domain = GridMorphology.LargestComponentWithoutHoles(covered, columns, rows);
        var isGround = new bool[top.Length];
        for (int node = 0; node < top.Length; node++)
            isGround[node] = domain[node] && covered[node] && !objects[node];

        // A one-node sliver of ground reaching between two objects holds the fill to whatever it touched.
        isGround = GridMorphology.OpenMask(isGround, columns, rows, 1);

        // A scrap of ground smaller than the smallest object, inside or between objects (a node on a roof the
        // filter missed, a gap between a house and its hedge), is not ground to hold the fill to: a harmonic
        // fill pinned at one node makes a cone there.
        int[] islands = GridMorphology.Label(isGround, columns, rows, out List<int> islandSizes, out _);
        int smallestIsland = (int)Math.Ceiling(options.SmallestObject * options.SmallestObject / surface.NodeArea);
        int largestIsland = 0;
        for (int island = 1; island < islandSizes.Count; island++)
        {
            if (islandSizes[island] > islandSizes[largestIsland])
                largestIsland = island;
        }

        int domainNodes = 0, filledNodes = 0;
        for (int node = 0; node < top.Length; node++)
        {
            if (isGround[node] && islands[node] != largestIsland && islandSizes[islands[node]] < smallestIsland)
                isGround[node] = false;
            if (domain[node])
            {
                domainNodes++;
                if (!isGround[node])
                    filledNodes++;
            }
        }

        double[] ground = HarmonicFill.Fill(SmoothFillEdge(top, isGround, domain, columns, rows), isGround, domain, columns, rows, options.CancellationToken);
        return new Result
        {
            Surface = surface,
            Ground = surface.WithValues(ground),
            Domain = domain,
            IsGround = isGround,
            ObjectCount = objectCount,
            StepCount = stepCount,
            DomainArea = domainNodes * surface.NodeArea,
            FilledArea = filledNodes * surface.NodeArea,
        };
    }

    /// <summary>Use supplied LAS ground observations as height anchors, retaining filtered ground
    /// elsewhere. The filter is also run as an independent disagreement check, not used to discard
    /// supplied ground. IsGround still describes the observed top surface, so a roof over a ground
    /// return is not offered as evidence for a kerb or paving.</summary>
    internal static Result WithClassifiedGround(Result filtered, ScanGrid supplied, Options options)
    {
        var anchors = (bool[])filtered.IsGround.Clone();
        var heights = (double[])filtered.Ground.Values.Clone();
        var observed = (bool[])filtered.IsGround.Clone();
        int count = 0, disagreements = 0;
        for (int node = 0; node < supplied.NodeCount; node++)
        {
            if (!filtered.Domain[node] || !double.IsFinite(supplied.Values[node])) continue;
            count++;
            if (Math.Abs(supplied.Values[node] - filtered.Ground.Values[node]) > options.InitialThreshold) disagreements++;
            heights[node] = supplied.Values[node]; anchors[node] = true;
            observed[node] = Math.Abs(filtered.Surface.Values[node] - supplied.Values[node]) <= options.InitialThreshold;
        }
        if (count == 0) return filtered;
        double[] ground = HarmonicFill.Fill(heights, anchors, filtered.Domain, supplied.Columns, supplied.Rows, options.CancellationToken);
        return new Result
        {
            Surface = filtered.Surface, Ground = supplied.WithValues(ground), Domain = filtered.Domain, IsGround = observed,
            ObjectCount = filtered.ObjectCount, StepCount = filtered.StepCount, DomainArea = filtered.DomainArea,
            FilledArea = filtered.Domain.Where((inside, node) => inside && !anchors[node]).Count() * supplied.NodeArea,
            ClassifiedGroundNodes = count, ClassifiedGroundDisagreements = disagreements,
        };
    }

    /// <summary>
    /// The heights the fill is held to: at each ground node beside a node to fill, the median of the ground
    /// within <see cref="FillEdgeReach"/> nodes, elsewhere the surface. A harmonic fill follows its edge
    /// exactly, so one odd node there (the foot of a wall, a stone, a step of a stair) leaves a cone reaching
    /// into the patch; the median keeps the edge's height and drops the odd one.
    /// </summary>
    private static double[] SmoothFillEdge(double[] top, bool[] isGround, bool[] domain, int columns, int rows)
    {
        var heights = (double[])top.Clone();
        var window = new List<double>();
        for (int node = 0; node < top.Length; node++)
        {
            if (!isGround[node])
                continue;
            int i = node % columns, j = node / columns;
            bool edge = (i > 0 && domain[node - 1] && !isGround[node - 1]) ||
                (i < columns - 1 && domain[node + 1] && !isGround[node + 1]) ||
                (j > 0 && domain[node - columns] && !isGround[node - columns]) ||
                (j < rows - 1 && domain[node + columns] && !isGround[node + columns]);
            if (!edge)
                continue;

            window.Clear();
            for (int y = Math.Max(0, j - FillEdgeReach); y <= Math.Min(rows - 1, j + FillEdgeReach); y++)
            {
                for (int x = Math.Max(0, i - FillEdgeReach); x <= Math.Min(columns - 1, i + FillEdgeReach); x++)
                {
                    int other = (y * columns) + x;
                    if (isGround[other])
                        window.Add(top[other]);
                }
            }

            window.Sort();
            heights[node] = window[window.Count / 2];
        }

        return heights;
    }

    private const int FillEdgeReach = 2;

    private static void Validate(Options options)
    {
        if (!double.IsFinite(options.CellSize) || options.CellSize < 0 ||
            !double.IsFinite(options.SmallestObject) || options.SmallestObject <= 0 ||
            !double.IsFinite(options.LargestObject) || options.LargestObject < options.SmallestObject ||
            !double.IsFinite(options.InitialThreshold) || options.InitialThreshold < 0 ||
            !double.IsFinite(options.MaximumThreshold) || options.MaximumThreshold < options.InitialThreshold ||
            !double.IsFinite(options.GroundSlope) || options.GroundSlope < 0 ||
            !double.IsFinite(options.RaisedPerimeterShare) || options.RaisedPerimeterShare < 0 || options.RaisedPerimeterShare > 1 ||
            !double.IsFinite(options.SmoothRaisedPerimeterShare) || options.SmoothRaisedPerimeterShare < 0 || options.SmoothRaisedPerimeterShare > 1 ||
            !double.IsFinite(options.SmoothTopRoughness) || options.SmoothTopRoughness < 0 ||
            !double.IsFinite(options.PartDrop) || options.PartDrop < 0 ||
            !double.IsFinite(options.Margin) || options.Margin < 0 ||
            !double.IsFinite(options.MinimumObjectArea) || options.MinimumObjectArea < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Scan lengths and thresholds must be finite and nonnegative, object widths ordered and positive, and perimeter share between 0 and 1.");
    }

    /// <summary>
    /// The progressive morphological filter: true where a node stands on the ground rather than being it.
    /// Windows are half-widths in nodes, doubling from the smallest object's to the largest's.
    /// </summary>
    /// <summary>
    /// Raised regions judged steps, parted at every drop of <see cref="Options.PartDrop"/>: a part standing up along
    /// its edge (over the ground, or over the rest of its region) is an object. Judged whole, a tree or shed touching
    /// a terrace took the terrace's verdict: a raised region on a hillside runs on for hundreds of square metres, and
    /// an 8 m tree on Vardalag was kept as ground inside 574 m2 raised along a third of its edge.
    /// </summary>
    private static int StandingParts(double[] top, bool[] covered, int[] labels, bool[] isObject, bool[] objects,
        int columns, int rows, int minimumNodes, Options options)
    {
        var part = new int[top.Length];
        Array.Fill(part, -1);
        var queue = new Queue<int>();
        var members = new List<int>();
        int found = 0;
        for (int seed = 0; seed < top.Length; seed++)
        {
            if (labels[seed] < 0 || isObject[labels[seed]] || part[seed] >= 0)
                continue;
            options.CancellationToken.ThrowIfCancellationRequested();
            int label = labels[seed];
            members.Clear();
            part[seed] = seed;
            queue.Enqueue(seed);
            while (queue.TryDequeue(out int node))
            {
                members.Add(node);
                int i = node % columns, j = node / columns;
                if (i > 0) Join(node - 1);
                if (i < columns - 1) Join(node + 1);
                if (j > 0) Join(node - columns);
                if (j < rows - 1) Join(node + columns);

                void Join(int next)
                {
                    if (labels[next] == label && part[next] < 0 && Math.Abs(top[next] - top[node]) < options.PartDrop)
                    { part[next] = seed; queue.Enqueue(next); }
                }
            }
            int edges = 0, raisedEdges = 0;
            foreach (int node in members)
            {
                int i = node % columns, j = node / columns;
                if (i > 0) Compare(node - 1);
                if (i < columns - 1) Compare(node + 1);
                if (j > 0) Compare(node - columns);
                if (j < rows - 1) Compare(node + columns);

                void Compare(int next)
                {
                    if (part[next] == seed || !covered[next])
                        return;
                    edges++;
                    if (top[node] - top[next] > options.InitialThreshold)
                        raisedEdges++;
                }
            }
            if (edges == 0 || raisedEdges < options.RaisedPerimeterShare * edges)
                continue;
            foreach (int node in members)
                objects[node] = true;
            if (members.Count >= minimumNodes) found++;
        }
        return found;
    }

    /// <summary>
    /// The regions <paramref name="consider"/> picks whose top is roof-smooth: the median absolute Laplacian over
    /// steps of about half a metre, at nodes whose four step neighbours lie in the same region, is at most
    /// <see cref="Options.SmoothTopRoughness"/>. A region with too few such nodes is not judged smooth.
    /// </summary>
    private static HashSet<int> SmoothTops(double[] top, int[] labels, int labelCount, int columns, int rows, double cell,
        Options options, Func<int, bool> consider)
    {
        var smooth = new HashSet<int>();
        if (options.SmoothTopRoughness <= 0 || options.SmoothRaisedPerimeterShare >= options.RaisedPerimeterShare)
            return smooth;
        var samples = new Dictionary<int, List<double>>();
        for (int label = 0; label < labelCount; label++)
            if (consider(label))
                samples[label] = new List<double>();
        if (samples.Count == 0)
            return smooth;
        int k = Math.Max(1, (int)Math.Round(0.5 / cell));
        for (int j = k; j < rows - k; j++)
            for (int i = k; i < columns - k; i++)
            {
                int node = j * columns + i, label = labels[node];
                if (label < 0 || !samples.TryGetValue(label, out List<double>? list))
                    continue;
                int w = node - k, e = node + k, s = node - k * columns, n = node + k * columns;
                if (labels[w] != label || labels[e] != label || labels[s] != label || labels[n] != label)
                    continue;
                double laplacian = 4 * top[node] - top[w] - top[e] - top[s] - top[n];
                if (double.IsFinite(laplacian))
                    list.Add(Math.Abs(laplacian));
            }
        foreach ((int label, List<double> list) in samples)
        {
            if (list.Count < 10)
                continue;
            list.Sort();
            if (list[list.Count / 2] <= options.SmoothTopRoughness)
                smooth.Add(label);
        }
        return smooth;
    }

    private static bool[] FilterObjects(double[] top, int columns, int rows, double cell, Options options)
    {
        var halfWidths = new List<double>();
        double half = Math.Max(options.SmallestObject * 0.5, cell);
        double last = Math.Max(options.LargestObject * 0.5, half);
        while (half < last)
        {
            halfWidths.Add(half);
            half *= 2.0;
        }

        halfWidths.Add(last);
        var raised = new bool[top.Length];
        double[] surface = top;
        double previous = 0.0;
        foreach (double halfWidth in halfWidths)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            int k = (int)Math.Clamp(Math.Round(halfWidth / cell), 1, Math.Max(1, Math.Max(columns, rows)));
            double[] opened = GridMorphology.Open(surface, columns, rows, k);
            double threshold = Math.Min(options.InitialThreshold + (options.GroundSlope * (halfWidth - previous)), options.MaximumThreshold);
            for (int node = 0; node < top.Length; node++)
            {
                if (!double.IsNaN(surface[node]) && surface[node] - opened[node] > threshold)
                    raised[node] = true;
            }

            surface = opened;
            previous = halfWidth;
        }

        return raised;
    }
}
