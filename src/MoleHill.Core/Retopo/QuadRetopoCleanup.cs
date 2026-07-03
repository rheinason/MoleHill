using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Retopo;

/// <summary>
/// Practical cleanup for the first-cut quad extractor: close internal lattice holes with triangle fans
/// lifted back to the source terrain, and weld separately-built quad sets before Rhino mesh creation.
/// </summary>
public static class QuadRetopoCleanup
{
    public sealed class Options
    {
        public double Tolerance { get; init; }

        /// <summary>
        /// Maximum XY area of an internal loop that may be closed by the cheap fan fill. Larger holes are
        /// usually parametrization defects or feature corridors; fanning those creates obvious spikes.
        /// 0 disables the area cap.
        /// </summary>
        public double MaxFanFillArea { get; init; }
    }

    public sealed class CleanupResult
    {
        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Quads { get; init; } = Array.Empty<int>();

        public int[] Tris { get; init; } = Array.Empty<int>();

        public int ClosedLoopCount { get; init; }

        public int AddedTriangleCount { get; init; }

        public int SkippedLargeLoopCount { get; init; }

        public string? Warning { get; init; }
    }

    public sealed class WeldResult
    {
        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Quads { get; init; } = Array.Empty<int>();

        public int[] Tris { get; init; } = Array.Empty<int>();

        public int RemovedVertexCount { get; init; }

        public int DroppedFaceCount { get; init; }
    }

    public static CleanupResult CloseInternalBoundaryLoops(
        double[] sourceVertices,
        int[] sourceFaces,
        double[] vertices,
        int[] quads,
        int[] tris,
        Options options)
    {
        List<BoundaryLoop> loops = FindBoundaryLoops(vertices, quads, tris);
        if (loops.Count <= 1)
        {
            return new CleanupResult
            {
                Vertices = vertices,
                Quads = quads,
                Tris = tris
            };
        }

        int outerIndex = 0;
        double outerScore = double.NegativeInfinity;
        for (int i = 0; i < loops.Count; i++)
        {
            double score = Math.Abs(loops[i].Area);
            if (score > outerScore)
            {
                outerScore = score;
                outerIndex = i;
            }
        }

        var outVertices = new List<double>(vertices);
        var outTris = new List<int>(tris);
        var grid = new TerrainFaceGrid(sourceVertices, sourceVertices.Length / 3, sourceFaces, sourceFaces.Length / 3);

        int closedLoops = 0;
        int addedTriangles = 0;
        int skippedLarge = 0;
        int skipped = 0;
        double tolerance = Math.Max(options.Tolerance, 1e-9);
        double maxFanFillArea = Math.Max(0.0, options.MaxFanFillArea);

        for (int i = 0; i < loops.Count; i++)
        {
            if (i == outerIndex)
                continue;

            BoundaryLoop loop = loops[i];
            if (loop.Vertices.Count < 3 || Math.Abs(loop.Area) <= tolerance * tolerance)
            {
                skipped++;
                continue;
            }

            if (maxFanFillArea > 0.0 && Math.Abs(loop.Area) > maxFanFillArea)
            {
                skippedLarge++;
                continue;
            }

            int center = outVertices.Count / 3;
            double cx = 0.0;
            double cy = 0.0;
            foreach (int vertex in loop.Vertices)
            {
                cx += vertices[vertex * 3];
                cy += vertices[vertex * 3 + 1];
            }

            cx /= loop.Vertices.Count;
            cy /= loop.Vertices.Count;
            outVertices.Add(cx);
            outVertices.Add(cy);
            outVertices.Add(grid.InterpolateZ(cx, cy));

            for (int j = 0; j < loop.Vertices.Count; j++)
            {
                int a = loop.Vertices[j];
                int b = loop.Vertices[(j + 1) % loop.Vertices.Count];
                if (a == b)
                    continue;

                outTris.Add(center);
                outTris.Add(a);
                outTris.Add(b);
                addedTriangles++;
            }

            closedLoops++;
        }

        return new CleanupResult
        {
            Vertices = outVertices.ToArray(),
            Quads = quads,
            Tris = outTris.ToArray(),
            ClosedLoopCount = closedLoops,
            AddedTriangleCount = addedTriangles,
            SkippedLargeLoopCount = skippedLarge,
            Warning = BuildCleanupWarning(skipped, skippedLarge)
        };
    }

