namespace MoleHill.Core.Engine;

internal static class IndexedMeshTools
{
    internal sealed class EdgeTopology
    {
        public int[] Edges { get; }

        public int EdgeCount { get; }

        public int[] NakedEdges { get; }

        public int NakedEdgeCount { get; }

        public EdgeTopology(int[] edges, int edgeCount, int[] nakedEdges, int nakedEdgeCount)
        {
            Edges = edges;
            EdgeCount = edgeCount;
            NakedEdges = nakedEdges;
            NakedEdgeCount = nakedEdgeCount;
        }
    }

    internal sealed class CompactionResult
    {
        public int[] Faces { get; }

        public int FaceCount { get; }

        public int[] OldToNew { get; }

        public int[] NewToOld { get; }

        public int VertexCount { get; }

        public CompactionResult(int[] faces, int faceCount, int[] oldToNew, int[] newToOld, int vertexCount)
        {
            Faces = faces;
            FaceCount = faceCount;
            OldToNew = oldToNew;
            NewToOld = newToOld;
            VertexCount = vertexCount;
        }
    }

    public static EdgeTopology BuildEdgeTopology(int[] faces, int faceCount)
    {
        if (faceCount == 0)
            return new EdgeTopology(Array.Empty<int>(), 0, Array.Empty<int>(), 0);

        // Build a flat array of all edge keys (3 per face), sort it, then do a linear scan.
        // This avoids Dictionary<long, int> whose default hash (a ^ b) collapses to a narrow
        // range for small vertex indices, causing severe collision chains at scale.
        int totalEdgeRefs = faceCount * 3;
        var sortedKeys = new long[totalEdgeRefs];
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            sortedKeys[f * 3]     = GetEdgeKey(a, b);
            sortedKeys[f * 3 + 1] = GetEdgeKey(b, c);
            sortedKeys[f * 3 + 2] = GetEdgeKey(c, a);
        }
        Array.Sort(sortedKeys);

        // Count unique edges and naked edges in one linear pass.
        int uniqueEdges = 0;
        int i = 0;
        while (i < totalEdgeRefs)
        {
            long key = sortedKeys[i];
            int run = 1;
            while (i + run < totalEdgeRefs && sortedKeys[i + run] == key)
                run++;
            uniqueEdges++;
            i += run;
        }

        var edges = new int[uniqueEdges * 2];
        var nakedEdges = new List<int>();
        int edgeIdx = 0;
        i = 0;
        while (i < totalEdgeRefs)
        {
            long key = sortedKeys[i];
            int run = 1;
            while (i + run < totalEdgeRefs && sortedKeys[i + run] == key)
                run++;

            int a = (int)(key >> 32);
            int b = (int)(key & 0xFFFFFFFFL);
            edges[edgeIdx * 2]     = a;
            edges[edgeIdx * 2 + 1] = b;
            edgeIdx++;

            if (run == 1)
            {
                nakedEdges.Add(a);
                nakedEdges.Add(b);
            }
            i += run;
        }

        return new EdgeTopology(edges, uniqueEdges, nakedEdges.ToArray(), nakedEdges.Count / 2);
    }

    public static CompactionResult Compact(int vertexCount, int[] faces, int faceCount)
    {
        var oldToNew = new int[vertexCount];
        Array.Fill(oldToNew, -1);

        var newToOld = new int[Math.Min(vertexCount, faceCount * 3)];
        var remappedFaces = new int[faceCount * 3];
        int nextVertex = 0;

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int oldIndex = faces[faceIndex * 3 + corner];
                if ((uint)oldIndex >= (uint)vertexCount)
                    throw new ArgumentOutOfRangeException(nameof(faces), $"Face references invalid vertex index {oldIndex}.");

                int newIndex = oldToNew[oldIndex];
                if (newIndex < 0)
                {
                    newIndex = nextVertex++;
                    oldToNew[oldIndex] = newIndex;
                    newToOld[newIndex] = oldIndex;
                }

                remappedFaces[faceIndex * 3 + corner] = newIndex;
            }
        }

        if (nextVertex != newToOld.Length)
            Array.Resize(ref newToOld, nextVertex);

        return new CompactionResult(remappedFaces, faceCount, oldToNew, newToOld, nextVertex);
    }

    public static double[] CompactDoubleData(double[] values, int stride, int[] newToOld, int newVertexCount)
    {
        var compact = new double[newVertexCount * stride];
        for (int newIndex = 0; newIndex < newVertexCount; newIndex++)
        {
            int oldIndex = newToOld[newIndex];
            Array.Copy(values, oldIndex * stride, compact, newIndex * stride, stride);
        }
        return compact;
    }

    public static int[] FlattenSegments(IReadOnlyList<(int a, int b)> segments)
    {
        var flat = new int[segments.Count * 2];
        for (int i = 0; i < segments.Count; i++)
        {
            flat[i * 2] = segments[i].a;
            flat[i * 2 + 1] = segments[i].b;
        }
        return flat;
    }

    private static void CountEdge(Dictionary<long, int> edgeCounts, List<long> edgeOrder, int a, int b)
    {
        long edgeKey = GetEdgeKey(a, b);
        if (edgeCounts.TryGetValue(edgeKey, out int count))
        {
            edgeCounts[edgeKey] = count + 1;
        }
        else
        {
            edgeCounts[edgeKey] = 1;
            edgeOrder.Add(edgeKey);
        }
    }

    internal static long GetEdgeKey(int a, int b)
    {
        return a < b
            ? ((long)a << 32) | (uint)b
            : ((long)b << 32) | (uint)a;
    }

    /// <summary>
    /// Use instead of the default long comparer for edge key dictionaries.
    /// The default long.GetHashCode() = (int)value ^ (int)(value >> 32) = a ^ b,
    /// which collapses to a narrow range for small vertex indices, causing O(n) chains.
    /// HashCode.Combine spreads bits much better.
    /// </summary>
    internal sealed class EdgeKeyComparer : IEqualityComparer<long>
    {
        internal static readonly EdgeKeyComparer Instance = new();
        public bool Equals(long x, long y) => x == y;
        public int GetHashCode(long key) => HashCode.Combine((int)(key >> 32), (int)(key & 0xFFFFFFFF));
    }
}
