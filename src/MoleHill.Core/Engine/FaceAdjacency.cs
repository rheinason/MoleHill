namespace MoleHill.Core.Engine;

/// <summary>
/// Face-to-face adjacency across shared edges, as a flat <c>faceCount * 3</c> array: entry
/// <c>(face * 3) + edge</c> is the face on the other side of the edge running from the face's vertex
/// <c>edge</c> to vertex <c>(edge + 1) % 3</c>, or -1 where the edge is naked.
/// </summary>
/// <remarks>
/// Shared deliberately. <see cref="Analysis.WaterflowTracer"/> and the drainage basin analyzer both walk
/// the dual graph, and the two must agree exactly on the degenerate cases — which edges are naked, and
/// what happens at a non-manifold edge — or a traced flow path would cross a catchment boundary that
/// claims water cannot go there. One builder is what makes that impossible rather than merely unlikely.
/// </remarks>
internal static class FaceAdjacency
{
    /// <summary>
    /// Builds the neighbour array. Degenerate edges (a repeated index) and indices outside
    /// <paramref name="vertexCount"/> are skipped, leaving those slots naked.
    /// </summary>
    public static int[] Build(
        IReadOnlyList<int> faces,
        int faceCount,
        int vertexCount,
        CancellationProbe? probe = null)
    {
        if (faces == null)
            throw new ArgumentNullException(nameof(faces));

        CancellationProbe cancellation = probe ?? CancellationProbe.None;
        var neighbors = new int[faceCount * 3];
        Array.Fill(neighbors, -1);

        // Presized: a closed triangle mesh has about 1.5 edges per face, so an unsized dictionary
        // rehashes its way up from 0 to that on every call.
        var edges = new Dictionary<EdgeKey, (int Face, int Edge)>(Math.Max(16, faceCount * 2));

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            cancellation.ThrowIfCancelledOften();
            for (int edge = 0; edge < 3; edge++)
            {
                int a = faces[(faceIndex * 3) + edge];
                int b = faces[(faceIndex * 3) + ((edge + 1) % 3)];
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || a == b)
                    continue;

                var key = new EdgeKey(a, b);
                if (!edges.TryGetValue(key, out var previous))
                {
                    edges.Add(key, (faceIndex, edge));
                    continue;
                }

                // Non-manifold edges are treated as boundaries rather than choosing an arbitrary
                // third face. The first two faces still form a deterministic pair.
                if (neighbors[(previous.Face * 3) + previous.Edge] < 0)
                {
                    neighbors[(previous.Face * 3) + previous.Edge] = faceIndex;
                    neighbors[(faceIndex * 3) + edge] = previous.Face;
                }
            }
        }

        return neighbors;
    }

    /// <summary>
    /// Unordered vertex pair. A record struct rather than a packed <c>long</c> because the default
    /// <c>long</c> hash (<c>lo ^ hi</c>) collapses adjacent mesh indices into a handful of buckets; see
    /// the edge-key note in <c>CLAUDE.md</c>.
    /// </summary>
    internal readonly record struct EdgeKey
    {
        public EdgeKey(int a, int b)
        {
            A = Math.Min(a, b);
            B = Math.Max(a, b);
        }

        public int A { get; }

        public int B { get; }
    }
}
