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
            if (length > ScaleAwareTolerance.LengthFloor(length))
                current = Math.Min(current, length);
        }

        return current;
    }

    private static double ComputePadConstraintSegmentLength(double shoulderDistance, double modelTolerance)
    {
        if (!(shoulderDistance > 0.0) || !double.IsFinite(shoulderDistance))
            return 0.0;

        double tolerance = ScaleAwareTolerance.ResolveLength(modelTolerance, shoulderDistance);
        return Math.Clamp(shoulderDistance * 0.2, tolerance * 500.0, tolerance * 1000.0);
    }
}
