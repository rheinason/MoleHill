using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private readonly record struct PadInfluenceBounds(
        PadBoundary Pad,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        double InfluenceMinX,
        double InfluenceMaxX,
        double InfluenceMinY,
        double InfluenceMaxY);

    private readonly record struct ClosestLoopLocation(
        int SegmentIndex,
        double SegmentT,
        double Distance);

    private readonly record struct PreparedPadSections(
        PadBoundary Pad,
        double[] BoundaryLoopXy,
        int BoundaryVertexCount,
        double[] ShoulderXy,
        double[] ShoulderZ,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        double InfluenceMinX,
        double InfluenceMaxX,
        double InfluenceMinY,
        double InfluenceMaxY);

    private readonly record struct ProtectedPadRegion(
        int PadIndex,
        PreparedPadSections Prepared,
        double[] DaylightLoopXy,
        double[] StitchLoopXy,
        Bounds2D Bounds);

    public sealed class ConstraintSet
    {
        public required SurfaceRemesher.ConstraintPolyline[] Constraints { get; init; }

        public required double SuggestedEdgeLength { get; init; }

        public required string[] Diagnostics { get; init; }

        public IReadOnlyList<GradingDiagnostic> StructuredDiagnostics { get; init; } = Array.Empty<GradingDiagnostic>();
    }

    public sealed class PadBoundary
    {
        public double[] XyVertices { get; }
        public int VertexCount { get; }
        public double[] BoundaryVertices { get; }
        public double PlaneXCoeff { get; }
        public double PlaneYCoeff { get; }
        public double PlaneConstant { get; }
        public double SlopeAngleDeg { get; }
        public double MaxDistance { get; }
        public int CornerFanSegments { get; }
        public double StitchApronDistance { get; }

        public PadBoundary(double[] xyVertices, int vertexCount, double targetZ,
            double slopeAngleDeg = 33.0, double maxDistance = 0.0, int cornerFanSegments = 0,
            double stitchApronDistance = DefaultStitchApronDistance)
        {
            XyVertices = (double[])xyVertices.Clone();
            VertexCount = vertexCount;
            BoundaryVertices = BuildBoundaryVertices(XyVertices, vertexCount, targetZ);
            PlaneXCoeff = 0.0;
            PlaneYCoeff = 0.0;
            PlaneConstant = targetZ;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
            CornerFanSegments = Math.Max(0, cornerFanSegments);
            StitchApronDistance = Math.Max(0.0, stitchApronDistance);
        }

        public static PadBoundary CreatePlanar(
            double[] boundaryVertices,
            int vertexCount,
            double planeXCoeff,
            double planeYCoeff,
            double planeConstant,
            double slopeAngleDeg = 33.0,
            double maxDistance = 0.0,
            int cornerFanSegments = 0,
            double stitchApronDistance = DefaultStitchApronDistance)
        {
            return new PadBoundary(
                ExtractXyVertices(boundaryVertices, vertexCount),
                boundaryVertices,
                vertexCount,
                planeXCoeff,
                planeYCoeff,
                planeConstant,
                slopeAngleDeg,
                maxDistance,
                cornerFanSegments,
                stitchApronDistance);
        }

        public double EvaluateZ(double x, double y) => PlaneXCoeff * x + PlaneYCoeff * y + PlaneConstant;

        private PadBoundary(
            double[] xyVertices,
            double[] boundaryVertices,
            int vertexCount,
            double planeXCoeff,
            double planeYCoeff,
            double planeConstant,
            double slopeAngleDeg,
            double maxDistance,
            int cornerFanSegments = 0,
            double stitchApronDistance = DefaultStitchApronDistance)
        {
            XyVertices = (double[])xyVertices.Clone();
            BoundaryVertices = (double[])boundaryVertices.Clone();
            VertexCount = vertexCount;
            PlaneXCoeff = planeXCoeff;
            PlaneYCoeff = planeYCoeff;
            PlaneConstant = planeConstant;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
            CornerFanSegments = Math.Max(0, cornerFanSegments);
            StitchApronDistance = Math.Max(0.0, stitchApronDistance);
        }

        private static double[] BuildBoundaryVertices(double[] xyVertices, int vertexCount, double targetZ)
        {
            var boundaryVertices = new double[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
            {
                boundaryVertices[i * 3] = xyVertices[i * 2];
                boundaryVertices[i * 3 + 1] = xyVertices[i * 2 + 1];
                boundaryVertices[i * 3 + 2] = targetZ;
            }

            return boundaryVertices;
        }

        private static double[] ExtractXyVertices(double[] boundaryVertices, int vertexCount)
        {
            var xyVertices = new double[vertexCount * 2];
            for (int i = 0; i < vertexCount; i++)
            {
                xyVertices[i * 2] = boundaryVertices[i * 3];
                xyVertices[i * 2 + 1] = boundaryVertices[i * 3 + 1];
            }

            return xyVertices;
        }
    }

    public sealed class LockCurve
    {
        public double[] XyVertices { get; }
        public int VertexCount { get; }

        public LockCurve(double[] xyVertices, int vertexCount)
        {
            XyVertices = xyVertices;
            VertexCount = vertexCount;
        }
    }

    private sealed class PadTopologyResult
    {
        public required double[] Vertices { get; init; }
        public required int VertexCount { get; init; }
        public required int[] Faces { get; init; }
        public required int FaceCount { get; init; }
        public required OutputPolyline[] PadPolylines { get; init; }
    }
}
