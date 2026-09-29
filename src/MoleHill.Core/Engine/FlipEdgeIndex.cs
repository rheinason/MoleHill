namespace MoleHill.Core.Engine;

/// <summary>
/// Edge → incident-triangle index for one flip sweep, built by counting sort instead of a hash table.
///
/// It answers exactly what the <c>Dictionary&lt;long, (t0, o0, t1, o1, count)&gt;</c> built by
/// <see cref="MeshFlipGeometry.AddIncidence"/> answered, in the same order, so a sweep driven by it makes
/// the same flips in the same sequence. That dictionary, filled face by face and never removed from,
/// enumerates its keys in first-insertion order, which is the scan order of each edge's <b>first</b>
/// half-edge. Here a half-edge is <c>h = t * 3 + k</c> for corner <c>k</c> of live face <c>t</c>
/// (edges <c>ab</c>, <c>bc</c>, <c>ca</c>), so walking <c>h</c> upward and stopping at each edge's
/// first half-edge visits the edges in that same order.
///
/// Why: on a 5M-face terrain the dictionary held ~7.5M hashed entries and was rebuilt for every sweep of
/// every remesh iteration; its fill and walk were most of the 122 s flip phase in the park-scale stress
/// probe. The index is a handful of flat arrays in face order, reused across sweeps.
///
/// Layout: half-edges are bucketed by their lower vertex (CSR), and each bucket is sorted by
/// <c>(higher vertex, h)</c>, so an edge's half-edges are adjacent and in scan order.
/// </summary>
internal sealed class FlipEdgeIndex
{
    private const int InsertionSortLimit = 24;

    private int[] _bucketStart = Array.Empty<int>();
    private int[] _cursor = Array.Empty<int>();
    private long[] _entries = Array.Empty<long>();

    /// <summary>At an edge's first half-edge: the number of incident half-edges. Zero everywhere else.</summary>
    private int[] _count = Array.Empty<int>();

    /// <summary>At an edge's first half-edge: its second half-edge, or -1.</summary>
    private int[] _second = Array.Empty<int>();

    private int _vertexCount;

    /// <summary>Indexes the live faces of <paramref name="tris"/> (a face is dead when its first index is negative).</summary>
    public void Build(ReadOnlySpan<int> tris, int faceCount, int vertexCount)
    {
        _vertexCount = vertexCount;
        int halfEdgeCount = faceCount * 3;
        EnsureCapacity(ref _bucketStart, vertexCount + 1);
        EnsureCapacity(ref _cursor, vertexCount);
        EnsureCapacity(ref _entries, halfEdgeCount);
        EnsureCapacity(ref _count, halfEdgeCount);
        EnsureCapacity(ref _second, halfEdgeCount);
        Array.Clear(_bucketStart, 0, vertexCount + 1);
        Array.Clear(_count, 0, halfEdgeCount);

        // Pass 1: bucket sizes, keyed by the lower vertex of each half-edge.
        for (int t = 0; t < faceCount; t++)
        {
            int a = tris[t * 3];
            if (a < 0)
                continue;
            int b = tris[t * 3 + 1];
            int c = tris[t * 3 + 2];
            _bucketStart[Math.Min(a, b) + 1]++;
            _bucketStart[Math.Min(b, c) + 1]++;
            _bucketStart[Math.Min(c, a) + 1]++;
        }

        for (int v = 0; v < vertexCount; v++)
            _bucketStart[v + 1] += _bucketStart[v];

        // Pass 2: fill in scan order, so each bucket arrives already ordered by h.
        Array.Copy(_bucketStart, _cursor, vertexCount);
        for (int t = 0; t < faceCount; t++)
        {
            int a = tris[t * 3];
            if (a < 0)
                continue;
            int b = tris[t * 3 + 1];
            int c = tris[t * 3 + 2];
            int h = t * 3;
            Place(a, b, h);
            Place(b, c, h + 1);
            Place(c, a, h + 2);
        }

        // Pass 3: order each bucket by (higher vertex, h) and record each run's head.
        for (int v = 0; v < vertexCount; v++)
        {
            int start = _bucketStart[v];
            int end = _bucketStart[v + 1];
            if (end - start > 1)
            {
                if (end - start <= InsertionSortLimit)
                    InsertionSort(_entries, start, end);
                else
                    Array.Sort(_entries, start, end - start);
            }

            int i = start;
            while (i < end)
            {
                long hi = _entries[i] >> 32;
                int head = (int)(_entries[i] & 0xFFFFFFFFL);
                int run = i + 1;
                while (run < end && (_entries[run] >> 32) == hi)
                    run++;
                _count[head] = run - i;
                _second[head] = run - i > 1 ? (int)(_entries[i + 1] & 0xFFFFFFFFL) : -1;
                i = run;
            }
        }
    }

    /// <summary>
    /// Incident half-edge count when <paramref name="halfEdge"/> is its edge's first half-edge; zero when it
    /// is not, or its face is dead.
    /// </summary>
    public int FirstIncidenceCount(int halfEdge) => _count[halfEdge];

    /// <summary>The edge's second half-edge in scan order, or -1. Valid only at a first half-edge.</summary>
    public int SecondHalfEdge(int halfEdge) => _second[halfEdge];

    /// <summary>True when the undirected edge <paramref name="a"/>–<paramref name="b"/> was indexed.</summary>
    public bool ContainsEdge(int a, int b)
    {
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);
        if ((uint)lo >= (uint)_vertexCount)
            return false;

        int start = _bucketStart[lo];
        int end = _bucketStart[lo + 1];
        long probe = (long)hi << 32;
        int index = Array.BinarySearch(_entries, start, end - start, probe);
        if (index < 0)
            index = ~index;
        return index < end && (_entries[index] >> 32) == hi;
    }

    private void Place(int u, int v, int halfEdge)
    {
        int lo = Math.Min(u, v);
        int hi = Math.Max(u, v);
        _entries[_cursor[lo]++] = ((long)hi << 32) | (uint)halfEdge;
    }

    private static void InsertionSort(long[] items, int start, int end)
    {
        for (int i = start + 1; i < end; i++)
        {
            long value = items[i];
            int j = i - 1;
            while (j >= start && items[j] > value)
            {
                items[j + 1] = items[j];
                j--;
            }

            items[j + 1] = value;
        }
    }

    private static void EnsureCapacity<T>(ref T[] buffer, int required)
    {
        if (buffer.Length < required)
            buffer = new T[Math.Max(required, buffer.Length * 2)];
    }
}
