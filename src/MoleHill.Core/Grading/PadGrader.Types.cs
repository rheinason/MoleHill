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

    public sealed class ConstraintSet
    {
        public required ConstraintPolyline[] Constraints { get; init; }

        public ConstraintPolyline[] GuidePolylines { get; init; } = Array.Empty<ConstraintPolyline>();

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

        /// <summary>Cut-side batter slope (terrain above the pad grade).</summary>
        public double SlopeAngleDeg { get; }

        /// <summary>Fill-side batter slope (terrain below the pad grade). Defaults to the cut slope.</summary>
        public double FillSlopeAngleDeg { get; }

        public double MaxDistance { get; }
        public int CornerFanSegments { get; }
        public double StitchApronDistance { get; }

        public PadBoundary(double[] xyVertices, int vertexCount, double targetZ,
            double slopeAngleDeg = 33.0, double maxDistance = 0.0, int cornerFanSegments = 0,
            double stitchApronDistance = DefaultStitchApronDistance, double fillSlopeAngleDeg = 0.0)
        {
            XyVertices = (double[])xyVertices.Clone();
            VertexCount = vertexCount;
            BoundaryVertices = BuildBoundaryVertices(XyVertices, vertexCount, targetZ);
            PlaneXCoeff = 0.0;
            PlaneYCoeff = 0.0;
            PlaneConstant = targetZ;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            FillSlopeAngleDeg = ResolveFillSlope(fillSlopeAngleDeg, SlopeAngleDeg);
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
            double stitchApronDistance = DefaultStitchApronDistance,
            double fillSlopeAngleDeg = 0.0)
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
                stitchApronDistance,
                fillSlopeAngleDeg);
        }

        public double EvaluateZ(double x, double y) => PlaneXCoeff * x + PlaneYCoeff * y + PlaneConstant;

        /// <summary>
        /// Slope ratio (rise/run) for a station, chosen by the cut/fill branch:
        /// <paramref name="branchSign"/> &gt; 0 means terrain is above grade (cut), &lt; 0 means below (fill).
        /// </summary>
        public double SlopeRatioFor(double branchSign) =>
            GradingSlope.RatioFor(SlopeAngleDeg, FillSlopeAngleDeg, branchSign);

        private static double ResolveFillSlope(double fillSlopeAngleDeg, double cutSlopeAngleDeg) =>
            fillSlopeAngleDeg > 0.0 ? Math.Max(0.1, Math.Min(89.9, fillSlopeAngleDeg)) : cutSlopeAngleDeg;

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
            double stitchApronDistance = DefaultStitchApronDistance,
            double fillSlopeAngleDeg = 0.0)
        {
            XyVertices = (double[])xyVertices.Clone();
            BoundaryVertices = (double[])boundaryVertices.Clone();
            VertexCount = vertexCount;
            PlaneXCoeff = planeXCoeff;
            PlaneYCoeff = planeYCoeff;
            PlaneConstant = planeConstant;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            FillSlopeAngleDeg = ResolveFillSlope(fillSlopeAngleDeg, SlopeAngleDeg);
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

}
