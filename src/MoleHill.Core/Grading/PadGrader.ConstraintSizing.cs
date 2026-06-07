using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private static double UpdateSuggestedEdgeLength(
        double current,
        double[] points,
        int pointCount,
        int stride,
        bool isClosed)
    {
        if (pointCount < 2)
            return current;

        int segmentCount = isClosed ? pointCount : pointCount - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            int next = (i + 1) % pointCount;
            double dx = points[next * stride] - points[i * stride];
            double dy = points[next * stride + 1] - points[i * stride + 1];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 1e-9)
                current = Math.Min(current, length);
        }

        return current;
    }

    private static double ComputePadConstraintSegmentLength(double shoulderDistance)
    {
        if (shoulderDistance <= 1e-9)
            return 1.0;

        return Math.Clamp(shoulderDistance * 0.2, 0.5, 1.0);
    }

    private static double ComputeMinimumStitchSegmentLength(double modelTolerance, double terrainDetailSize)
    {
        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        double resolvedDetail = double.IsFinite(terrainDetailSize) && terrainDetailSize > 0.0
            ? terrainDetailSize
            : tolerance * 20.0;
        double targetStitchSpacing = Math.Max(tolerance * 8.0, resolvedDetail * 0.5);
        return Math.Max(tolerance * 4.0, targetStitchSpacing * 0.35);
    }
}