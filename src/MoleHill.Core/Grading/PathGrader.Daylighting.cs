using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    private static bool TryFindPathDaylightReach(
        Func<double, double, double> interpolateOriginalZ,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double maxReach,
        double width,
        out double daylightReach,
        out double bestApproachReach)
    {
        daylightReach = 0.0;
        bestApproachReach = 0.0;
        if (maxReach <= 1e-9 || Math.Abs(branchSign) <= 1e-12)
            return false;

        const double diffTolerance = 1e-4;
        double step = ComputePathDaylightSampleStep(maxReach, width);
        int sampleCount = Math.Max(1, (int)Math.Ceiling(maxReach / step));
        double startDiff = EvaluatePathSectionDifference(interpolateOriginalZ, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, 0.0);
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
            double firstDiff = EvaluatePathSectionDifference(interpolateOriginalZ, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, firstReach);
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
            double currentDiff = EvaluatePathSectionDifference(interpolateOriginalZ, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, currentReach);
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
                daylightReach = RefinePathDaylightReach(
                    interpolateOriginalZ,
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

    private static double ComputePathDaylightSampleStep(double maxReach, double width)
    {
        double maxStep = Math.Max(width * 0.5, 1.0);
        return Math.Clamp(maxReach / 48.0, 0.1, maxStep);
    }

    private static double RefinePathDaylightReach(
        Func<double, double, double> interpolateOriginalZ,
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
        const double diffTolerance = 1e-5;
        double low = lowReach;
        double high = highReach;

        for (int i = 0; i < 24; i++)
        {
            double mid = (low + high) * 0.5;
            double diff = EvaluatePathSectionDifference(interpolateOriginalZ, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, mid);
            if (Math.Abs(diff) <= diffTolerance || (high - low) <= 1e-4)
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

    private static double EvaluatePathSectionDifference(
        Func<double, double, double> interpolateOriginalZ,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double reach)
    {
        double terrainZ = interpolateOriginalZ(edgeX + (dirX * reach), edgeY + (dirY * reach));
        double gradeZ = edgeZ + (branchSign * slopeRatio * reach);
        return terrainZ - gradeZ;
    }

    private static double SampleShoulderSectionElevation(
        Func<double, double, double> interpolateOriginalZ,
        double edgeX,
        double edgeY,
        double shoulderX,
        double shoulderY)
    {
        double dx = shoulderX - edgeX;
        double dy = shoulderY - edgeY;
        if ((dx * dx) + (dy * dy) <= 1e-12)
            return interpolateOriginalZ(edgeX, edgeY);

        double midX0 = edgeX + (dx * 0.5);
        double midY0 = edgeY + (dy * 0.5);
        double midX1 = edgeX + (dx * 0.75);
        double midY1 = edgeY + (dy * 0.75);
        return (interpolateOriginalZ(midX0, midY0) +
                interpolateOriginalZ(midX1, midY1) +
                interpolateOriginalZ(shoulderX, shoulderY)) / 3.0;
    }
}