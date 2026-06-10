using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    private static List<GradingPatch> BuildPathPatchSummaries(IReadOnlyList<PathDefinition> paths)
    {
        var patches = new List<GradingPatch>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
            patches.Add(BuildPathPatchSummary(BuildConservativePathOwnedLoop(paths[i]), paths[i], i));

        return patches;
    }

    private static GradingPatch BuildPathPatchSummary(double[] stitchLoopXy, PathDefinition path, int pathIndex)
    {
        return BuildPathPatchSummary(stitchLoopXy, stitchLoopXy, path, pathIndex);
    }

    private static string[] BuildPathStitchDiagnostics(
        int pathIndex,
        double[] shoulderLoopXy,
        double[] seamLoopXy,
        double[] patchLoopXy,
        double[] patchVertices,
        int[] patchFaces,
        int patchFaceCount,
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        double tolerance)
    {
        LoopDeviationMetrics seamToPatch = SeamValidator.ComputeLoopDeviation(seamLoopXy, patchLoopXy, tolerance * 2.0);
        LoopDeviationMetrics patchToSeam = SeamValidator.ComputeLoopDeviation(patchLoopXy, seamLoopXy, tolerance * 2.0);
        SeamGraph seamGraph = SeamGraph.Build(
            seamLoopXy,
            patchVertices,
            patchFaces,
            patchFaceCount,
            outsideVertices,
            outsideFaces,
            outsideFaceCount,
            tolerance);

        return
        [
            $"Grade Path[{pathIndex}] seam vertices: split={seamLoopXy.Length / 2}, patch={patchLoopXy.Length / 2}.",
            $"Grade Path[{pathIndex}] seam deviation: split->patch max={seamToPatch.MaxDistance:F6} ({seamToPatch.MissCount} misses), patch->split max={patchToSeam.MaxDistance:F6} ({patchToSeam.MissCount} misses).",
            BuildPathTopologyBandWidthDiagnostic(pathIndex, shoulderLoopXy, seamLoopXy),
            $"Grade Path[{pathIndex}] patch boundary edges near seam: {seamGraph.PatchBoundaryEdgesNearSeam}.",
            $"Grade Path[{pathIndex}] outside-mesh naked edges near seam: {seamGraph.TerrainBoundaryEdgesNearSeam}.",
            $"Grade Path[{pathIndex}] seam segment matches: patch={seamGraph.PatchMatchedSegments}/{seamGraph.SeamVertexCount}, outside={seamGraph.TerrainMatchedSegments}/{seamGraph.SeamVertexCount}.",
            $"Grade Path[{pathIndex}] seam-near boundary segments: patch={seamGraph.PatchBoundarySegmentsNearSeam}, outside={seamGraph.TerrainBoundarySegmentsNearSeam}."
        ];
    }

    private static string BuildPathSectionStatusDiagnostic(
        int pathIndex,
        string sideLabel,
        PathSectionResolutionStatus[] statuses,
        int repairedSectionCount)
    {
        int daylightCount = 0;
        int capCount = 0;
        int noGradeCount = 0;
        int blockedCount = 0;
        int unresolvedCount = 0;
        for (int i = 0; i < statuses.Length; i++)
        {
            switch (statuses[i])
            {
                case PathSectionResolutionStatus.ResolvedDaylight:
                    daylightCount++;
                    break;
                case PathSectionResolutionStatus.ResolvedCap:
                    capCount++;
                    break;
                case PathSectionResolutionStatus.NoGradeNeeded:
                    noGradeCount++;
                    break;
                case PathSectionResolutionStatus.Blocked:
                    blockedCount++;
                    break;
                default:
                    unresolvedCount++;
                    break;
            }
        }

        return $"Grade Path[{pathIndex}] {sideLabel} sections: daylight={daylightCount}, cap={capCount}, no-grade={noGradeCount}, blocked={blockedCount}, unresolved={unresolvedCount}, repaired={repairedSectionCount}.";
    }

    private static string? BuildPathSectionQualityWarning(
        int pathIndex,
        string sideLabel,
        PathSectionResolutionStatus[] statuses,
        int repairedSectionCount)
    {
        int blockedCount = 0;
        int unresolvedCount = 0;
        for (int i = 0; i < statuses.Length; i++)
        {
            if (statuses[i] == PathSectionResolutionStatus.Blocked)
                blockedCount++;
            else if (statuses[i] == PathSectionResolutionStatus.Unresolved)
                unresolvedCount++;
        }

        int affectedCount = blockedCount + unresolvedCount + repairedSectionCount;
        if (affectedCount == 0)
            return null;

        double affectedRatio = statuses.Length > 0
            ? affectedCount / (double)statuses.Length
            : 1.0;
        if (affectedCount <= 4 && affectedRatio < 0.03)
            return null;

        return $"Grade Path[{pathIndex}] {sideLabel} shoulder quality warning: {affectedCount} of {statuses.Length} sampled section(s) were unresolved, blocked, or repaired; shoulder/batter output may be sparse or visually discontinuous.";
    }

    private static string BuildPathTopologyBandWidthDiagnostic(int pathIndex, double[] shoulderLoopXy, double[] seamLoopXy)
    {
        ComputeClosedLoopDistanceStats(shoulderLoopXy, seamLoopXy, out double shoulderToSeamMin, out double shoulderToSeamMax);
        ComputeClosedLoopDistanceStats(seamLoopXy, shoulderLoopXy, out double seamToShoulderMin, out double seamToShoulderMax);
        return $"Grade Path[{pathIndex}] topology band width: shoulder->seam min={shoulderToSeamMin:F6}, max={shoulderToSeamMax:F6}; seam->shoulder min={seamToShoulderMin:F6}, max={seamToShoulderMax:F6}.";
    }

    private static void ComputeClosedLoopDistanceStats(
        double[] sourceLoopXy,
        double[] targetLoopXy,
        out double minDistance,
        out double maxDistance)
    {
        minDistance = double.MaxValue;
        maxDistance = 0.0;
        int sourceCount = sourceLoopXy.Length / 2;
        int targetCount = targetLoopXy.Length / 2;
        for (int i = 0; i < sourceCount; i++)
        {
            double px = sourceLoopXy[i * 2];
            double py = sourceLoopXy[i * 2 + 1];
            double bestDistance = double.MaxValue;
            if (TryFindClosestClosedLoopLocation(targetLoopXy, targetCount, px, py, out ClosestClosedLoopLocation closest))
                bestDistance = closest.Distance;

            if (bestDistance < minDistance)
                minDistance = bestDistance;
            if (bestDistance > maxDistance)
                maxDistance = bestDistance;
        }

        if (minDistance == double.MaxValue)
            minDistance = 0.0;
    }

    private static double[] BuildConservativePathOwnedLoop(PathDefinition path)
    {
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        for (int i = 0; i < path.VertexCount; i++)
        {
            double x = path.XyVertices[i * 2];
            double y = path.XyVertices[i * 2 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double halfWidth = path.Width * 0.5;
        double shoulderAllowance = path.MaxDistance > 0.0
            ? path.MaxDistance
            : Math.Max(path.Width * 2.0, halfWidth);
        double expansion = halfWidth + shoulderAllowance;
        return new double[]
        {
            minX - expansion, minY - expansion,
            maxX + expansion, minY - expansion,
            maxX + expansion, maxY + expansion,
            minX - expansion, maxY + expansion
        };
    }

    private static GradingPatch BuildPathPatchSummary(double[] ownedRegionLoopXy, double[] stitchLoopXy, PathDefinition path, int pathIndex)
    {
        return new GradingPatch
        {
            OwnerKey = $"path:{pathIndex}",
            Kind = GradingPatchKind.Path,
            Priority = pathIndex,
            OwnedRegionLoopXy = (double[])ownedRegionLoopXy.Clone(),
            DaylightLoopXy = (double[])stitchLoopXy.Clone(),
            StitchLoopXy = (double[])stitchLoopXy.Clone(),
            DirtyBounds = GradingPatch.ComputeBounds(ownedRegionLoopXy),
            UsesFallbackBand = false
        };
    }
}
