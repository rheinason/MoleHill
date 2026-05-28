namespace MoleHill.Core.Grading;

internal enum CoincidentVertexZPolicy
{
    KeepFirst,
    UseLatest
}

internal static class MeshTopologyOperations
{
    public static bool TryFillSmallBranchedBoundaryLoops(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double tolerance,
        out int[] repairedFaces,
        out int repairedFaceCount,
        out int filledLoopCount)
    {
        repairedFaces = faces;
        repairedFaceCount = faceCount;
        filledLoopCount = 0;
        if (vertexCount <= 0 || faceCount <= 0)
            return false;

        Dictionary<ulong, int> edgeCounts = BuildEdgeCounts(faces, faceCount);
        var adjacency = BuildBoundaryAdjacency(edgeCounts);
        if (adjacency.Count == 0 || !adjacency.Values.Any(static neighbors => neighbors.Count > 2))
            return false;

        double resolvedTolerance = Math.Max(tolerance, 1e-9);
        var addedFaces = new List<int>();
        var filledKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach ((int branch, List<int> neighbors) in adjacency)
        {
            if (neighbors.Count <= 2)
                continue;

            foreach (int neighbor in neighbors)
            {
                if (!TryTraceBoundaryCycle(adjacency, branch, neighbor, out List<int>? cycle))
                    continue;

                int uniqueVertexCount = cycle.Count - 1;
                if (uniqueVertexCount < 3 || uniqueVertexCount > 16)
                    continue;

                string key = CreateCycleKey(cycle, uniqueVertexCount);
                if (!filledKeys.Add(key))
                    continue;

                double area = Math.Abs(ComputeSignedArea(vertices, cycle, uniqueVertexCount));
                double perimeter = ComputePerimeter(vertices, cycle, uniqueVertexCount);
                double maxFillArea = Math.Max(resolvedTolerance * resolvedTolerance * 100.0, resolvedTolerance * perimeter * 0.5);
                if (area <= 0.0 || area > maxFillArea)
                    continue;

                int anchor = cycle[0];
                for (int i = 1; i < uniqueVertexCount - 1; i++)
                {
                    int b = cycle[i];
                    int c = cycle[i + 1];
                    if (anchor == b || b == c || c == anchor)
                        continue;

                    addedFaces.Add(anchor);
                    addedFaces.Add(b);
                    addedFaces.Add(c);
                }

                filledLoopCount++;
            }
        }

        if (addedFaces.Count == 0)
        {
            filledLoopCount = 0;
            return false;
        }

        repairedFaces = new int[(faceCount * 3) + addedFaces.Count];
        Array.Copy(faces, repairedFaces, faceCount * 3);
        addedFaces.CopyTo(repairedFaces, faceCount * 3);
        repairedFaceCount = repairedFaces.Length / 3;
        return true;
    }

    public static void MergeMeshes(
        double[] firstVertices,
        int firstVertexCount,
        int[] firstFaces,
        int firstFaceCount,
        double[] secondVertices,
        int secondVertexCount,
        int[] secondFaces,
        int secondFaceCount,
        double tolerance,
        CoincidentVertexZPolicy coincidentZPolicy,
        out double[] mergedVertices,
        out int mergedVertexCount,
        out int[] mergedFaces,
        out int mergedFaceCount)
    {
        var xyList = new List<double>(firstVertexCount * 2 + secondVertexCount * 2);
        var zList = new List<double>(firstVertexCount + secondVertexCount);
        var vertHash = new SpatialVertexHash(tolerance);
        var seenFaces = new HashSet<ulong>();

        int AddVertex(double x, double y, double z)
        {
            int near = vertHash.FindNearest(xyList, x, y, tolerance);
            if (near >= 0)
            {
                if (coincidentZPolicy == CoincidentVertexZPolicy.UseLatest)
                    zList[near] = z;

                return near;
            }

            int index = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(z);
            vertHash.Insert(index, x, y);
            return index;
        }

        bool TryAddFace(List<int> faceList, int a, int b, int c)
        {
            if (a == b || b == c || c == a)
                return false;

            double ax = xyList[a * 2];
            double ay = xyList[a * 2 + 1];
            double bx = xyList[b * 2];
            double by = xyList[b * 2 + 1];
            double cx = xyList[c * 2];
            double cy = xyList[c * 2 + 1];
            double area2 = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            if (Math.Abs(area2) <= tolerance * tolerance)
                return false;

            ulong key = CreateFaceKey(a, b, c);
            if (!seenFaces.Add(key))
                return false;

            faceList.Add(a);
            faceList.Add(b);
            faceList.Add(c);
            return true;
        }

        var faceList = new List<int>((firstFaceCount + secondFaceCount) * 3);
        var firstRemap = new int[firstVertexCount];
        for (int i = 0; i < firstVertexCount; i++)
            firstRemap[i] = AddVertex(firstVertices[i * 3], firstVertices[i * 3 + 1], firstVertices[i * 3 + 2]);

        for (int i = 0; i < firstFaceCount; i++)
        {
            int a = firstRemap[firstFaces[i * 3]];
            int b = firstRemap[firstFaces[i * 3 + 1]];
            int c = firstRemap[firstFaces[i * 3 + 2]];
            TryAddFace(faceList, a, b, c);
        }

        var secondRemap = new int[secondVertexCount];
        for (int i = 0; i < secondVertexCount; i++)
            secondRemap[i] = AddVertex(secondVertices[i * 3], secondVertices[i * 3 + 1], secondVertices[i * 3 + 2]);

        for (int i = 0; i < secondFaceCount; i++)
        {
            int a = secondRemap[secondFaces[i * 3]];
            int b = secondRemap[secondFaces[i * 3 + 1]];
            int c = secondRemap[secondFaces[i * 3 + 2]];
            TryAddFace(faceList, a, b, c);
        }

        mergedVertexCount = zList.Count;
        mergedVertices = new double[mergedVertexCount * 3];
        for (int i = 0; i < mergedVertexCount; i++)
        {
            mergedVertices[i * 3] = xyList[i * 2];
            mergedVertices[i * 3 + 1] = xyList[i * 2 + 1];
            mergedVertices[i * 3 + 2] = zList[i];
        }

        mergedFaces = faceList.ToArray();
        mergedFaceCount = mergedFaces.Length / 3;
    }

