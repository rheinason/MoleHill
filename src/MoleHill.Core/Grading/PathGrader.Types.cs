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
        public required ConstraintPolyline[] Constraints { get; init; }

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
        double[] TangentY,
        double[]? LeftEdgeXy = null,
        double[]? RightEdgeXy = null);
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
        double LeftSlopeRatio,
        double LeftFillSlopeRatio,
        double RightSlopeRatio,
        double RightFillSlopeRatio,
        double OutwardSideSign,
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

        /// <summary>
        /// The batter ratio for one side of the design line, so an asymmetric section survives the
        /// elevation pass. <paramref name="sideSign"/> follows <see cref="ClosestPathLocation.SideSign"/>:
        /// non-negative is the left side. Symmetric definitions resolve both sides to the same pair,
        /// so this is the general form of <see cref="SlopeRatioForBranch"/>, not a special case.
        /// </summary>
        public double SlopeRatioFor(double branchSign, double sideSign)
        {
            bool fill = branchSign < 0.0;
            if (sideSign >= 0.0)
                return fill ? LeftFillSlopeRatio : LeftSlopeRatio;
            return fill ? RightFillSlopeRatio : RightSlopeRatio;
        }
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
        double OutwardSideSign,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY);

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

        /// <summary>Optional plan rails aligned one-to-one with <see cref="XyVertices"/>. Their
        /// elevations are deliberately absent: the centerline <see cref="ZValues"/> author the
        /// finished path elevation across the whole section.</summary>
        public double[]? LeftEdgeXy { get; }

        public double[]? RightEdgeXy { get; }

        public bool IsClosed { get; }

        /// <summary>
        /// Per-side batter overrides. Zero (or non-positive) means "inherit the shared
        /// <see cref="SlopeAngleDeg"/>/<see cref="FillSlopeAngleDeg"/> pair", so a symmetric section
        /// stays the default. Left is the side the plan normal (-tangentY, tangentX) points to.
        /// </summary>
        public double LeftCutSlopeAngleDeg { get; }

        public double LeftFillSlopeAngleDeg { get; }

        public double RightCutSlopeAngleDeg { get; }

        public double RightFillSlopeAngleDeg { get; }

        /// <summary>
        /// Optional explicit outward directions, one unit normal per vertex, flat
        /// <c>[nx0,ny0,nx1,ny1,...]</c>. A retaining-wall rail grades away from its partner rail, which
        /// is not the curve's own plan normal — so that caller supplies the direction rather than
        /// letting it be derived. When set, only that one side is graded.
        /// </summary>
        public double[]? OutwardNormals { get; }

        /// <summary>
        /// A width-less design line: the curve itself is the graded footprint and the batters run
        /// away from it. Everything downstream (stationing, daylight, carve, weld) is the corridor
        /// pipeline with the two rails coincident.
        /// </summary>
        public bool IsSingleLine => Width <= 0.0;

        public bool HasVariableWidth =>
            LeftEdgeXy is { Length: > 0 } && RightEdgeXy is { Length: > 0 };

        public PathDefinition(double[] xyVertices, double[] zValues, int vertexCount,
                              double width, double slopeAngleDeg = 33.0, double maxDistance = 0.0,
                              double fillSlopeAngleDeg = 0.0,
                              double[]? leftEdgeXy = null,
                              double[]? rightEdgeXy = null,
                              bool isClosed = false,
                              double leftCutSlopeAngleDeg = 0.0,
                              double leftFillSlopeAngleDeg = 0.0,
                              double rightCutSlopeAngleDeg = 0.0,
                              double rightFillSlopeAngleDeg = 0.0,
                              double[]? outwardNormals = null)
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
            LeftEdgeXy = leftEdgeXy;
            RightEdgeXy = rightEdgeXy;
            IsClosed = isClosed;
            LeftCutSlopeAngleDeg = ResolveSideAngle(leftCutSlopeAngleDeg, SlopeAngleDeg);
            LeftFillSlopeAngleDeg = ResolveSideAngle(leftFillSlopeAngleDeg, FillSlopeAngleDeg);
            RightCutSlopeAngleDeg = ResolveSideAngle(rightCutSlopeAngleDeg, SlopeAngleDeg);
            RightFillSlopeAngleDeg = ResolveSideAngle(rightFillSlopeAngleDeg, FillSlopeAngleDeg);
            OutwardNormals = outwardNormals;
        }

        private static double ResolveSideAngle(double overrideDeg, double inherited) =>
            overrideDeg > 0.0 ? Math.Max(0.1, Math.Min(89.9, overrideDeg)) : inherited;

        /// <summary>
        /// Which side of the line a batter is allowed on: +1 left, -1 right, 0 both sides (the ordinary
        /// case). Derived from <see cref="OutwardNormals"/> by comparing each supplied direction with the
        /// line's own left normal, so a wall rail only grades away from its partner. Without this the
        /// elevation pass would batter a one-sided rail in *both* directions — the daylight geometry
        /// would be right and the elevations wrong, which is exactly how it presented live.
        /// </summary>
        internal double OutwardSideSign()
        {
            if (OutwardNormals is not { Length: > 0 })
                return 0.0;

            double sum = 0.0;
            for (int i = 0; i < VertexCount; i++)
            {
                int next = Math.Min(i + 1, VertexCount - 1);
                int prev = Math.Max(i - 1, 0);
                double tx = XyVertices[next * 2] - XyVertices[prev * 2];
                double ty = XyVertices[(next * 2) + 1] - XyVertices[(prev * 2) + 1];
                double length = Math.Sqrt((tx * tx) + (ty * ty));
                if (length <= 1e-12)
                    continue;

                // The left normal, matching ClosestPathLocation.SideSign's convention.
                double lx = -ty / length;
                double ly = tx / length;
                sum += (lx * OutwardNormals[i * 2]) + (ly * OutwardNormals[(i * 2) + 1]);
            }

            return sum >= 0.0 ? 1.0 : -1.0;
        }

        /// <summary>
        /// The flattest cut angle across both sides, which is the batter that reaches furthest and so
        /// sizes the section search. A symmetric definition resolves both sides to
        /// <see cref="SlopeAngleDeg"/>, so this returns exactly that for every ordinary path.
        /// </summary>
        internal double FlattestCutAngleDeg() => Math.Min(LeftCutSlopeAngleDeg, RightCutSlopeAngleDeg);

        /// <summary>
        /// True when either side departs from the shared cut/fill pair. Comparing left with right is
        /// not enough: both sides overridden to the same angle agree with each other yet not with the
        /// shared pair, and the daylight loop, which grades at the shared pair unless told otherwise,
        /// would then disagree with the sections that grade at the override.
        /// </summary>
        internal bool HasAsymmetricSides =>
            LeftCutSlopeAngleDeg != SlopeAngleDeg ||
            RightCutSlopeAngleDeg != SlopeAngleDeg ||
            LeftFillSlopeAngleDeg != FillSlopeAngleDeg ||
            RightFillSlopeAngleDeg != FillSlopeAngleDeg;

        /// <summary>
        /// Transverse half extent of the graded footprint. A single line genuinely has none — it is
        /// its own footprint — so this returns zero there, and every caller already floors it against
        /// a model tolerance or adds it to another distance.
        /// </summary>
        internal double MaximumHalfWidth()
        {
            double maximum = Width * 0.5;
            if (!HasVariableWidth)
                return maximum;

            for (int i = 0; i < VertexCount; i++)
            {
                double cx = XyVertices[i * 2];
                double cy = XyVertices[(i * 2) + 1];
                double ldx = LeftEdgeXy![i * 2] - cx;
                double ldy = LeftEdgeXy[(i * 2) + 1] - cy;
                double rdx = RightEdgeXy![i * 2] - cx;
                double rdy = RightEdgeXy[(i * 2) + 1] - cy;
                maximum = Math.Max(maximum, Math.Sqrt((ldx * ldx) + (ldy * ldy)));
                maximum = Math.Max(maximum, Math.Sqrt((rdx * rdx) + (rdy * rdy)));
            }

            return maximum;
        }

        /// <summary>
        /// Slope ratio (rise/run) to use for a station, chosen by the cut/fill branch:
        /// <paramref name="branchSign"/> &gt; 0 means terrain is above grade (cut), &lt; 0 means below (fill).
        /// </summary>
        public double SlopeRatioFor(double branchSign) =>
            GradingSlope.RatioFor(SlopeAngleDeg, FillSlopeAngleDeg, branchSign);
    }
}
