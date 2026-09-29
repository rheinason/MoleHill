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
/// after a build (a collapse re-points a face at the survivor) go to a per-vertex overflow chain, so the
/// CSR arrays themselves stay fixed for the lifetime of a round. The chains are flat arrays indexed by
/// vertex rather than a dictionary: <see cref="FacesOf"/> runs several times per collapse attempt, and a
/// hashed probe per call was a measurable share of the 1 m park's collapse phase.
/// </summary>
internal sealed class MeshVertexAdjacency
{
    private int[] _faceStart = Array.Empty<int>();
    private int[] _faceEnd = Array.Empty<int>();
    private int[] _faceItems = Array.Empty<int>();
    private int[] _neighborStart = Array.Empty<int>();
    private int[] _neighborEnd = Array.Empty<int>();
    private int[] _neighborItems = Array.Empty<int>();

    // Faces appended by AddFace after the build: per vertex, the first node of a chain in insertion
    // order (-1 for none), and the nodes themselves. Only the vertices in _extraTouched are reset.
    private int[] _extraHead = Array.Empty<int>();
    private int[] _extraTail = Array.Empty<int>();
    private int[] _extraNodeFace = new int[16];
    private int[] _extraNodeNext = new int[16];
    private int _extraNodeCount;
    private readonly List<int> _extraTouched = new();

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
        foreach (int touched in _extraTouched)
        {
            if (touched < _extraHead.Length)
                _extraHead[touched] = -1;
        }

        _extraTouched.Clear();
        _extraNodeCount = 0;
        if (_extraHead.Length < vertexCount)
        {
            _extraHead = new int[Math.Max(vertexCount, _extraHead.Length * 2)];
            _extraTail = new int[_extraHead.Length];
            Array.Fill(_extraHead, -1);
        }

        EnsureCapacity(ref _faceStart, vertexCount + 1);
        EnsureCapacity(ref _faceEnd, vertexCount + 1);
        EnsureCapacity(ref _neighborStart, vertexCount + 1);
        EnsureCapacity(ref _neighborEnd, vertexCount + 1);
        Array.Clear(_faceStart, 0, vertexCount + 1);

        ReadOnlySpan<int> faceIndices = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tris);

        // Pass 1: per-vertex incidence counts. A vertex repeated inside one (degenerate) face is
        // counted once, matching the deduplicating list the dictionary form built.
        for (int t = 0; t < faceCount; t++)
        {
            int a = faceIndices[t * 3];
            if (a < 0)
                continue;
            int b = faceIndices[t * 3 + 1];
            int c = faceIndices[t * 3 + 2];
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
            int a = faceIndices[t * 3];
            if (a < 0)
                continue;
            int b = faceIndices[t * 3 + 1];
            int c = faceIndices[t * 3 + 2];

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
        // insertion sort beats Array.Sort's per-call overhead across one call per vertex). Slices are
        // independent, so a large mesh sorts them in parallel; the remesh collapse phase rebuilds this
        // once per round on the whole terrain.
        if (vertexCount >= ParallelFinishMinimum)
        {
            int blockCount = (vertexCount + ParallelFinishBlock - 1) / ParallelFinishBlock;
            System.Threading.Tasks.Parallel.For(0, blockCount, block =>
                FinishNeighborSlices(block * ParallelFinishBlock, Math.Min(vertexCount, (block + 1) * ParallelFinishBlock)));
        }
        else
        {
            FinishNeighborSlices(0, vertexCount);
        }
    }

    private const int ParallelFinishMinimum = 65_536;
    private const int ParallelFinishBlock = 16_384;

    private void FinishNeighborSlices(int fromVertex, int toVertex)
    {
        for (int v = fromVertex; v < toVertex; v++)
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
            return new FaceEnumerable(Array.Empty<int>(), 0, 0, _extraNodeFace, _extraNodeNext, -1);

        return new FaceEnumerable(
            _faceItems, _faceStart[vertex], _faceEnd[vertex], _extraNodeFace, _extraNodeNext, _extraHead[vertex]);
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

        int head = _extraHead[vertex];
        for (int node = head; node >= 0; node = _extraNodeNext[node])
        {
            if (_extraNodeFace[node] == face)
                return;
        }

        if (_extraNodeCount == _extraNodeFace.Length)
        {
            Array.Resize(ref _extraNodeFace, _extraNodeCount * 2);
            Array.Resize(ref _extraNodeNext, _extraNodeCount * 2);
        }

        int added = _extraNodeCount++;
        _extraNodeFace[added] = face;
        _extraNodeNext[added] = -1;
        if (head < 0)
        {
            _extraHead[vertex] = added;
            _extraTouched.Add(vertex);
        }
        else
        {
            _extraNodeNext[_extraTail[vertex]] = added;
        }

        _extraTail[vertex] = added;
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

    /// <summary>Allocation-free enumeration over a CSR face slice plus its overflow chain.</summary>
    internal readonly struct FaceEnumerable
    {
        private readonly int[] _items;
        private readonly int _start;
        private readonly int _end;
        private readonly int[] _extraFace;
        private readonly int[] _extraNext;
        private readonly int _extraHead;

        public FaceEnumerable(int[] items, int start, int end, int[] extraFace, int[] extraNext, int extraHead)
        {
            _items = items;
            _start = start;
            _end = end;
            _extraFace = extraFace;
            _extraNext = extraNext;
            _extraHead = extraHead;
        }

        public Enumerator GetEnumerator() => new(_items, _start, _end, _extraFace, _extraNext, _extraHead);

        internal struct Enumerator
        {
            private readonly int[] _items;
            private readonly int _end;
            private readonly int[] _extraFace;
            private readonly int[] _extraNext;
            private int _index;
            private int _extraNode;

            public Enumerator(int[] items, int start, int end, int[] extraFace, int[] extraNext, int extraHead)
            {
                _items = items;
                _end = end;
                _extraFace = extraFace;
                _extraNext = extraNext;
                _index = start - 1;
                // Before the first extra node, _extraNode holds its index encoded as -(head + 2), so
                // -1 still means "chain finished" and -2 upward mean "not started".
                _extraNode = extraHead >= 0 ? -(extraHead + 2) : -1;
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
                int next = _extraNode <= -2 ? -(_extraNode + 2) : _extraNode >= 0 ? _extraNext[_extraNode] : -1;
                if (next < 0)
                {
                    _extraNode = -1;
                    return false;
                }

                _extraNode = next;
                Current = _extraFace[next];
                return true;
            }
        }
    }
}