    private static ulong CreateFaceKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return ((ulong)(uint)a << 42) | ((ulong)(uint)b << 21) | (uint)c;
    }

    private static Dictionary<ulong, int> BuildEdgeCounts(int[] faces, int faceCount)
    {
        var edgeCounts = new Dictionary<ulong, int>(faceCount * 2);
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[(faceIndex * 3) + 1];
            int c = faces[(faceIndex * 3) + 2];
            CountEdge(edgeCounts, a, b);
            CountEdge(edgeCounts, b, c);
            CountEdge(edgeCounts, c, a);
        }

        return edgeCounts;
    }

    private static Dictionary<int, List<int>> BuildBoundaryAdjacency(Dictionary<ulong, int> edgeCounts)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach ((ulong edge, int count) in edgeCounts)
        {
            if (count != 1)
                continue;

            int a = (int)(edge >> 32);
            int b = (int)(edge & 0xFFFFFFFFu);
            AddNeighbor(adjacency, a, b);
            AddNeighbor(adjacency, b, a);
        }

        return adjacency;
    }

    private static void CountEdge(Dictionary<ulong, int> edgeCounts, int a, int b)
    {
        ulong key = CreateEdgeKey(a, b);
        edgeCounts[key] = edgeCounts.GetValueOrDefault(key) + 1;
    }

    private static ulong CreateEdgeKey(int a, int b)
    {
        return a < b
            ? ((ulong)(uint)a << 32) | (uint)b
            : ((ulong)(uint)b << 32) | (uint)a;
    }

    private static void AddNeighbor(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out List<int>? neighbors))
        {
            neighbors = new List<int>(2);
            adjacency[from] = neighbors;
        }

        if (!neighbors.Contains(to))
            neighbors.Add(to);
    }

    private static bool TryTraceBoundaryCycle(
        Dictionary<int, List<int>> adjacency,
        int branch,
        int firstNeighbor,
        out List<int>? cycle)
    {
        cycle = null;
        var path = new List<int> { branch, firstNeighbor };
        int previous = branch;
        int current = firstNeighbor;

        while (true)
        {
            if (!adjacency.TryGetValue(current, out List<int>? neighbors))
                return false;

            if (current != branch && neighbors.Count != 2)
                return false;

            int next = -1;
            foreach (int candidate in neighbors)
            {
                if (candidate == previous)
                    continue;

                next = candidate;
                break;
            }

            if (next < 0)
                return false;

            path.Add(next);
            if (next == branch)
            {
                cycle = path;
                return true;
            }

            previous = current;
            current = next;
            if (path.Count > adjacency.Count + 1)
                return false;
        }
    }

    private static string CreateCycleKey(List<int> cycle, int uniqueVertexCount)
    {
        int[] vertices = new int[uniqueVertexCount];
        for (int i = 0; i < uniqueVertexCount; i++)
            vertices[i] = cycle[i];

        Array.Sort(vertices);
        return string.Join(",", vertices);
    }

    private static double ComputeSignedArea(double[] vertices, List<int> cycle, int uniqueVertexCount)
    {
        double area2 = 0.0;
        for (int i = 0; i < uniqueVertexCount; i++)
        {
            int current = cycle[i];
            int next = cycle[(i + 1) % uniqueVertexCount];
            double ax = vertices[current * 3];
            double ay = vertices[(current * 3) + 1];
            double bx = vertices[next * 3];
            double by = vertices[(next * 3) + 1];
            area2 += (ax * by) - (bx * ay);
        }

        return area2 * 0.5;
    }

    private static double ComputePerimeter(double[] vertices, List<int> cycle, int uniqueVertexCount)
    {
        double perimeter = 0.0;
        for (int i = 0; i < uniqueVertexCount; i++)
        {
            int current = cycle[i];
            int next = cycle[(i + 1) % uniqueVertexCount];
            double dx = vertices[current * 3] - vertices[next * 3];
            double dy = vertices[(current * 3) + 1] - vertices[(next * 3) + 1];
            perimeter += Math.Sqrt((dx * dx) + (dy * dy));
        }

        return perimeter;
    }
}
