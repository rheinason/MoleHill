using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    /// <summary>
    /// Shared grading logic: evaluate all nearby paths per vertex so overlapping
    /// corridors blend by proximity instead of depending on input order.
    /// </summary>
    /// <param name="interpolateOriginalZ">
    /// Optional sampler returning original terrain Z at any XY point.
    /// Must represent the unmodified input terrain, not any already-graded
    /// or remeshed geometry. When null, falls back to vertex-accumulation
    /// for the reference shoulder profile.
    /// </param>
    private static void ApplyPathGrading(
        PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> barrierConstraints,
        double[] outXy, double[] origZ, double[] newZ, int vertCount,
        Func<double, double, double>? interpolateOriginalZ = null,
        bool hasBoundaryLoop = false,
        double[]? boundaryLoop = null,
        int boundaryVertexCount = 0,
        double boundaryTolerance = 1e-3)
    {
        PreparedBarriers preparedBarriers = GradingBarriers.Build(barrierConstraints);
        if (interpolateOriginalZ != null)
        {
            ApplyPathGradingWithSections(
                paths,
                barrierConstraints,
                outXy,
                origZ,
                newZ,
                vertCount,
                interpolateOriginalZ,
                hasBoundaryLoop,
                boundaryLoop ?? Array.Empty<double>(),
                boundaryVertexCount,
                boundaryTolerance);
        }
        else
        {
            ApplyPathGradingByProximity(paths, preparedBarriers, outXy, origZ, newZ, vertCount);
        }

        KeepHardConstraintHeights(preparedBarriers, outXy, origZ, newZ, vertCount);
    }

    /// <summary>
    /// A vertex on a hard constraint keeps its height. A batter stops where its ray from the road edge
    /// crosses a constraint, but a vertex lying on the constraint only touches it at the ray's end, so the
    /// constraint's own vertices were the ones a batter reaching it moved: a breakline past a path's end or
    /// beside a stopped path came out up to the road's full rise off its drawn height.
    /// </summary>
    private static void KeepHardConstraintHeights(
        PreparedBarriers barriers, double[] outXy, double[] origZ, double[] newZ, int vertCount)
    {
        if (barriers.Segments.Length == 0)
            return;

        const double onLine = 1e-6;
        var scratch = new SpatialHashGrid2D.QueryScratch(barriers.Segments.Length);
        var candidates = new List<int>(8);
        for (int i = 0; i < vertCount; i++)
        {
            if (newZ[i] == origZ[i])
                continue;

            double px = outXy[i * 2], py = outXy[(i * 2) + 1];
            barriers.Index.GatherCandidates(new Bounds2D(px - onLine, px + onLine, py - onLine, py + onLine), candidates, scratch);
            foreach (int index in candidates)
            {
                BarrierSegment segment = barriers.Segments[index];
                if (Geometry2D.DistanceToSegment(px, py, segment.Ax, segment.Ay, segment.Bx, segment.By) <= onLine)
                {
                    newZ[i] = origZ[i];
                    break;
                }
            }
        }
    }

    private static void ApplyPathGradingByProximity(
        PathDefinition[] paths,
        PreparedBarriers preparedBarriers,
        double[] outXy, double[] origZ, double[] newZ, int vertCount)
    {
        var setupScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(preparedBarriers.Segments.Length, 1));
        var setupCandidates = new List<int>(8);
        var preparedPaths = new PreparedPath[paths.Length];
        for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
        {
            PathDefinition path = paths[pathIndex];
            double halfWidth = path.MaximumHalfWidth();
            double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);
            double fillSlopeRatio = Math.Tan(path.FillSlopeAngleDeg * Math.PI / 180.0);
            double leftSlopeRatio = Math.Tan(path.LeftCutSlopeAngleDeg * Math.PI / 180.0);
            double leftFillSlopeRatio = Math.Tan(path.LeftFillSlopeAngleDeg * Math.PI / 180.0);
            double rightSlopeRatio = Math.Tan(path.RightCutSlopeAngleDeg * Math.PI / 180.0);
            double rightFillSlopeRatio = Math.Tan(path.RightFillSlopeAngleDeg * Math.PI / 180.0);
            double shoulderDistance = ComputePathShoulderDistance(outXy, origZ, vertCount, path, preparedBarriers, setupScratch, setupCandidates);
            var samplePath = BuildConstraintPolyline(
                path,
                ComputeConstraintSegmentLength(path, shoulderDistance),
                dedupTol: 1e-6);
            SpatialHashGrid2D? segmentGrid = BuildPathSegmentGrid(samplePath);

            BuildShoulderReferenceProfile(
                samplePath,
                segmentGrid,
                halfWidth,
                shoulderDistance,
                outXy,
                origZ,
                vertCount,
                preparedBarriers,
                setupScratch,
                setupCandidates,
                out double[] leftReferenceDz,
                out double[] rightReferenceDz);

            double mnX = double.MaxValue, mxX = double.MinValue;
            double mnY = double.MaxValue, mxY = double.MinValue;
            for (int i = 0; i < samplePath.VertexCount; i++)
            {
                double x = samplePath.XyVertices[i * 2], y = samplePath.XyVertices[i * 2 + 1];
                if (x < mnX) mnX = x; if (x > mxX) mxX = x;
                if (y < mnY) mnY = y; if (y > mxY) mxY = y;
            }

            double maxInfluence = halfWidth + shoulderDistance;
            preparedPaths[pathIndex] = new PreparedPath(
                halfWidth,
                slopeRatio,
                fillSlopeRatio,
                leftSlopeRatio,
                leftFillSlopeRatio,
                rightSlopeRatio,
                rightFillSlopeRatio,
                path.OutwardSideSign(),
                path.MaxDistance,
                shoulderDistance,
                maxInfluence,
                samplePath,
                segmentGrid,
                leftReferenceDz,
                rightReferenceDz,
                mnX - maxInfluence,
                mxX + maxInfluence,
                mnY - maxInfluence,
                mxY + maxInfluence);
        }

        System.Threading.Tasks.Parallel.For(
            0,
            vertCount,
            () => (
                Scratch: new SpatialHashGrid2D.QueryScratch(preparedBarriers.Segments.Length),
                Candidates: new List<int>(8)),
            (i, _, state) =>
        {
            double px = outXy[i * 2];
            double py = outXy[i * 2 + 1];
            double originalZ = origZ[i];

            double roadWeightSum = 0.0;
            double roadZSum = 0.0;
            double shoulderWeightSum = 0.0;
            double shoulderDeltaSum = 0.0;

            foreach (var preparedPath in preparedPaths)
            {
                if (!TryComputePathInfluence(
                        preparedPath,
                        preparedBarriers,
                        state.Scratch,
                        state.Candidates,
                        px,
                        py,
                        originalZ,
                        out bool insideRoad,
                        out double candidateZ,
                        out double weight))
                {
                    continue;
                }

                if (insideRoad)
                {
                    roadWeightSum += weight;
                    roadZSum += candidateZ * weight;
                }
                else
                {
                    shoulderWeightSum += weight;
                    shoulderDeltaSum += (candidateZ - originalZ) * weight;
                }
            }

            if (roadWeightSum > 1e-12)
            {
                newZ[i] = roadZSum / roadWeightSum;
            }
            else if (shoulderWeightSum > 1e-12)
            {
                newZ[i] = originalZ + (shoulderDeltaSum / shoulderWeightSum);
            }

            return state;
        }, _ => { });
    }
}
