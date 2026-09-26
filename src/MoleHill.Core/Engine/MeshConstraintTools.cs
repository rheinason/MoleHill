namespace MoleHill.Core.Engine;

public static class MeshConstraintTools
{
    public static void AddBoundarySegments(
        List<(int a, int b)> segments,
        HashSet<long> segmentKeys,
        int[] faces,
        int faceCount)
    {
        // Enumerated in first-use order, which the outline chaining depends on.
        Dictionary<long, int> edgeFaceCount = IndexedMeshTools.CountFaceEdges(faces, faceCount);

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

        long edgeKey = IndexedMeshTools.GetEdgeKey(a, b);
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
}
