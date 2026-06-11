using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal static class MeshBoundaryLoopBuilder
{
    public static bool TryBuildBoundaryLoop(
        double[] vertices,
        int[] faces,
        int faceCount,
        out double[] boundaryXy,
        out int boundaryVertexCount)
    {
        boundaryXy = Array.Empty<double>();
        boundaryVertexCount = 0;

        if (!TryBuildBoundaryVertexOrder(faces, faceCount, out List<int> order))
            return false;

        boundaryVertexCount = order.Count;
        boundaryXy = new double[boundaryVertexCount * 2];
        for (int i = 0; i < boundaryVertexCount; i++)
        {
            int vertexIndex = order[i];
            boundaryXy[i * 2] = vertices[vertexIndex * 3];
            boundaryXy[i * 2 + 1] = vertices[vertexIndex * 3 + 1];
        }

        return true;
    }

    public static bool TryBuildBoundaryLoop(
        double[] vertices,
        int[] faces,
        int faceCount,
        double tolerance,
        out double[] boundaryXy,
        out double[] boundaryZ)
    {
        boundaryXy = Array.Empty<double>();
        boundaryZ = Array.Empty<double>();

        if (!TryBuildBoundaryVertexOrder(faces, faceCount, out List<int> order))
            return false;

        boundaryXy = new double[order.Count * 2];
        boundaryZ = new double[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            int vertexIndex = order[i];
            boundaryXy[i * 2] = vertices[vertexIndex * 3];
            boundaryXy[i * 2 + 1] = vertices[vertexIndex * 3 + 1];
            boundaryZ[i] = vertices[vertexIndex * 3 + 2];
        }

        return boundaryXy.Length >= 6 &&
               Math.Abs(ClipperGeometry.SignedArea(boundaryXy)) > tolerance * tolerance;
    }

    /// <summary>
    /// Extracts every closed boundary loop of a face set as ordered original vertex indices. Unlike
    /// the single-loop overloads, this walks ALL naked edges and returns each separate component, so
    /// a face set with several disjoint holes (or an outer boundary plus holes) yields one loop each.
    /// Returns false if any naked edge has a non-manifold (degree != 2) junction.
    /// </summary>
    public static bool TryBuildBoundaryLoopsIndexed(int[] faces, int faceCount, out List<int[]> loops)
    {
        loops = new List<int[]>();

        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[f * 3 + 1];
            int c = faces[f * 3 + 2];
            IncrementEdge(edgeFaceCount, a, b);
            IncrementEdge(edgeFaceCount, b, c);
            IncrementEdge(edgeFaceCount, c, a);
        }

        var adjacency = new Dictionary<int, List<int>>();
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
        }

        if (adjacency.Count == 0)
            return false;

        foreach (var neighbors in adjacency.Values)
        {
            if (neighbors.Count != 2)
                return false;
        }

        var visited = new HashSet<int>();
        foreach (int seed in adjacency.Keys)
        {
            if (visited.Contains(seed))
                continue;

            var loop = new List<int>();
            int previous = -1;
            int current = seed;
            while (true)
            {
                loop.Add(current);
                visited.Add(current);
                var neighbors = adjacency[current];
                int next = neighbors[0] != previous ? neighbors[0] : neighbors[1];
                previous = current;
                current = next;

                if (current == seed)
                    break;

                if (loop.Count > adjacency.Count)
                    return false;
            }

            if (loop.Count >= 3)
                loops.Add(loop.ToArray());
        }

        return loops.Count > 0;
    }

    private static bool TryBuildBoundaryVertexOrder(int[] faces, int faceCount, out List<int> order)
    {
        order = new List<int>();

        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[f * 3 + 1];
            int c = faces[f * 3 + 2];
            IncrementEdge(edgeFaceCount, a, b);
            IncrementEdge(edgeFaceCount, b, c);
            IncrementEdge(edgeFaceCount, c, a);
        }

        var adjacency = new Dictionary<int, List<int>>();
        int segmentCount = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
            segmentCount++;
        }

        if (segmentCount < 3 || adjacency.Count == 0)
            return false;

        foreach (var neighbors in adjacency.Values)
        {
            if (neighbors.Count != 2)
                return false;
        }

        int start = adjacency.Keys.Min();
        int previous = -1;
        int current = start;

        while (true)
        {
            order.Add(current);
            var neighbors = adjacency[current];
            int next = neighbors[0] != previous ? neighbors[0] : neighbors[1];
            previous = current;
            current = next;

            if (current == start)
                break;

            if (order.Count > adjacency.Count)
                return false;
        }

        return order.Count >= 3 && order.Count == adjacency.Count;
    }

    private static void IncrementEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }

    private static void AddBoundaryNeighbor(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out var list))
        {
            list = new List<int>(2);
            adjacency[from] = list;
        }

        list.Add(to);
    }
}