    private static string? BuildCleanupWarning(int skippedDegenerate, int skippedLarge)
    {
        var parts = new List<string>(2);
        if (skippedDegenerate > 0)
            parts.Add($"skipped {skippedDegenerate:N0} degenerate cleanup loop(s)");
        if (skippedLarge > 0)
            parts.Add($"left {skippedLarge:N0} large retopo gap loop(s) open instead of fan-filling them");
        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    public static WeldResult WeldByTolerance(double[] vertices, int[] quads, int[] tris, double tolerance)
    {
        int inputVertexCount = vertices.Length / 3;
        if (inputVertexCount == 0)
            return new WeldResult { Vertices = vertices, Quads = quads, Tris = tris };

        double cell = Math.Max(tolerance, 1e-9);
        double invCell = 1.0 / cell;
        double toleranceSquared = cell * cell;
        var buckets = new Dictionary<(long x, long y, long z), List<int>>();
        var outVertices = new List<double>(vertices.Length);
        var remap = new int[inputVertexCount];

        for (int i = 0; i < inputVertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            double z = vertices[i * 3 + 2];
            long cx = (long)Math.Floor(x * invCell);
            long cy = (long)Math.Floor(y * invCell);
            long cz = (long)Math.Floor(z * invCell);

            int existing = FindExistingVertex(buckets, outVertices, x, y, z, cx, cy, cz, toleranceSquared);
            if (existing >= 0)
            {
                remap[i] = existing;
                continue;
            }

            int next = outVertices.Count / 3;
            outVertices.Add(x);
            outVertices.Add(y);
            outVertices.Add(z);
            remap[i] = next;

            var key = (cx, cy, cz);
            if (!buckets.TryGetValue(key, out List<int>? bucket))
            {
                bucket = new List<int>(2);
                buckets.Add(key, bucket);
            }

            bucket.Add(next);
        }

        int dropped = 0;
        var outQuads = new List<int>(quads.Length);
        for (int i = 0; i < quads.Length / 4; i++)
        {
            int a = remap[quads[i * 4]];
            int b = remap[quads[i * 4 + 1]];
            int c = remap[quads[i * 4 + 2]];
            int d = remap[quads[i * 4 + 3]];
            if (a == b || b == c || c == d || d == a || a == c || b == d)
            {
                dropped++;
                continue;
            }

            outQuads.Add(a);
            outQuads.Add(b);
            outQuads.Add(c);
            outQuads.Add(d);
        }

        var outTris = new List<int>(tris.Length);
        for (int i = 0; i < tris.Length / 3; i++)
        {
            int a = remap[tris[i * 3]];
            int b = remap[tris[i * 3 + 1]];
            int c = remap[tris[i * 3 + 2]];
            if (a == b || b == c || c == a)
            {
                dropped++;
                continue;
            }

            outTris.Add(a);
            outTris.Add(b);
            outTris.Add(c);
        }

        return new WeldResult
        {
            Vertices = outVertices.ToArray(),
            Quads = outQuads.ToArray(),
            Tris = outTris.ToArray(),
            RemovedVertexCount = inputVertexCount - (outVertices.Count / 3),
            DroppedFaceCount = dropped
        };
    }

    private static int FindExistingVertex(
        Dictionary<(long x, long y, long z), List<int>> buckets,
        List<double> vertices,
        double x,
        double y,
        double z,
        long cx,
        long cy,
        long cz,
        double toleranceSquared)
    {
        for (long dz = -1; dz <= 1; dz++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                for (long dx = -1; dx <= 1; dx++)
                {
                    if (!buckets.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? bucket))
                        continue;

                    foreach (int candidate in bucket)
                    {
                        double vx = vertices[candidate * 3] - x;
                        double vy = vertices[candidate * 3 + 1] - y;
                        double vz = vertices[candidate * 3 + 2] - z;
                        if ((vx * vx) + (vy * vy) + (vz * vz) <= toleranceSquared)
                            return candidate;
                    }
                }
            }
        }

