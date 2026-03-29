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
        var edgeCounts = new Dictionary<long, int>(Math.Max(faceCount * 2, 8));
        var edgeOrder = new List<long>(Math.Max(faceCount * 2, 8));

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];

            CountEdge(edgeCounts, edgeOrder, a, b);
            CountEdge(edgeCounts, edgeOrder, b, c);
            CountEdge(edgeCounts, edgeOrder, c, a);
        }

        var edges = new int[edgeOrder.Count * 2];
        var nakedEdges = new List<int>(edgeOrder.Count * 2);

        for (int i = 0; i < edgeOrder.Count; i++)
        {
            long edgeKey = edgeOrder[i];
            int a = (int)(edgeKey >> 32);
            int b = (int)(edgeKey & 0xFFFFFFFFL);
            edges[i * 2] = a;
            edges[i * 2 + 1] = b;

            if (edgeCounts[edgeKey] == 1)
            {
                nakedEdges.Add(a);
                nakedEdges.Add(b);
            }
        }

        return new EdgeTopology(edges, edgeOrder.Count, nakedEdges.ToArray(), nakedEdges.Count / 2);
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
}
