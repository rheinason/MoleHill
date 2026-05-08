using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal sealed class SeamGraph
{
    public required double[] SeamLoopXy { get; init; }

    public required int PatchBoundaryEdgesNearSeam { get; init; }

    public required int TerrainBoundaryEdgesNearSeam { get; init; }

    public required int PatchMatchedSegments { get; init; }

    public required int TerrainMatchedSegments { get; init; }

    public required int PatchBoundarySegmentsNearSeam { get; init; }

    public required int TerrainBoundarySegmentsNearSeam { get; init; }

    public int SeamVertexCount => SeamLoopXy.Length / 2;

    public bool PatchHasFullSegmentMatch => PatchMatchedSegments == SeamVertexCount;

    public bool TerrainHasFullSegmentMatch => TerrainMatchedSegments == SeamVertexCount;

    public static SeamGraph Build(
        double[] seamLoopXy,
        double[] patchVertices,
        int[] patchFaces,
        int patchFaceCount,
        double[] terrainVertices,
        int[] terrainFaces,
        int terrainFaceCount,
        double tolerance)
    {
        return new SeamGraph
        {
            SeamLoopXy = seamLoopXy,
            PatchBoundaryEdgesNearSeam = CountBoundaryEdgesNearLoop(patchVertices, patchFaces, patchFaceCount, seamLoopXy, tolerance * 4.0),
            TerrainBoundaryEdgesNearSeam = CountBoundaryEdgesNearLoop(terrainVertices, terrainFaces, terrainFaceCount, seamLoopXy, tolerance * 4.0),
            PatchMatchedSegments = CountMatchedBoundarySegments(seamLoopXy, patchVertices, patchFaces, patchFaceCount, tolerance, out int patchNearSegments),
            TerrainMatchedSegments = CountMatchedBoundarySegments(seamLoopXy, terrainVertices, terrainFaces, terrainFaceCount, tolerance, out int terrainNearSegments),
            PatchBoundarySegmentsNearSeam = patchNearSegments,
            TerrainBoundarySegmentsNearSeam = terrainNearSegments
        };
    }

    private static int CountBoundaryEdgesNearLoop(
        double[] vertices,
        int[] faces,
        int faceCount,
        double[] loopXy,
        double distanceTolerance)
    {
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[f * 3 + 1];
            int c = faces[f * 3 + 2];
            Increment(edgeFaceCount, a, b);
            Increment(edgeFaceCount, b, c);
            Increment(edgeFaceCount, c, a);
        }

        int boundaryNearLoop = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double mx = (vertices[a * 3] + vertices[b * 3]) * 0.5;
            double my = (vertices[a * 3 + 1] + vertices[b * 3 + 1]) * 0.5;
            if (DistanceToLoop(mx, my, loopXy) <= distanceTolerance)
                boundaryNearLoop++;
        }

        return boundaryNearLoop;
    }

    private static int CountMatchedBoundarySegments(
        double[] seamLoopXy,
        double[] meshVertices,
        int[] meshFaces,
        int meshFaceCount,
        double tolerance,
        out int boundarySegmentsNearSeam)
    {
        int seamVertexCount = seamLoopXy.Length / 2;
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < meshFaceCount; f++)
        {
            int a = meshFaces[f * 3];
            int b = meshFaces[f * 3 + 1];
            int c = meshFaces[f * 3 + 2];
            Increment(edgeFaceCount, a, b);
            Increment(edgeFaceCount, b, c);
            Increment(edgeFaceCount, c, a);
        }

        var boundarySegments = new List<(double Ax, double Ay, double Bx, double By)>();
        boundarySegmentsNearSeam = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double ax = meshVertices[a * 3];
            double ay = meshVertices[a * 3 + 1];
            double bx = meshVertices[b * 3];
            double by = meshVertices[b * 3 + 1];
            double mx = (ax + bx) * 0.5;
            double my = (ay + by) * 0.5;
            if (DistanceToLoop(mx, my, seamLoopXy) <= tolerance * 4.0)
            {
                boundarySegmentsNearSeam++;
                boundarySegments.Add((ax, ay, bx, by));
            }
        }

        int matchedSegments = 0;
        for (int i = 0; i < seamVertexCount; i++)
        {
            int next = (i + 1) % seamVertexCount;
            double sax = seamLoopXy[i * 2];
            double say = seamLoopXy[i * 2 + 1];
            double sbx = seamLoopXy[next * 2];
            double sby = seamLoopXy[next * 2 + 1];
            if (BoundarySegmentsCoverSeamSegment(sax, say, sbx, sby, boundarySegments, tolerance))
                matchedSegments++;
        }

        return matchedSegments;
    }

    private static bool BoundarySegmentsCoverSeamSegment(
        double sax,
        double say,
        double sbx,
        double sby,
        IReadOnlyList<(double Ax, double Ay, double Bx, double By)> boundarySegments,
        double tolerance)
    {
        double segDx = sbx - sax;
        double segDy = sby - say;
        double segLenSq = (segDx * segDx) + (segDy * segDy);
        if (segLenSq <= tolerance * tolerance)
            return false;

        double tolSq = tolerance * tolerance;
        var intervals = new List<(double Start, double End)>();
        for (int i = 0; i < boundarySegments.Count; i++)
        {
            var edge = boundarySegments[i];
            double aDistanceSq = DistanceSquaredPointToSegment(edge.Ax, edge.Ay, sax, say, sbx, sby);
            double bDistanceSq = DistanceSquaredPointToSegment(edge.Bx, edge.By, sax, say, sbx, sby);
            if (aDistanceSq > tolSq || bDistanceSq > tolSq)
                continue;

            double ta = (((edge.Ax - sax) * segDx) + ((edge.Ay - say) * segDy)) / segLenSq;
            double tb = (((edge.Bx - sax) * segDx) + ((edge.By - say) * segDy)) / segLenSq;
            if (ta > tb)
                (ta, tb) = (tb, ta);
            if (tb < -1e-9 || ta > 1.0 + 1e-9)
                continue;

            ta = Math.Clamp(ta, 0.0, 1.0);
            tb = Math.Clamp(tb, 0.0, 1.0);
            if (tb - ta <= 1e-9)
                continue;

            intervals.Add((ta, tb));
        }

        if (intervals.Count == 0)
            return false;

        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        double normalizedTolerance = Math.Min(0.25, Math.Max(1e-9, tolerance / Math.Sqrt(segLenSq)));
        double coveredUntil = 0.0;
        foreach (var interval in intervals)
        {
            if (interval.Start > coveredUntil + normalizedTolerance)
                return false;

            coveredUntil = Math.Max(coveredUntil, interval.End);
            if (coveredUntil >= 1.0 - normalizedTolerance)
                return true;
        }

        return coveredUntil >= 1.0 - normalizedTolerance;
    }

    private static void Increment(Dictionary<long, int> edgeFaceCount, int a, int b)
    {
        long key = IndexedMeshTools.GetEdgeKey(a, b);
        edgeFaceCount.TryGetValue(key, out int value);
        edgeFaceCount[key] = value + 1;
    }

    private static double DistanceToLoop(double px, double py, double[] loopXy)
    {
        int count = loopXy.Length / 2;
        if (count < 2)
            return double.MaxValue;

        double best = double.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            double ax = loopXy[i * 2];
            double ay = loopXy[i * 2 + 1];
            double bx = loopXy[next * 2];
            double by = loopXy[next * 2 + 1];
            double distance = DistancePointToSegment(px, py, ax, ay, bx, by);
            if (distance < best)
                best = distance;
        }

        return best;
    }

    private static double DistancePointToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        return Math.Sqrt(DistanceSquaredPointToSegment(px, py, ax, ay, bx, by));
    }

    private static double DistanceSquaredPointToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lenSq = (dx * dx) + (dy * dy);
        if (lenSq <= 1e-16)
            return DistanceSquaredXY(px, py, ax, ay);

        double t = (((px - ax) * dx) + ((py - ay) * dy)) / lenSq;
        t = Math.Max(0.0, Math.Min(1.0, t));
        double qx = ax + (t * dx);
        double qy = ay + (t * dy);
        return DistanceSquaredXY(px, py, qx, qy);
    }

    private static double DistanceSquaredXY(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return (dx * dx) + (dy * dy);
    }
}
