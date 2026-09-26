using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private static double ComputePadDaylightReach(
        TerrainFaceGrid TerrainFaceGrid,
        double boundaryX,
        double boundaryY,
        double boundaryZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double fallbackReach,
        double maxDistance)
    {
        if (slopeRatio <= 1e-12 ||
            Math.Abs(branchSign) <= 1e-12 ||
            !double.IsFinite(fallbackReach) ||
            fallbackReach <= ScaleAwareTolerance.LengthFloor(fallbackReach))
        {
            return Math.Max(0.0, fallbackReach);
        }

        double searchDistance = maxDistance > 0.0
            ? maxDistance
            : Math.Max(fallbackReach * 4.0, TerrainFaceGrid.BoundsDiagonal);
        if (searchDistance <= ScaleAwareTolerance.LengthFloor(searchDistance))
            return Math.Max(0.0, fallbackReach);

        if (TryFindPadDaylightReachByTriangleIntervals(
                TerrainFaceGrid,
                boundaryX,
                boundaryY,
                boundaryZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                searchDistance,
                out double daylightReach,
                out double bestApproachReach))
        {
            return daylightReach;
        }

        if (TryFindPadDaylightReach(
                TerrainFaceGrid,
                boundaryX,
                boundaryY,
                boundaryZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                searchDistance,
                out daylightReach,
                out bestApproachReach))
        {
            return daylightReach;
        }

        return Math.Min(fallbackReach, searchDistance);
    }

    private static bool TryFindPadDaylightReachByTriangleIntervals(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double maxReach,
        out double daylightReach,
        out double bestApproachReach)
    {
        daylightReach = 0.0;
        bestApproachReach = 0.0;
        if (maxReach <= ScaleAwareTolerance.LengthFloor(maxReach))
            return false;

        return TerrainFaceGrid.TryFindRayDaylightReach(
            edgeX,
            edgeY,
            edgeZ,
            dirX,
            dirY,
            slopeRatio,
            branchSign,
            maxReach,
            out daylightReach,
            out bestApproachReach);
    }

    private static bool TryFindPadDaylightReach(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double maxReach,
        out double daylightReach,
        out double bestApproachReach)
    {
        daylightReach = 0.0;
        bestApproachReach = 0.0;
        if (maxReach <= ScaleAwareTolerance.LengthFloor(maxReach))
            return false;

        double diffTolerance = ScaleAwareTolerance.ResolveLength(maxReach * 1e-6, maxReach);
        double step = maxReach / 48.0;
        int sampleCount = Math.Max(1, (int)Math.Ceiling(maxReach / step));
        double startDiff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, 0.0);
        double bestAbsDiff = Math.Abs(startDiff);
        double prevReach;
        double prevDiff;
        int sampleIndex;

        if (Math.Abs(startDiff) > diffTolerance)
        {
            prevReach = 0.0;
            prevDiff = startDiff;
            sampleIndex = 1;
        }
        else
        {
            double firstReach = Math.Min(maxReach, step);
            double firstDiff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, firstReach);
            if (Math.Abs(firstDiff) <= diffTolerance ||
                (branchSign > 0.0 && firstDiff < diffTolerance) ||
                (branchSign < 0.0 && firstDiff > -diffTolerance))
            {
                daylightReach = 0.0;
                return true;
            }

            prevReach = firstReach;
            prevDiff = firstDiff;
            sampleIndex = 2;
        }

        for (; sampleIndex <= sampleCount; sampleIndex++)
        {
            double currentReach = sampleIndex == sampleCount
                ? maxReach
                : Math.Min(maxReach, sampleIndex * step);
            double currentDiff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, currentReach);
            double currentAbsDiff = Math.Abs(currentDiff);
            if (currentAbsDiff < bestAbsDiff)
            {
                bestAbsDiff = currentAbsDiff;
                bestApproachReach = currentReach;
            }

            if (Math.Abs(currentDiff) <= diffTolerance)
            {
                daylightReach = currentReach;
                return true;
            }

            bool crossed = branchSign > 0.0
                ? prevDiff > diffTolerance && currentDiff < diffTolerance
                : prevDiff < -diffTolerance && currentDiff > -diffTolerance;
            if (crossed)
            {
                daylightReach = RefinePadDaylightReach(
                    TerrainFaceGrid,
                    edgeX,
                    edgeY,
                    edgeZ,
                    dirX,
                    dirY,
                    slopeRatio,
                    branchSign,
                    prevReach,
                    currentReach);
                return true;
            }

            prevReach = currentReach;
            prevDiff = currentDiff;
        }

        return false;
    }

    private static double RefinePadDaylightReach(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double lowReach,
        double highReach)
    {
        double reachScale = Math.Max(Math.Abs(lowReach), Math.Abs(highReach));
        double diffTolerance = ScaleAwareTolerance.ResolveLength(reachScale * 1e-7, reachScale);
        double reachTolerance = ScaleAwareTolerance.ResolveLength(reachScale * 1e-6, reachScale);
        double low = lowReach;
        double high = highReach;

        for (int i = 0; i < 24; i++)
        {
            double mid = (low + high) * 0.5;
            double diff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, mid);
            if (Math.Abs(diff) <= diffTolerance || (high - low) <= reachTolerance)
                return mid;

            if (branchSign > 0.0)
            {
                if (diff > 0.0) low = mid;
                else high = mid;
            }
            else
            {
                if (diff < 0.0) low = mid;
                else high = mid;
            }
        }

        return (low + high) * 0.5;
    }

    private static double EvaluatePadSectionDifference(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double reach)
    {
        double terrainZ = TerrainFaceGrid.InterpolateZ(edgeX + (dirX * reach), edgeY + (dirY * reach));
        double gradeZ = edgeZ + (branchSign * slopeRatio * reach);
        return terrainZ - gradeZ;
    }
}
