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
        double MaxDistance,
        double ShoulderDistance,
        double MaxInfluence,
        ConstraintPath SamplePath,
        double[] LeftReferenceDz,
        double[] RightReferenceDz,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY);

    private readonly record struct PreparedPathSections(
        double HalfWidth,
        double MaxInfluence,
        ConstraintPath SamplePath,
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
        public double SlopeAngleDeg { get; }
        public double MaxDistance { get; }

        public PathDefinition(double[] xyVertices, double[] zValues, int vertexCount,
                              double width, double slopeAngleDeg = 33.0, double maxDistance = 0.0)
        {
            XyVertices = xyVertices;
            ZValues = zValues;
            VertexCount = vertexCount;
            Width = width;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
        }
    }
}
