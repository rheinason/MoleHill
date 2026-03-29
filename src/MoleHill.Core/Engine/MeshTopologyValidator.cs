namespace MoleHill.Core.Engine;

public static class MeshTopologyValidator
{
    public readonly record struct BoundaryGraphAnalysis(
        int BoundaryEdgeCount,
        int BoundaryVertexCount,
        int BoundaryComponentCount,
        bool HasOpenBoundaryChains)
    {
        public bool HasSingleClosedBoundaryLoop =>
            BoundaryEdgeCount > 0 &&
            BoundaryComponentCount == 1 &&
            !HasOpenBoundaryChains;
    }

    public static BoundaryGraphAnalysis AnalyzeBoundaryGraph(int[] faces, int faceCount)
    {
        var edgeCounts = new Dictionary<long, int>();
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];

            CountEdge(edgeCounts, a, b);
            CountEdge(edgeCounts, b, c);
            CountEdge(edgeCounts, c, a);
        }

        var adjacency = new Dictionary<int, List<int>>();
        var degree = new Dictionary<int, int>();
        int boundaryEdgeCount = 0;

        foreach (var (edgeKey, count) in edgeCounts)
        {
            if (count != 1)
                continue;

            boundaryEdgeCount++;
            int a = (int)(edgeKey >> 32);
            int b = (int)(edgeKey & 0xFFFFFFFFL);
            AddNeighbor(adjacency, degree, a, b);
            AddNeighbor(adjacency, degree, b, a);
        }

        if (boundaryEdgeCount == 0)
            return new BoundaryGraphAnalysis(0, 0, 0, HasOpenBoundaryChains: true);

        bool hasOpenBoundaryChains = degree.Values.Any(value => value != 2);
        int boundaryComponentCount = 0;
        var visited = new HashSet<int>();

        foreach (int start in adjacency.Keys)
        {
            if (!visited.Add(start))
                continue;

            boundaryComponentCount++;
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                foreach (int next in adjacency[current])
                {
                    if (visited.Add(next))
                        stack.Push(next);
                }
            }
        }

        return new BoundaryGraphAnalysis(
            boundaryEdgeCount,
            degree.Count,
            boundaryComponentCount,
            hasOpenBoundaryChains);
    }

    private static void CountEdge(Dictionary<long, int> edgeCounts, int a, int b)
    {
        long key = a < b
            ? ((long)a << 32) | (uint)b
            : ((long)b << 32) | (uint)a;
        edgeCounts[key] = edgeCounts.GetValueOrDefault(key, 0) + 1;
    }

    private static void AddNeighbor(
        Dictionary<int, List<int>> adjacency,
        Dictionary<int, int> degree,
        int from,
        int to)
    {
        if (!adjacency.TryGetValue(from, out var neighbors))
        {
            neighbors = new List<int>(2);
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
        degree[from] = degree.GetValueOrDefault(from) + 1;
    }
}
