using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    private enum PathSectionResolutionStatus
    {
        ResolvedDaylight,
        ResolvedCap,
        NoGradeNeeded,
        Blocked,
        Unresolved
    }

    private enum PathSectionBranchStatus
    {
        Resolved,
        NoGradeNeeded,
        Unresolved
    }

    public sealed class ConstraintSet
    {
        public required SurfaceRemesher.ConstraintPolyline[] Constraints { get; init; }

        public required double SuggestedEdgeLength { get; init; }

        public IReadOnlyList<GradingDiagnostic> StructuredDiagnostics { get; init; } = Array.Empty<GradingDiagnostic>();

        public IReadOnlyList<string> Diagnostics =>
            StructuredDiagnostics.Count == 0
                ? Array.Empty<string>()
                : StructuredDiagnostics.Select(static diagnostic => diagnostic.Message).ToArray();
    }

    private readonly record struct ConstraintPath(
        double[] XyVertices,
        double[] ZValues,
        int VertexCount,
        double[] TangentX,
        double[] TangentY);
    private readonly record struct ClosestPathLocation(
        int SegmentIndex,
        double SegmentT,
        double Distance,
        double PathZ,
        double SideSign,
        double ProjectedX,
        double ProjectedY,
        double DirectionX,
        double DirectionY);

    private enum ShoulderRayClipKind
    {
        None,
        Barrier,
        Boundary
    }

    private readonly record struct PreparedPath(
        double HalfWidth,
        double SlopeRatio,
        double FillSlopeRatio,
        double MaxDistance,
        double ShoulderDistance,
        double MaxInfluence,
        ConstraintPath SamplePath,
        SpatialHashGrid2D? SegmentGrid,
        double[] LeftReferenceDz,
        double[] RightReferenceDz,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY)
    {
        /// <summary>Cut (branchSign &gt;= 0) vs fill (branchSign &lt; 0) batter slope ratio.</summary>
        public double SlopeRatioForBranch(double branchSign) => branchSign < 0.0 ? FillSlopeRatio : SlopeRatio;
    }

    private readonly record struct PreparedPathSections(
        double HalfWidth,
        double MaxInfluence,
        ConstraintPath SamplePath,
        SpatialHashGrid2D? SegmentGrid,
        double[] LeftEdgeXy,
        double[] RightEdgeXy,
        double[] LeftShoulderXy,
        double[] RightShoulderXy,
        double[] LeftShoulderZ,
        double[] RightShoulderZ,
        PathSectionResolutionStatus[] LeftStatuses,
        PathSectionResolutionStatus[] RightStatuses,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY);

    private readonly record struct ClosestClosedLoopLocation(
        int SegmentIndex,
        double SegmentT,
        double Distance);

    public sealed class PathDefinition
    {
        public double[] XyVertices { get; }
        public double[] ZValues { get; }
        public int VertexCount { get; }
        public double Width { get; }

        /// <summary>Cut-side batter slope (terrain above the road grade).</summary>
        public double SlopeAngleDeg { get; }

        /// <summary>Fill-side batter slope (terrain below the road grade). Defaults to the cut slope.</summary>
        public double FillSlopeAngleDeg { get; }

        public double MaxDistance { get; }

        public PathDefinition(double[] xyVertices, double[] zValues, int vertexCount,
                              double width, double slopeAngleDeg = 33.0, double maxDistance = 0.0,
                              double fillSlopeAngleDeg = 0.0)
        {
            XyVertices = xyVertices;
            ZValues = zValues;
            VertexCount = vertexCount;
            Width = width;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            FillSlopeAngleDeg = fillSlopeAngleDeg > 0.0
                ? Math.Max(0.1, Math.Min(89.9, fillSlopeAngleDeg))
                : SlopeAngleDeg;
            MaxDistance = maxDistance;
        }

        /// <summary>
        /// Slope ratio (rise/run) to use for a station, chosen by the cut/fill branch:
        /// <paramref name="branchSign"/> &gt; 0 means terrain is above grade (cut), &lt; 0 means below (fill).
        /// </summary>
        public double SlopeRatioFor(double branchSign) =>
            GradingSlope.RatioFor(SlopeAngleDeg, FillSlopeAngleDeg, branchSign);
    }
}
