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
    private static double[] BuildConservativePathOwnedLoop(PathDefinition path)
    {
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        for (int i = 0; i < path.VertexCount; i++)
        {
            Include(path.XyVertices[i * 2], path.XyVertices[i * 2 + 1]);
            if (path.HasVariableWidth)
            {
                Include(path.LeftEdgeXy![i * 2], path.LeftEdgeXy[(i * 2) + 1]);
                Include(path.RightEdgeXy![i * 2], path.RightEdgeXy[(i * 2) + 1]);
            }
        }

        double halfWidth = path.MaximumHalfWidth();
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

        void Include(double x, double y)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
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
