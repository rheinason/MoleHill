namespace MoleHill.Core.Engine;

// Sorted-edge boundary analysis with compressed sparse-safe vertex adjacency.
public static class MeshTopologyValidator
{
    public readonly record struct BoundaryGraphAnalysis(
        int BoundaryEdgeCount,
        int BoundaryVertexCount,
        int BoundaryComponentCount,
        bool HasOpenBoundaryChains,
        int NonManifoldEdgeCount)
    {
        public bool HasSingleClosedBoundaryLoop =>
            BoundaryEdgeCount > 0 &&
            BoundaryComponentCount == 1 &&
            !HasOpenBoundaryChains &&
            NonManifoldEdgeCount == 0;
    }

    internal sealed class FlatBoundaryTopology
    {
        private readonly int[] _boundaryVertices;
        private readonly int[] _neighborOffsets;
        private readonly int[] _neighbors;

        internal BoundaryGraphAnalysis Analysis { get; }

        private FlatBoundaryTopology(
            BoundaryGraphAnalysis analysis,
            int[] boundaryVertices,
            int[] neighborOffsets,
            int[] neighbors)
        {
            Analysis = analysis;
            _boundaryVertices = boundaryVertices;
            _neighborOffsets = neighborOffsets;
            _neighbors = neighbors;
        }

        internal static FlatBoundaryTopology Build(int[] faces, int faceCount)
        {
            long[] sortedEdgeKeys = BuildSortedEdgeKeys(faces, faceCount);
            int boundaryEdgeCount = 0;
            int nonManifoldEdgeCount = 0;
            int index = 0;
            while (index < sortedEdgeKeys.Length)
            {
                int runLength = CountRun(sortedEdgeKeys, index);
                if (runLength == 1)
                    boundaryEdgeCount++;
                else if (runLength > 2)
                    nonManifoldEdgeCount++;
                index += runLength;
            }

            if (boundaryEdgeCount == 0)
            {
                return new FlatBoundaryTopology(
                    new BoundaryGraphAnalysis(
                        0,
                        0,
                        0,
                        HasOpenBoundaryChains: true,
                        nonManifoldEdgeCount),
                    Array.Empty<int>(),
                    new[] { 0 },
                    Array.Empty<int>());
            }

            var boundaryKeys = new long[boundaryEdgeCount];
            int nextBoundary = 0;
            index = 0;
            while (index < sortedEdgeKeys.Length)
            {
                int runLength = CountRun(sortedEdgeKeys, index);
                if (runLength == 1)
                    boundaryKeys[nextBoundary++] = sortedEdgeKeys[index];
                index += runLength;
            }

            var boundaryVertices = new int[boundaryEdgeCount * 2];
            for (int edge = 0; edge < boundaryEdgeCount; edge++)
            {
                long key = boundaryKeys[edge];
                boundaryVertices[edge * 2] = (int)(key >> 32);
                boundaryVertices[edge * 2 + 1] = (int)(key & 0xFFFFFFFFL);
            }

            Array.Sort(boundaryVertices);
            int boundaryVertexCount = CompactSortedValues(boundaryVertices);
            Array.Resize(ref boundaryVertices, boundaryVertexCount);

            var localEdges = new int[boundaryEdgeCount * 2];
            var degrees = new int[boundaryVertexCount];
            for (int edge = 0; edge < boundaryEdgeCount; edge++)
            {
                long key = boundaryKeys[edge];
                int a = Array.BinarySearch(boundaryVertices, (int)(key >> 32));
                int b = Array.BinarySearch(boundaryVertices, (int)(key & 0xFFFFFFFFL));
                localEdges[edge * 2] = a;
                localEdges[edge * 2 + 1] = b;
                degrees[a]++;
                degrees[b]++;
            }

            bool hasOpenBoundaryChains = false;
            for (int vertex = 0; vertex < boundaryVertexCount; vertex++)
            {
                if (degrees[vertex] != 2)
                {
                    hasOpenBoundaryChains = true;
                    break;
                }
            }

            var neighborOffsets = new int[boundaryVertexCount + 1];
            for (int vertex = 0; vertex < boundaryVertexCount; vertex++)
                neighborOffsets[vertex + 1] = neighborOffsets[vertex] + degrees[vertex];

            var neighbors = new int[boundaryEdgeCount * 2];
            var writeOffsets = new int[boundaryVertexCount];
            Array.Copy(neighborOffsets, writeOffsets, boundaryVertexCount);
            for (int edge = 0; edge < boundaryEdgeCount; edge++)
            {
                int a = localEdges[edge * 2];
                int b = localEdges[edge * 2 + 1];
                neighbors[writeOffsets[a]++] = b;
                neighbors[writeOffsets[b]++] = a;
            }

            int boundaryComponentCount = CountComponents(neighborOffsets, neighbors);
            return new FlatBoundaryTopology(
                new BoundaryGraphAnalysis(
                    boundaryEdgeCount,
                    boundaryVertexCount,
                    boundaryComponentCount,
                    hasOpenBoundaryChains,
                    nonManifoldEdgeCount),
                boundaryVertices,
                neighborOffsets,
                neighbors);
        }

