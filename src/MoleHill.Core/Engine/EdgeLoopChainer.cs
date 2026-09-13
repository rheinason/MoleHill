namespace MoleHill.Core.Engine;

/// <summary>
/// Chains directed edges into vertex sequences — closed loops where the edges close, open chains where
/// they do not.
/// </summary>
/// <remarks>
/// Deliberately *directed*, which is what keeps this small. A basin boundary is collected by walking the
/// faces of the basin and emitting each outward edge in that face's own winding, so every boundary vertex
/// has as many outgoing edges as incoming ones and a loop is found by following the direction. The
/// undirected case — chaining an arbitrary edge set, deciding what a junction means — is
/// <see cref="FeaturePolylineGraph"/>'s problem, and it needs corner classification and arc-length
/// parameters that a boundary polygon has no use for. Two small correct things, not one general one.
/// </remarks>
internal static class EdgeLoopChainer
{
    /// <summary>
    /// Chains <paramref name="edges"/>, a flat <c>[a0, b0, a1, b1, …]</c> array of directed edges, into
    /// vertex sequences. A closed loop does not repeat its first vertex at the end.
    /// </summary>
    public static List<int[]> ChainDirected(IReadOnlyList<int> edges, int edgeCount, int vertexCount)
    {
        var chains = new List<int[]>();
        if (edgeCount <= 0)
            return chains;

        // Flat CSR of outgoing edges per vertex rather than a dictionary of lists: this runs once per
        // basin, and a dictionary of lists over a 180k-face terrain's boundaries allocates more than the
        // whole basin graph does.
        var outgoingStart = new int[vertexCount + 1];
        for (int edge = 0; edge < edgeCount; edge++)
        {
            int from = edges[edge * 2];
            if ((uint)from < (uint)vertexCount)
                outgoingStart[from + 1]++;
        }

        for (int vertex = 0; vertex < vertexCount; vertex++)
            outgoingStart[vertex + 1] += outgoingStart[vertex];

        var cursor = (int[])outgoingStart.Clone();
        var outgoing = new int[edgeCount];
        for (int edge = 0; edge < edgeCount; edge++)
        {
            int from = edges[edge * 2];
            if ((uint)from < (uint)vertexCount)
                outgoing[cursor[from]++] = edge;
        }

        var used = new bool[edgeCount];
        var walked = new int[vertexCount + 1];
        Array.Fill(walked, -1);
        var chain = new List<int>();

        for (int seed = 0; seed < edgeCount; seed++)
        {
            if (used[seed])
                continue;

            chain.Clear();
            int current = seed;
            int startVertex = edges[seed * 2];

            while (true)
            {
                used[current] = true;
                chain.Add(edges[current * 2]);
                int to = edges[(current * 2) + 1];

                if (to == startVertex)
                    break;

                int next = TakeUnusedOutgoing(outgoing, outgoingStart, walked, used, to, vertexCount);
                if (next < 0)
                {
                    // Ran out before closing: an open chain. Keep its last vertex, which a loop would
                    // have folded back onto its first.
                    chain.Add(to);
                    break;
                }

                current = next;
            }

            if (chain.Count >= 2)
                chains.Add(chain.ToArray());
        }

        return chains;
    }

    /// <summary>
    /// Next unused edge leaving <paramref name="vertex"/>. <paramref name="walked"/> remembers how far
    /// this vertex's slice has already been consumed, so a pinch point where many loops meet costs one
    /// scan across all of them rather than one per visit.
    /// </summary>
    private static int TakeUnusedOutgoing(
        int[] outgoing,
        int[] outgoingStart,
        int[] walked,
        bool[] used,
        int vertex,
        int vertexCount)
    {
        if ((uint)vertex >= (uint)vertexCount)
            return -1;

        int start = outgoingStart[vertex];
        int end = outgoingStart[vertex + 1];
        int index = walked[vertex] < start ? start : walked[vertex];

        for (; index < end; index++)
        {
            int edge = outgoing[index];
            if (used[edge])
                continue;

            walked[vertex] = index;
            return edge;
        }

        walked[vertex] = end;
        return -1;
    }
}
