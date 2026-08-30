namespace MoleHill.Core.Engine;

/// <summary>
/// Vertex → incident faces and vertex → neighbor vertices for a triangle soup, stored as flat CSR
/// arrays (offset table + item array) instead of a <c>Dictionary&lt;int, List&lt;int&gt;&gt;</c> /
/// <c>Dictionary&lt;int, HashSet&lt;int&gt;&gt;</c> pair.
///
/// The remesh operator loop rebuilds this adjacency once per collapse round and once per relax sweep —
/// dozens of times per remesh. The dictionary form allocated one <see cref="List{T}"/> plus one
/// <see cref="HashSet{T}"/> per vertex per rebuild, which on a 180k-face terrain meant millions of small
/// objects and turned a linear pass into a GC-bound superlinear one (it measured as ~99% of total
/// collapse time). CSR allocates a fixed handful of arrays, reuses them across rebuilds via
/// <see cref="Rebuild"/>, and answers the same queries with better locality.
///
/// Neighbor slices are kept sorted so <see cref="NeighborsContain"/> is a binary search. Faces appended
/// after a build (a collapse re-points a face at the survivor) go to a small overflow list, so the CSR
/// arrays themselves stay fixed for the lifetime of a round.
/// </summary>
internal sealed class MeshVertexAdjacency
{
    private int[] _faceStart = Array.Empty<int>();
    private int[] _faceEnd = Array.Empty<int>();
    private int[] _faceItems = Array.Empty<int>();
    private int[] _neighborStart = Array.Empty<int>();
    private int[] _neighborEnd = Array.Empty<int>();
    private int[] _neighborItems = Array.Empty<int>();

    /// <summary>Faces appended by <see cref="AddFace"/> after the build, keyed by vertex.</summary>
    private readonly Dictionary<int, List<int>> _extraFaces = new();

    public int VertexCount { get; private set; }

    /// <summary>
    /// Builds (or rebuilds in place, reusing the existing buffers) the adjacency of the live faces of
    /// <paramref name="tris"/>. A face is live when its first index is non-negative (tombstoned faces
    /// carry -1). Vertices with no live face get empty slices.
    /// </summary>
    public static MeshVertexAdjacency Build(List<int> tris, int faceCount, int vertexCount, MeshVertexAdjacency? reuse = null)
    {
        MeshVertexAdjacency adjacency = reuse ?? new MeshVertexAdjacency();
        adjacency.Rebuild(tris, faceCount, vertexCount);
        return adjacency;
    }

    public void Rebuild(List<int> tris, int faceCount, int vertexCount)
    {
        VertexCount = vertexCount;
        _extraFaces.Clear();

        EnsureCapacity(ref _faceStart, vertexCount + 1);
        EnsureCapacity(ref _faceEnd, vertexCount + 1);
        EnsureCapacity(ref _neighborStart, vertexCount + 1);
        EnsureCapacity(ref _neighborEnd, vertexCount + 1);
        Array.Clear(_faceStart, 0, vertexCount + 1);

        // Pass 1: per-vertex incidence counts. A vertex repeated inside one (degenerate) face is
        // counted once, matching the deduplicating list the dictionary form built.
        for (int t = 0; t < faceCount; t++)
        {
            int a = tris[t * 3];
            if (a < 0)
                continue;
            int b = tris[t * 3 + 1];
            int c = tris[t * 3 + 2];
            CountVertex(_faceStart, a, vertexCount);
            if (b != a)
                CountVertex(_faceStart, b, vertexCount);
            if (c != a && c != b)
                CountVertex(_faceStart, c, vertexCount);
        }

        // Prefix sum → slice starts. Each vertex's neighbor slice is sized at twice its face count
        // (two directed edges per incident face), an upper bound before deduplication.
        int faceOffset = 0;
        int neighborOffset = 0;
        for (int v = 0; v < vertexCount; v++)
        {
            int count = _faceStart[v];
            _faceStart[v] = faceOffset;
            _faceEnd[v] = faceOffset;
            faceOffset += count;
            _neighborStart[v] = neighborOffset;
            _neighborEnd[v] = neighborOffset;
            neighborOffset += count * 2;
        }

        _faceStart[vertexCount] = faceOffset;
        _neighborStart[vertexCount] = neighborOffset;
        EnsureCapacity(ref _faceItems, faceOffset);
        EnsureCapacity(ref _neighborItems, neighborOffset);

        // Pass 2: fill both tables. _faceEnd / _neighborEnd double as write cursors.
        for (int t = 0; t < faceCount; t++)
        {
            int a = tris[t * 3];
            if (a < 0)
                continue;
            int b = tris[t * 3 + 1];
            int c = tris[t * 3 + 2];

            AddFaceItem(a, t, vertexCount);
            if (b != a)
                AddFaceItem(b, t, vertexCount);
            if (c != a && c != b)
                AddFaceItem(c, t, vertexCount);

            AddNeighborItem(a, b, vertexCount);
            AddNeighborItem(b, a, vertexCount);
            AddNeighborItem(b, c, vertexCount);
            AddNeighborItem(c, b, vertexCount);
            AddNeighborItem(c, a, vertexCount);
            AddNeighborItem(a, c, vertexCount);
        }

        // Pass 3: sort each neighbor slice and drop duplicates in place (degrees are small, so an
        // insertion sort beats Array.Sort's per-call overhead across one call per vertex).
        for (int v = 0; v < vertexCount; v++)
        {
            int start = _neighborStart[v];
            int end = _neighborEnd[v];
            InsertionSort(_neighborItems, start, end);

            int write = start;
            for (int i = start; i < end; i++)
            {
                int value = _neighborItems[i];
                if (value == v)
                    continue; // self-loop from a degenerate face
                if (write > start && _neighborItems[write - 1] == value)
                    continue;
                _neighborItems[write++] = value;
            }

            _neighborEnd[v] = write;
        }
    }

