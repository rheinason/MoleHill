namespace MoleHill.Core.Engine;

public static class MeshConstraintTools
{
    public static void AddBoundarySegments(
        List<(int a, int b)> segments,
        HashSet<long> segmentKeys,
        int[] faces,
        int faceCount)
    {
        // Presized for the ~1.5 edges per face of a closed triangulation: grown from 8 it rehashed its
        // way through ~170k entries on a 111k-face terrain. Enumeration below is insertion order either
        // way, so the boundary segment order the outline chaining depends on is unchanged.
        var edgeFaceCount = new Dictionary<long, int>(Math.Max(8, faceCount * 3 / 2 + 16), IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];

            CountEdge(edgeFaceCount, a, b);
            CountEdge(edgeFaceCount, b, c);
            CountEdge(edgeFaceCount, c, a);
        }

        foreach (var (edgeKey, count) in edgeFaceCount)
        {
            if (count != 1)
                continue;

            int a = (int)(edgeKey >> 32);
            int b = (int)(edgeKey & 0xFFFFFFFFL);
            TryAddSegment(segments, segmentKeys, a, b);
        }
    }

    public static bool TryAddSegment(
        List<(int a, int b)> segments,
        HashSet<long> segmentKeys,
        int a,
        int b)
    {
        if (a == b)
            return false;

        long edgeKey = GetEdgeKey(a, b);
        if (!segmentKeys.Add(edgeKey))
            return false;

        segments.Add((a, b));
        return true;
    }

    public static bool ConstraintsWereDropped(TriangulationWarningFlags flags)
    {
        return (flags & TriangulationWarningFlags.DroppedSegments) != 0;
    }

    public static bool QualityWasDropped(TriangulationWarningFlags flags)
    {
        return (flags & TriangulationWarningFlags.DroppedQualityConstraints) != 0;
    }

    private static void CountEdge(Dictionary<long, int> edgeFaceCount, int a, int b)
    {
        // One hash lookup instead of a read and a write.
        System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(edgeFaceCount, GetEdgeKey(a, b), out _)++;
    }

    private static long GetEdgeKey(int a, int b)
    {
        return a < b
            ? ((long)a << 32) | (uint)b
            : ((long)b << 32) | (uint)a;
    }
}
