namespace MoleHill.Core.Grading;

internal readonly record struct LoopDeviationMetrics(double MaxDistance, int MissCount);

internal readonly record struct SeamValidationResult(
    bool IsValid,
    double[] PatchBoundaryLoopXy,
    SeamGraph? SeamGraph,
    string? FailureReason);

internal static class SeamValidator
{
    public static SeamValidationResult ValidatePatchForStitching(
        double[] seamLoopXy,
        double[] patchVertices,
        int[] patchFaces,
        int patchFaceCount,
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        double tolerance)
    {
        if (!MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(
                patchVertices,
                patchFaces,
                patchFaceCount,
                tolerance,
                out double[] patchBoundaryLoopXy,
                out _))
        {
            SeamGraph graph = SeamGraph.Build(
                seamLoopXy,
                patchVertices,
                patchFaces,
                patchFaceCount,
                outsideVertices,
                outsideFaces,
                outsideFaceCount,
                tolerance);

            if (graph.HasExcessiveNearBoundaryFragmentation)
            {
                return new SeamValidationResult(
                    false,
                    Array.Empty<double>(),
                    graph,
                    $"stitched patch did not produce a single closed stitch boundary and seam-adjacent boundary fragmentation was too high (patch={graph.PatchBoundarySegmentsNearSeam}, outside={graph.TerrainBoundarySegmentsNearSeam}, matched={graph.PatchMatchedSegments}/{graph.SeamVertexCount}).");
            }

            return new SeamValidationResult(true, (double[])seamLoopXy.Clone(), graph, null);
        }

        LoopDeviationMetrics seamToPatch = ComputeLoopDeviation(seamLoopXy, patchBoundaryLoopXy, tolerance * 2.0);
        LoopDeviationMetrics patchToSeam = ComputeLoopDeviation(patchBoundaryLoopXy, seamLoopXy, tolerance * 2.0);
        SeamGraph seamGraph = SeamGraph.Build(
            seamLoopXy,
            patchVertices,
            patchFaces,
            patchFaceCount,
            outsideVertices,
            outsideFaces,
            outsideFaceCount,
            tolerance);

        if (seamToPatch.MissCount > 0 || patchToSeam.MissCount > 0)
        {
            return new SeamValidationResult(
                false,
                patchBoundaryLoopXy,
                seamGraph,
                $"stitched seam geometry check failed (split misses={seamToPatch.MissCount}, patch misses={patchToSeam.MissCount}, split max={seamToPatch.MaxDistance:F6}, patch max={patchToSeam.MaxDistance:F6}).");
        }

        return new SeamValidationResult(true, patchBoundaryLoopXy, seamGraph, null);
    }

    public static SeamValidationResult ValidatePatchSegmentMatch(
        double[] seamLoopXy,
        double[] patchVertices,
        int[] patchFaces,
        int patchFaceCount,
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        double tolerance)
    {
        SeamGraph graph = SeamGraph.Build(
            seamLoopXy,
            patchVertices,
            patchFaces,
            patchFaceCount,
            outsideVertices,
            outsideFaces,
            outsideFaceCount,
            tolerance);

        return graph.PatchHasFullSegmentMatch
            ? new SeamValidationResult(true, seamLoopXy, graph, null)
            : new SeamValidationResult(false, seamLoopXy, graph, $"patch={graph.PatchMatchedSegments}/{graph.SeamVertexCount}");
    }

    public static LoopDeviationMetrics ComputeLoopDeviation(
        double[] sourceLoopXy,
        double[] targetLoopXy,
        double tolerance)
    {
        double maxDistance = 0.0;
        int missCount = 0;
        int sourceCount = sourceLoopXy.Length / 2;
        int targetCount = targetLoopXy.Length / 2;
        if (targetCount < 2)
            return new LoopDeviationMetrics(sourceCount == 0 ? 0.0 : double.MaxValue, sourceCount);

        // Checking every source vertex against every target segment is O(source x target) and turns
        // quadratic when both loops are detailed. The index answers the exact same nearest distance
        // (same formula, and a growing query box that only stops once everything outside it is provably
        // further), so max deviation and miss count are unchanged. Small loops keep the scan.
        SegmentProximityIndex? index = SegmentProximityIndex.TryCreateForClosedLoop(targetLoopXy, targetCount);
        SegmentProximityIndex.QueryState? queryState = index != null
            ? new SegmentProximityIndex.QueryState(index.SegmentCount)
            : null;

        for (int i = 0; i < sourceCount; i++)
        {
            double px = sourceLoopXy[i * 2];
            double py = sourceLoopXy[i * 2 + 1];
            double bestDistance;
            if (index != null && queryState != null)
            {
                bestDistance = index.NearestDistance(px, py, queryState);
            }
            else
            {
                bestDistance = double.MaxValue;
                for (int j = 0; j < targetCount; j++)
                {
                    int next = (j + 1) % targetCount;
                    double distance = DistancePointToSegment(
                        px,
                        py,
                        targetLoopXy[j * 2],
                        targetLoopXy[j * 2 + 1],
                        targetLoopXy[next * 2],
                        targetLoopXy[next * 2 + 1]);
                    if (distance < bestDistance)
                        bestDistance = distance;
                }
            }

            if (bestDistance > maxDistance)
                maxDistance = bestDistance;
            if (bestDistance > tolerance)
                missCount++;
        }

        return new LoopDeviationMetrics(maxDistance, missCount);
    }

    internal static double DistancePointToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lenSq = (dx * dx) + (dy * dy);
        if (lenSq <= 1e-16)
            return Math.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay)));

        double t = (((px - ax) * dx) + ((py - ay) * dy)) / lenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        double qx = ax + (t * dx);
        double qy = ay + (t * dy);
        return Math.Sqrt(((px - qx) * (px - qx)) + ((py - qy) * (py - qy)));
    }
}