    /// <summary>Live and appended faces incident to <paramref name="vertex"/>.</summary>
    public FaceEnumerable FacesOf(int vertex)
    {
        if ((uint)vertex >= (uint)VertexCount)
            return new FaceEnumerable(Array.Empty<int>(), 0, 0, null);

        _extraFaces.TryGetValue(vertex, out List<int>? extra);
        return new FaceEnumerable(_faceItems, _faceStart[vertex], _faceEnd[vertex], extra);
    }

    /// <summary>Distinct neighbor vertices of <paramref name="vertex"/>, ascending.</summary>
    public ReadOnlySpan<int> NeighborsOf(int vertex)
    {
        if ((uint)vertex >= (uint)VertexCount)
            return ReadOnlySpan<int>.Empty;

        int start = _neighborStart[vertex];
        return _neighborItems.AsSpan(start, _neighborEnd[vertex] - start);
    }

    public int NeighborCount(int vertex) =>
        (uint)vertex >= (uint)VertexCount ? 0 : _neighborEnd[vertex] - _neighborStart[vertex];

    public bool HasNeighbors(int vertex) => NeighborCount(vertex) > 0;

    public bool NeighborsContain(int vertex, int neighbor) =>
        NeighborsOf(vertex).BinarySearch(neighbor) >= 0;

    /// <summary>
    /// Records a face that became incident to <paramref name="vertex"/> after the build (a collapse
    /// survivor inheriting the removed vertex's faces). Duplicates are ignored.
    /// </summary>
    public void AddFace(int vertex, int face)
    {
        if ((uint)vertex >= (uint)VertexCount)
            return;

        int start = _faceStart[vertex];
        int end = _faceEnd[vertex];
        for (int i = start; i < end; i++)
        {
            if (_faceItems[i] == face)
                return;
        }

        if (!_extraFaces.TryGetValue(vertex, out List<int>? extra))
        {
            extra = new List<int>(2);
            _extraFaces.Add(vertex, extra);
        }

        if (!extra.Contains(face))
            extra.Add(face);
    }

    private static void CountVertex(int[] counts, int vertex, int vertexCount)
    {
        if ((uint)vertex < (uint)vertexCount)
            counts[vertex]++;
    }

    private void AddFaceItem(int vertex, int face, int vertexCount)
    {
        if ((uint)vertex < (uint)vertexCount)
            _faceItems[_faceEnd[vertex]++] = face;
    }

    private void AddNeighborItem(int vertex, int neighbor, int vertexCount)
    {
        if ((uint)vertex < (uint)vertexCount && (uint)neighbor < (uint)vertexCount)
            _neighborItems[_neighborEnd[vertex]++] = neighbor;
    }

    private static void InsertionSort(int[] items, int start, int end)
    {
        for (int i = start + 1; i < end; i++)
        {
            int value = items[i];
            int j = i - 1;
            while (j >= start && items[j] > value)
            {
                items[j + 1] = items[j];
                j--;
            }

            items[j + 1] = value;
        }
    }

    private static void EnsureCapacity(ref int[] buffer, int required)
    {
        if (buffer.Length < required)
            buffer = new int[Math.Max(required, buffer.Length * 2)];
    }

    /// <summary>Allocation-free enumeration over a CSR face slice plus its overflow list.</summary>
    internal readonly struct FaceEnumerable
    {
        private readonly int[] _items;
        private readonly int _start;
        private readonly int _end;
        private readonly List<int>? _extra;

        public FaceEnumerable(int[] items, int start, int end, List<int>? extra)
        {
            _items = items;
            _start = start;
            _end = end;
            _extra = extra;
        }

        public Enumerator GetEnumerator() => new(_items, _start, _end, _extra);

        internal struct Enumerator
        {
            private readonly int[] _items;
            private readonly int _end;
            private readonly List<int>? _extra;
            private int _index;
            private int _extraIndex;

            public Enumerator(int[] items, int start, int end, List<int>? extra)
            {
                _items = items;
                _end = end;
                _extra = extra;
                _index = start - 1;
                _extraIndex = -1;
                Current = 0;
            }

            public int Current { get; private set; }

            public bool MoveNext()
            {
                if (_index + 1 < _end)
                {
                    Current = _items[++_index];
                    return true;
                }

                _index = _end;
                if (_extra != null && _extraIndex + 1 < _extra.Count)
                {
                    Current = _extra[++_extraIndex];
                    return true;
                }

                return false;
            }
        }
    }
}
