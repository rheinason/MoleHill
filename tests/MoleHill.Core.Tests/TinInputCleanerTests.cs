using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class TinInputCleanerTests
{
    [Fact]
    public void Clean_RemovesDegenerateAndDuplicateSegments()
    {
        var input = CreateMergedData(
            new[]
            {
                (0.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (1.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline)
            },
            new[] { 0, 1, 1, 0, 1, 1, 1, 2 });

        var cleaned = TinInputCleaner.Clean(input, 0.01);

        Assert.Equal(1, cleaned.DuplicateSegmentsRemoved);
        Assert.Equal(1, cleaned.DegenerateSegmentsRemoved);
        Assert.Equal(1, cleaned.CollinearVerticesCollapsed);
        Assert.Equal(1, cleaned.SegmentCount);
        Assert.Equal(2, cleaned.VertexCount);
    }

    [Fact]
    public void Clean_CollapsesCollinearBreaklineVertex_ButKeepsSpotVertex()
    {
        var input = CreateMergedData(
            new[]
            {
                (0.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (1.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline | PointCloudProcessor.VertexSource.Spot),
                (2.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline)
            },
            new[] { 0, 1, 1, 2 });

        var cleaned = TinInputCleaner.Clean(input, 0.01);

        Assert.Equal(1, cleaned.CollinearVerticesCollapsed);
        Assert.Equal(1, cleaned.SegmentCount);
        Assert.Equal(3, cleaned.VertexCount);
        Assert.Contains(cleaned.Sources, source => (source & PointCloudProcessor.VertexSource.Spot) != 0);
    }

    [Fact]
    public void Clean_SplitsCompatibleCrossingSegments()
    {
        var input = CreateMergedData(
            new[]
            {
                (0.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 2.0, 2.0, PointCloudProcessor.VertexSource.Breakline),
                (0.0, 2.0, 2.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline)
            },
            new[] { 0, 1, 2, 3 });

        var cleaned = TinInputCleaner.Clean(input, 0.01);

        Assert.Equal(1, cleaned.IntersectionsSplit);
        Assert.Equal(5, cleaned.VertexCount);
        Assert.Equal(4, cleaned.SegmentCount);
        Assert.Contains(Enumerable.Range(0, cleaned.VertexCount), i =>
            Math.Abs(cleaned.XyCoords[i * 2] - 1.0) < 1e-9 &&
            Math.Abs(cleaned.XyCoords[i * 2 + 1] - 1.0) < 1e-9 &&
            Math.Abs(cleaned.ZValues[i] - 1.0) < 1e-6);
    }

    [Fact]
    public void Clean_DoesNotSplitConflictingCrossingSegments()
    {
        var input = CreateMergedData(
            new[]
            {
                (0.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 2.0, 2.0, PointCloudProcessor.VertexSource.Breakline),
                (0.0, 2.0, 10.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 0.0, 10.0, PointCloudProcessor.VertexSource.Breakline)
            },
            new[] { 0, 1, 2, 3 });

        var cleaned = TinInputCleaner.Clean(input, 0.01);

        Assert.Equal(0, cleaned.IntersectionsSplit);
        Assert.Equal(1, cleaned.IntersectionConflictsDetected);
        Assert.Equal(4, cleaned.VertexCount);
        Assert.Equal(2, cleaned.SegmentCount);
    }

    [Fact]
    public void Clean_RandomChains_MatchesRescanFromZeroReference()
    {
        // Chain simplification must collapse in exactly the order the original algorithm did (always the
        // lowest-index qualifying vertex, rescanning from zero after each collapse), because the order
        // decides which vertices survive and how the replacement segments are oriented — and so the
        // triangulation. The reference below is that original algorithm.
        int collapsed = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            var (input, tolerance) = CreateRandomChains(seed);

            var cleaned = TinInputCleaner.Clean(input, tolerance);
            var (xy, z, segments) = ReferenceSimplify(input, tolerance);

            Assert.True(cleaned.IntersectionsSplit == 0 && cleaned.IntersectionConflictsDetected == 0, $"seed {seed}: the data must not cross");
            Assert.True(xy.SequenceEqual(cleaned.XyCoords), $"seed {seed}: vertices differ");
            Assert.Equal(xy, cleaned.XyCoords);
            Assert.Equal(z, cleaned.ZValues);
            Assert.Equal(segments, cleaned.Segments);
            collapsed += cleaned.CollinearVerticesCollapsed;
        }

        Assert.True(collapsed > 100, $"only {collapsed} collapses: the data no longer exercises the order");
    }

    [Fact]
    public void Clean_LongCollinearChain_CollapsesToItsEnds()
    {
        // 50,000 collinear breakline vertices: one collapse per vertex. The old rescan-from-zero loop was
        // quadratic in this (7,262 collapses took ~6 s on a real terrain); this must stay near-linear.
        const int count = 50_000;
        var vertices = new (double, double, double, PointCloudProcessor.VertexSource)[count];
        var segments = new int[(count - 1) * 2];
        for (int i = 0; i < count; i++)
            vertices[i] = (i * 0.1, 0.0, i * 0.01, PointCloudProcessor.VertexSource.Breakline);
        for (int i = 0; i + 1 < count; i++)
        {
            segments[i * 2] = i;
            segments[i * 2 + 1] = i + 1;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cleaned = TinInputCleaner.Clean(CreateMergedData(vertices, segments), 0.01);
        watch.Stop();

        Assert.Equal(count - 2, cleaned.CollinearVerticesCollapsed);
        Assert.Equal(2, cleaned.VertexCount);
        Assert.Equal(1, cleaned.SegmentCount);
        Assert.True(watch.Elapsed.TotalSeconds < 5, $"Clean took {watch.Elapsed.TotalSeconds:0.00} s");
    }

    /// <summary>Non-crossing rows of breakline chains (some closed, some with a short branch), with
    /// collinear runs, kinks and Z bumps, and vertex indices shuffled so the collapse order differs
    /// from chain order. Nothing crosses or near-touches, so intersection splitting has nothing to do
    /// (spikes are left to the focused tests above: a near-duplicate point also near-touches the
    /// segment before it).</summary>
    private static (PointCloudProcessor.MergedData Input, double Tolerance) CreateRandomChains(int seed)
    {
        var random = new Random(seed);
        var points = new List<(double X, double Y, double Z)>();
        var edges = new List<(int A, int B)>();
        for (int row = 0; row < 6; row++)
        {
            double y0 = row * 20.0;
            int length = random.Next(20, 60);
            int first = points.Count;
            for (int i = 0; i < length; i++)
            {
                double y = y0 + (random.NextDouble() < 0.2 ? 0.5 : 0.0);
                double z = i * 0.05 + (random.NextDouble() < 0.1 ? 0.4 : 0.0);
                double x = i * 0.3;
                points.Add((x, y, z));
                if (i > 0)
                    edges.Add((points.Count - 2, points.Count - 1));
            }

            if (random.NextDouble() < 0.3)
            {
                // Close the row into a loop: straight up from its last point, across, and down to its
                // first, so the closing segments cannot cross the row.
                int last = points.Count - 1;
                points.Add((points[last].X, y0 + 8.0, 1.0));
                points.Add((points[first].X, y0 + 8.0, 1.0));
                edges.Add((last, points.Count - 2));
                edges.Add((points.Count - 2, points.Count - 1));
                edges.Add((points.Count - 1, first));
            }
            else if (random.NextDouble() < 0.5)
            {
                // A short branch rising from the middle of the row: a degree-3 junction.
                int anchor = first + length / 2;
                for (int k = 1; k <= 3; k++)
                {
                    points.Add((points[anchor].X, y0 + k * 1.5, points[anchor].Z));
                    edges.Add((k == 1 ? anchor : points.Count - 2, points.Count - 1));
                }
            }
        }

        int[] order = Enumerable.Range(0, points.Count).OrderBy(_ => random.Next()).ToArray();
        var vertices = new (double, double, double, PointCloudProcessor.VertexSource)[points.Count];
        for (int i = 0; i < points.Count; i++)
            vertices[order[i]] = (points[i].X, points[i].Y, points[i].Z, PointCloudProcessor.VertexSource.Breakline);

        var segments = edges.SelectMany(edge => new[] { order[edge.A], order[edge.B] }).ToArray();
        return (CreateMergedData(vertices, segments), 0.02);
    }

    /// <summary>The original chain simplification, kept as the oracle for collapse order.</summary>
    private static (double[] Xy, double[] Z, int[] Segments) ReferenceSimplify(PointCloudProcessor.MergedData input, double tolerance)
    {
        double xyTol = Math.Max(tolerance, 1e-9);
        double zTol = Math.Max(tolerance, 1e-6);
        double[] xy = input.XyCoords;
        double[] zs = input.ZValues;
        double DistSq(int p, int q) =>
            (xy[p * 2] - xy[q * 2]) * (xy[p * 2] - xy[q * 2]) + (xy[p * 2 + 1] - xy[q * 2 + 1]) * (xy[p * 2 + 1] - xy[q * 2 + 1]);
        bool Near(int p, int q) => DistSq(p, q) <= xyTol * xyTol && Math.Abs(zs[p] - zs[q]) <= zTol;

        bool Collinear(int a, int v, int b)
        {
            double dx = xy[b * 2] - xy[a * 2], dy = xy[b * 2 + 1] - xy[a * 2 + 1];
            double lenSq = dx * dx + dy * dy;
            if (lenSq <= xyTol * xyTol)
                return false;
            double t = ((xy[v * 2] - xy[a * 2]) * dx + (xy[v * 2 + 1] - xy[a * 2 + 1]) * dy) / lenSq;
            if (t <= 1e-6 || t >= 1.0 - 1e-6)
                return false;
            double px = xy[a * 2] + t * dx, py = xy[a * 2 + 1] + t * dy;
            double lineTol = Math.Max(xyTol * 0.25, 1e-9);
            double distSq = (xy[v * 2] - px) * (xy[v * 2] - px) + (xy[v * 2 + 1] - py) * (xy[v * 2 + 1] - py);
            if (distSq > lineTol * lineTol)
                return false;
            return Math.Abs(zs[v] - (zs[a] + t * (zs[b] - zs[a]))) <= zTol;
        }

        long Key(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
        var segments = new List<(int A, int B)>();
        var keys = new HashSet<long>();
        for (int i = 0; i < input.SegmentCount; i++)
        {
            int a = input.Segments[i * 2], b = input.Segments[i * 2 + 1];
            if (a != b && keys.Add(Key(a, b)))
                segments.Add((a, b));
        }

        while (true)
        {
            var adjacency = new Dictionary<int, List<int>>();
            void AddNeighbor(int v, int n)
            {
                if (!adjacency.TryGetValue(v, out var list))
                    adjacency[v] = list = new List<int>();
                if (!list.Contains(n))
                    list.Add(n);
            }

            foreach (var (a, b) in segments)
            {
                AddNeighbor(a, b);
                AddNeighbor(b, a);
            }

            bool changed = false;
            for (int v = 0; v < input.VertexCount && !changed; v++)
            {
                if (!adjacency.TryGetValue(v, out var neighbors) || neighbors.Count != 2)
                    continue;
                int a = neighbors[0], b = neighbors[1];
                if (a == b)
                    continue;
                if (!(Near(a, v) || Near(v, b) || Near(a, b)) && !Collinear(a, v, b))
                    continue;

                var rebuilt = new List<(int A, int B)>();
                var rebuiltKeys = new HashSet<long>();
                foreach (var segment in segments)
                {
                    bool remove = (segment.A == v && (segment.B == a || segment.B == b)) ||
                                  (segment.B == v && (segment.A == a || segment.A == b));
                    if (!remove && rebuiltKeys.Add(Key(segment.A, segment.B)))
                        rebuilt.Add(segment);
                }

                if (rebuiltKeys.Add(Key(a, b)))
                    rebuilt.Add((a, b));
                segments = rebuilt;
                changed = true;
            }

            if (!changed)
                break;
        }

        var keep = new bool[input.VertexCount];
        foreach (var (a, b) in segments)
            keep[a] = keep[b] = true;
        var remap = new int[input.VertexCount];
        var outXy = new List<double>();
        var outZ = new List<double>();
        for (int i = 0, next = 0; i < input.VertexCount; i++)
        {
            if (!keep[i])
                continue;
            remap[i] = next++;
            outXy.Add(xy[i * 2]);
            outXy.Add(xy[i * 2 + 1]);
            outZ.Add(zs[i]);
        }

        return (outXy.ToArray(), outZ.ToArray(), segments.SelectMany(s => new[] { remap[s.A], remap[s.B] }).ToArray());
    }

    private static PointCloudProcessor.MergedData CreateMergedData(
        IReadOnlyList<(double x, double y, double z, PointCloudProcessor.VertexSource source)> vertices,
        int[] segments)
    {
        var xyCoords = new double[vertices.Count * 2];
        var zValues = new double[vertices.Count];
        var sources = new PointCloudProcessor.VertexSource[vertices.Count];

        for (int i = 0; i < vertices.Count; i++)
        {
            xyCoords[i * 2] = vertices[i].x;
            xyCoords[i * 2 + 1] = vertices[i].y;
            zValues[i] = vertices[i].z;
            sources[i] = vertices[i].source;
        }

        return new PointCloudProcessor.MergedData(
            xyCoords,
            zValues,
            vertices.Count,
            sources,
            segments,
            segments.Length / 2,
            0,
            0);
    }
}