        internal bool TryGetSingleBoundaryLoop(out int[] order)
        {
            order = Array.Empty<int>();
            if (Analysis.BoundaryEdgeCount < 3 ||
                Analysis.BoundaryComponentCount != 1 ||
                Analysis.HasOpenBoundaryChains)
            {
                return false;
            }

            var visited = new bool[_boundaryVertices.Length];
            if (!TryTraceLoop(0, visited, out int[] localOrder) ||
                localOrder.Length != _boundaryVertices.Length)
            {
                return false;
            }

            order = MapToSourceVertices(localOrder);
            return true;
        }

        internal bool TryGetBoundaryLoops(out List<int[]> loops)
        {
            loops = new List<int[]>();
            if (Analysis.BoundaryEdgeCount == 0 || Analysis.HasOpenBoundaryChains)
                return false;

            var visited = new bool[_boundaryVertices.Length];
            for (int seed = 0; seed < _boundaryVertices.Length; seed++)
            {
                if (visited[seed])
                    continue;

                if (!TryTraceLoop(seed, visited, out int[] localOrder))
                    return false;

                if (localOrder.Length >= 3)
                    loops.Add(MapToSourceVertices(localOrder));
            }

            return loops.Count > 0;
        }

        private bool TryTraceLoop(int seed, bool[] visited, out int[] localOrder)
        {
            var buffer = new int[_boundaryVertices.Length];
            int count = 0;
            int previous = -1;
            int current = seed;

            while (true)
            {
                if ((uint)current >= (uint)_boundaryVertices.Length ||
                    visited[current] && current != seed ||
                    count >= buffer.Length)
                {
                    localOrder = Array.Empty<int>();
                    return false;
                }

                buffer[count++] = current;
                visited[current] = true;

                int start = _neighborOffsets[current];
                int end = _neighborOffsets[current + 1];
                if (end - start != 2)
                {
                    localOrder = Array.Empty<int>();
                    return false;
                }

                int first = _neighbors[start];
                int next = first != previous ? first : _neighbors[start + 1];
                previous = current;
                current = next;
                if (current == seed)
                    break;
            }

            localOrder = new int[count];
            Array.Copy(buffer, localOrder, count);
            return true;
        }

        private int[] MapToSourceVertices(int[] localOrder)
        {
            var sourceOrder = new int[localOrder.Length];
            for (int index = 0; index < localOrder.Length; index++)
                sourceOrder[index] = _boundaryVertices[localOrder[index]];
            return sourceOrder;
        }

        private static int CountComponents(int[] neighborOffsets, int[] neighbors)
        {
            int vertexCount = neighborOffsets.Length - 1;
            var visited = new bool[vertexCount];
            var stack = new int[vertexCount];
            int componentCount = 0;

            for (int seed = 0; seed < vertexCount; seed++)
            {
                if (visited[seed])
                    continue;

                componentCount++;
                int stackCount = 1;
                stack[0] = seed;
                visited[seed] = true;
                while (stackCount > 0)
                {
                    int current = stack[--stackCount];
                    for (int offset = neighborOffsets[current]; offset < neighborOffsets[current + 1]; offset++)
                    {
                        int next = neighbors[offset];
                        if (visited[next])
                            continue;

                        visited[next] = true;
                        stack[stackCount++] = next;
                    }
                }
            }

            return componentCount;
        }
    }

    public static BoundaryGraphAnalysis AnalyzeBoundaryGraph(int[] faces, int faceCount)
    {
        return AnalyzeBoundaryTopology(faces, faceCount).Analysis;
    }

    internal static FlatBoundaryTopology AnalyzeBoundaryTopology(int[] faces, int faceCount)
    {
        return FlatBoundaryTopology.Build(faces, faceCount);
    }

    internal static bool HasNonManifoldEdge(int[] faces, int faceCount)
    {
        long[] sortedEdgeKeys = BuildSortedEdgeKeys(faces, faceCount);
        int index = 0;
        while (index < sortedEdgeKeys.Length)
        {
            int runLength = CountRun(sortedEdgeKeys, index);
            if (runLength > 2)
                return true;
            index += runLength;
        }

        return false;
    }

    private static long[] BuildSortedEdgeKeys(int[] faces, int faceCount)
    {
        if (faceCount <= 0)
            return Array.Empty<long>();

        var sortedEdgeKeys = new long[checked(faceCount * 3)];
        for (int face = 0; face < faceCount; face++)
        {
            int a = faces[face * 3];
            int b = faces[face * 3 + 1];
            int c = faces[face * 3 + 2];
            sortedEdgeKeys[face * 3] = IndexedMeshTools.GetEdgeKey(a, b);
            sortedEdgeKeys[face * 3 + 1] = IndexedMeshTools.GetEdgeKey(b, c);
            sortedEdgeKeys[face * 3 + 2] = IndexedMeshTools.GetEdgeKey(c, a);
        }

        Array.Sort(sortedEdgeKeys);
        return sortedEdgeKeys;
    }

    private static int CountRun(long[] sortedValues, int start)
    {
        long value = sortedValues[start];
        int length = 1;
        while (start + length < sortedValues.Length && sortedValues[start + length] == value)
            length++;
        return length;
    }

    private static int CompactSortedValues(int[] sortedValues)
    {
        if (sortedValues.Length == 0)
            return 0;

        int count = 1;
        for (int index = 1; index < sortedValues.Length; index++)
        {
            if (sortedValues[index] == sortedValues[count - 1])
                continue;
            sortedValues[count++] = sortedValues[index];
        }

        return count;
    }
}