        return -1;
    }

    private static List<BoundaryLoop> FindBoundaryLoops(double[] vertices, int[] quads, int[] tris)
    {
        var edgeCounts = new Dictionary<long, int>(IndexedMeshTools.EdgeKeyComparer.Instance);
        CountQuadEdges(edgeCounts, quads);
        CountTriEdges(edgeCounts, tris);

        var adjacency = new Dictionary<int, List<int>>();
        foreach (KeyValuePair<long, int> entry in edgeCounts)
        {
            if (entry.Value != 1)
                continue;

            int a = (int)(entry.Key >> 32);
            int b = (int)(entry.Key & 0xFFFFFFFFL);
            AddNeighbor(adjacency, a, b);
            AddNeighbor(adjacency, b, a);
        }

        var loops = new List<BoundaryLoop>();
        var visited = new HashSet<int>();
        foreach (int start in adjacency.Keys)
        {
            if (visited.Contains(start))
                continue;

            if (!TryTraceLoop(adjacency, start, visited, out List<int>? loop))
                continue;

            loops.Add(new BoundaryLoop(loop, SignedArea(vertices, loop)));
        }

        return loops;
    }

    private static void CountQuadEdges(Dictionary<long, int> edgeCounts, int[] quads)
    {
        for (int i = 0; i < quads.Length / 4; i++)
        {
            int a = quads[i * 4];
            int b = quads[i * 4 + 1];
            int c = quads[i * 4 + 2];
            int d = quads[i * 4 + 3];
            CountEdge(edgeCounts, a, b);
            CountEdge(edgeCounts, b, c);
            CountEdge(edgeCounts, c, d);
            CountEdge(edgeCounts, d, a);
        }
    }

    private static void CountTriEdges(Dictionary<long, int> edgeCounts, int[] tris)
    {
        for (int i = 0; i < tris.Length / 3; i++)
        {
            int a = tris[i * 3];
            int b = tris[i * 3 + 1];
            int c = tris[i * 3 + 2];
            CountEdge(edgeCounts, a, b);
            CountEdge(edgeCounts, b, c);
            CountEdge(edgeCounts, c, a);
        }
    }

    private static void CountEdge(Dictionary<long, int> edgeCounts, int a, int b)
    {
        long key = IndexedMeshTools.GetEdgeKey(a, b);
        edgeCounts[key] = edgeCounts.GetValueOrDefault(key, 0) + 1;
    }

    private static void AddNeighbor(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out List<int>? neighbors))
        {
            neighbors = new List<int>(2);
            adjacency.Add(from, neighbors);
        }

        neighbors.Add(to);
    }

    private static bool TryTraceLoop(
        Dictionary<int, List<int>> adjacency,
        int start,
        HashSet<int> visited,
        out List<int> loop)
    {
        loop = new List<int>();
        int previous = -1;
        int current = start;

        while (true)
        {
            if (!visited.Add(current))
                return current == start && loop.Count >= 3;

            loop.Add(current);
            if (!adjacency.TryGetValue(current, out List<int>? neighbors) || neighbors.Count != 2)
                return false;

            int next = neighbors[0] == previous ? neighbors[1] : neighbors[0];
            previous = current;
            current = next;
            if (current == start)
                return loop.Count >= 3;
        }
    }

    private static double SignedArea(double[] vertices, List<int> loop)
    {
        double twiceArea = 0.0;
        for (int i = 0; i < loop.Count; i++)
        {
            int a = loop[i];
            int b = loop[(i + 1) % loop.Count];
            twiceArea += (vertices[a * 3] * vertices[b * 3 + 1]) - (vertices[b * 3] * vertices[a * 3 + 1]);
        }

        return 0.5 * twiceArea;
    }

    private sealed record BoundaryLoop(List<int> Vertices, double Area);
}
