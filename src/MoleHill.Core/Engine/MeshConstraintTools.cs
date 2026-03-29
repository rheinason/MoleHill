namespace MoleHill.Core.Engine;

public static class MeshConstraintTools
{
    public static void AddBoundarySegments(
        List<(int a, int b)> segments,
        HashSet<long> segmentKeys,
        int[] faces,
        int faceCount)
    {
        var edgeFaceCount = new Dictionary<long, int>();
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
        long edgeKey = GetEdgeKey(a, b);
        edgeFaceCount[edgeKey] = edgeFaceCount.GetValueOrDefault(edgeKey, 0) + 1;
    }

    private static long GetEdgeKey(int a, int b)
    {
        return a < b
            ? ((long)a << 32) | (uint)b
            : ((long)b << 32) | (uint)a;
    }
}
