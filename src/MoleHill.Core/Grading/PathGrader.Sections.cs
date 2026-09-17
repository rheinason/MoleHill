using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    private static void ApplyPathGradingWithSections(
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        double[] outXy,
        double[] origZ,
        double[] newZ,
        int vertCount,
        Func<double, double, double> interpolateOriginalZ,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance)
    {
        PreparedBarriers preparedBarriers = GradingBarriers.Build(barrierConstraints);
        var setupScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(preparedBarriers.Segments.Length, 1));
        var setupCandidates = new List<int>(8);
        var preparedPaths = new PreparedPathSections[paths.Length];

        for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
        {
            PathDefinition path = paths[pathIndex];
            BuildPathSections(
                path,
                ComputePathSectionSearchDistance(path, interpolateOriginalZ),
                interpolateOriginalZ,
                preparedBarriers,
                setupScratch,
                setupCandidates,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                boundaryTolerance,
                out ConstraintPath samplePath,
                out double[] leftEdgeXy,
                out double[] rightEdgeXy,
                out double[] leftShoulderXy,
                out double[] rightShoulderXy,
                out double[] leftShoulderZ,
                out double[] rightShoulderZ,
                out PathSectionResolutionStatus[] leftStatuses,
                out PathSectionResolutionStatus[] rightStatuses,
                out _,
                out _,
                out double maxInfluence);

            double mnX = double.MaxValue;
            double mxX = double.MinValue;
            double mnY = double.MaxValue;
            double mxY = double.MinValue;
            for (int i = 0; i < samplePath.VertexCount; i++)
            {
                double x = samplePath.XyVertices[i * 2];
                double y = samplePath.XyVertices[i * 2 + 1];
                if (x < mnX) mnX = x;
                if (x > mxX) mxX = x;
                if (y < mnY) mnY = y;
                if (y > mxY) mxY = y;
            }

            SpatialHashGrid2D? segmentGrid = BuildPathSegmentGrid(samplePath);
            preparedPaths[pathIndex] = new PreparedPathSections(
                path.MaximumHalfWidth(),
                maxInfluence,
                samplePath,
                segmentGrid,
                leftEdgeXy,
                rightEdgeXy,
                leftShoulderXy,
                rightShoulderXy,
                leftShoulderZ,
                rightShoulderZ,
                leftStatuses,
                rightStatuses,
                path.OutwardSideSign(),
                mnX - maxInfluence,
                mxX + maxInfluence,
                mnY - maxInfluence,
                mxY + maxInfluence);
        }

        System.Threading.Tasks.Parallel.For(
            0,
            vertCount,
            () => (
                Scratch: new SpatialHashGrid2D.QueryScratch(Math.Max(preparedBarriers.Segments.Length, 1)),
                Candidates: new List<int>(8)),
            (i, _, state) =>
        {
            double px = outXy[i * 2];
            double py = outXy[i * 2 + 1];

            double roadWeightSum = 0.0;
            double roadZSum = 0.0;
            double shoulderWeightSum = 0.0;
            double shoulderDeltaSum = 0.0;

            foreach (var preparedPath in preparedPaths)
            {
                if (!TryComputePathSectionInfluence(
                        preparedPath,
                        preparedBarriers,
                        state.Scratch,
                        state.Candidates,
                        px,
                        py,
                        origZ[i],
                        boundaryTolerance,
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
                    shoulderDeltaSum += (candidateZ - origZ[i]) * weight;
                }
            }

            if (roadWeightSum > 1e-12)
            {
                newZ[i] = roadZSum / roadWeightSum;
            }
            else if (shoulderWeightSum > 1e-12)
            {
                newZ[i] = origZ[i] + (shoulderDeltaSum / shoulderWeightSum);
            }

            return state;
        }, _ => { });
    }

    private static void BuildPathSections(
        PathDefinition path,
        double maxSearchDistance,
        Func<double, double, double> interpolateOriginalZ,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        out ConstraintPath samplePath,
        out double[] leftEdgeXy,
        out double[] rightEdgeXy,
        out double[] leftShoulderXy,
        out double[] rightShoulderXy,
        out double[] leftShoulderZ,
        out double[] rightShoulderZ,
        out PathSectionResolutionStatus[] leftStatuses,
        out PathSectionResolutionStatus[] rightStatuses,
        out int repairedLeftSections,
        out int repairedRightSections,
        out double maxInfluence)
    {
        samplePath = BuildConstraintPolyline(
            path,
            ComputeConstraintSegmentLength(path, ComputePathSamplingDistance(path)),
            dedupTol: 1e-6);

        int n = samplePath.VertexCount;
        leftEdgeXy = new double[n * 2];
        rightEdgeXy = new double[n * 2];
        leftShoulderXy = new double[n * 2];
        rightShoulderXy = new double[n * 2];
        leftShoulderZ = new double[n];
        rightShoulderZ = new double[n];
        leftStatuses = new PathSectionResolutionStatus[n];
        rightStatuses = new PathSectionResolutionStatus[n];
        repairedLeftSections = 0;
        repairedRightSections = 0;
        double fallbackHalfWidth = path.Width * 0.5;
        double maximumHalfWidth = fallbackHalfWidth;
        double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);
        double fillSlopeRatio = Math.Tan(path.FillSlopeAngleDeg * Math.PI / 180.0);

        // Each side solves its own shoulder endpoint, so each gets its own batter pair. A symmetric
        // definition resolves both to the shared pair, so this is not a special case.
        double leftSlopeRatio = Math.Tan(path.LeftCutSlopeAngleDeg * Math.PI / 180.0);
        double leftFillSlopeRatio = Math.Tan(path.LeftFillSlopeAngleDeg * Math.PI / 180.0);
        double rightSlopeRatio = Math.Tan(path.RightCutSlopeAngleDeg * Math.PI / 180.0);
        double rightFillSlopeRatio = Math.Tan(path.RightFillSlopeAngleDeg * Math.PI / 180.0);
        bool allowCapFallback = path.MaxDistance > 1e-9;

        for (int i = 0; i < n; i++)
        {
            double cx = samplePath.XyVertices[i * 2];
            double cy = samplePath.XyVertices[i * 2 + 1];

            GetConstraintPathTangent(samplePath, i, out double tangentX, out double tangentY);
            double normalX = -tangentY;
            double normalY = tangentX;

            double leftEdgeX = samplePath.LeftEdgeXy?[i * 2] ?? cx + (normalX * fallbackHalfWidth);
            double leftEdgeY = samplePath.LeftEdgeXy?[(i * 2) + 1] ?? cy + (normalY * fallbackHalfWidth);
            double rightEdgeX = samplePath.RightEdgeXy?[i * 2] ?? cx - (normalX * fallbackHalfWidth);
            double rightEdgeY = samplePath.RightEdgeXy?[(i * 2) + 1] ?? cy - (normalY * fallbackHalfWidth);
            double leftDistance = Math.Sqrt(((leftEdgeX - cx) * (leftEdgeX - cx)) + ((leftEdgeY - cy) * (leftEdgeY - cy)));
            double rightDistance = Math.Sqrt(((rightEdgeX - cx) * (rightEdgeX - cx)) + ((rightEdgeY - cy) * (rightEdgeY - cy)));
            maximumHalfWidth = Math.Max(maximumHalfWidth, Math.Max(leftDistance, rightDistance));
            double localWidth = Math.Max(leftDistance + rightDistance, boundaryTolerance);
            leftEdgeXy[i * 2] = leftEdgeX;
            leftEdgeXy[i * 2 + 1] = leftEdgeY;
            rightEdgeXy[i * 2] = rightEdgeX;
            rightEdgeXy[i * 2 + 1] = rightEdgeY;

            SolvePathSectionEndpoint(
                interpolateOriginalZ,
                barriers,
                barrierScratch,
                barrierCandidates,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                boundaryTolerance,
                leftEdgeX,
                leftEdgeY,
                samplePath.ZValues[i],
                leftDistance > boundaryTolerance ? (leftEdgeX - cx) / leftDistance : normalX,
                leftDistance > boundaryTolerance ? (leftEdgeY - cy) / leftDistance : normalY,
                leftSlopeRatio,
                leftFillSlopeRatio,
                maxSearchDistance,
                localWidth,
                boundaryTolerance,
                allowCapFallback,
                out PathSectionResolutionStatus leftStatus,
                out double leftShoulderX,
                out double leftShoulderY,
                out double leftShoulderSectionZ);
            leftShoulderXy[i * 2] = leftShoulderX;
            leftShoulderXy[i * 2 + 1] = leftShoulderY;
            leftShoulderZ[i] = leftShoulderSectionZ;
            leftStatuses[i] = leftStatus;

            SolvePathSectionEndpoint(
                interpolateOriginalZ,
                barriers,
                barrierScratch,
                barrierCandidates,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                boundaryTolerance,
                rightEdgeX,
                rightEdgeY,
                samplePath.ZValues[i],
                rightDistance > boundaryTolerance ? (rightEdgeX - cx) / rightDistance : -normalX,
                rightDistance > boundaryTolerance ? (rightEdgeY - cy) / rightDistance : -normalY,
                rightSlopeRatio,
                rightFillSlopeRatio,
                maxSearchDistance,
                localWidth,
                boundaryTolerance,
                allowCapFallback,
                out PathSectionResolutionStatus rightStatus,
                out double rightShoulderX,
                out double rightShoulderY,
                out double rightShoulderSectionZ);
            rightShoulderXy[i * 2] = rightShoulderX;
            rightShoulderXy[i * 2 + 1] = rightShoulderY;
            rightShoulderZ[i] = rightShoulderSectionZ;
            rightStatuses[i] = rightStatus;

        }

        ApplyNoGradeFallbackShoulders(leftEdgeXy, leftShoulderXy, leftShoulderZ, leftStatuses, samplePath.ZValues, n);
        ApplyNoGradeFallbackShoulders(rightEdgeXy, rightShoulderXy, rightShoulderZ, rightStatuses, samplePath.ZValues, n);
        repairedLeftSections = RepairShortUnresolvedPathSectionRuns(leftEdgeXy, leftShoulderXy, leftShoulderZ, samplePath.ZValues, leftStatuses, n);
        repairedRightSections = RepairShortUnresolvedPathSectionRuns(rightEdgeXy, rightShoulderXy, rightShoulderZ, samplePath.ZValues, rightStatuses, n);
        double maxReach = ComputePathMaxShoulderReach(leftEdgeXy, rightEdgeXy, leftShoulderXy, rightShoulderXy, n);
        maxInfluence = maximumHalfWidth + maxReach;
    }

    private static bool TryComputePathSectionInfluence(
        PreparedPathSections preparedPath,
        PreparedBarriers preparedBarriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        double px,
        double py,
        double originalZ,
        double tolerance,
        out bool insideRoad,
        out double candidateZ,
        out double weight)
    {
        insideRoad = false;
        candidateZ = 0.0;
        weight = 0.0;

        if (px < preparedPath.MinX || px > preparedPath.MaxX || py < preparedPath.MinY || py > preparedPath.MaxY)
            return false;

        if (!TryFindClosestPathLocation(
                preparedPath.SamplePath,
                preparedPath.SegmentGrid,
                preparedPath.MaxInfluence + 1e-6,
                px,
                py,
                barrierScratch,
                barrierCandidates,
                out ClosestPathLocation closest))
            return false;

        if (closest.Distance > preparedPath.MaxInfluence + 1e-6)
            return false;

        if (!TryInterpolatePathSection(
                preparedPath,
                closest,
                out double edgeX,
                out double edgeY,
                out double shoulderX,
                out double shoulderY,
                out double shoulderZ,
                out PathSectionResolutionStatus sectionStatus))
            return false;

        double edgeDistance = Math.Sqrt(
            ((edgeX - closest.ProjectedX) * (edgeX - closest.ProjectedX)) +
            ((edgeY - closest.ProjectedY) * (edgeY - closest.ProjectedY)));
        if (closest.Distance <= edgeDistance + 1e-6)
        {
            insideRoad = true;
            candidateZ = closest.PathZ;
            weight = ComputeRoadBlendWeight(edgeDistance, closest.Distance);
            return true;
        }

        // A one-sided rail batters away from its partner only — the wall's upper rail must not grade
        // the ground below the wall. This sits after the on-rail case above so the rail itself still
        // pins to its authored elevation; only the batter is confined to the outward side.
        if (preparedPath.OutwardSideSign != 0.0 &&
            (closest.SideSign >= 0.0 ? 1.0 : -1.0) != preparedPath.OutwardSideSign)
        {
            return false;
        }

        if (preparedBarriers.Segments.Length > 0 &&
            GradingBarriers.IsCrossedByBarrier(
                preparedBarriers,
                edgeX,
                edgeY,
                px,
                py,
                barrierScratch,
                barrierCandidates))
        {
            return false;
        }

        if (sectionStatus == PathSectionResolutionStatus.Unresolved ||
            sectionStatus == PathSectionResolutionStatus.Blocked)
        {
            return false;
        }

        double sectionReach = Math.Sqrt(((shoulderX - edgeX) * (shoulderX - edgeX)) + ((shoulderY - edgeY) * (shoulderY - edgeY)));
        double distFromEdge = closest.Distance - edgeDistance;
        if (sectionReach <= 1e-9 || distFromEdge > sectionReach + 1e-9)
            return false;

        double normalizedDistance = Math.Clamp(distFromEdge / sectionReach, 0.0, 1.0);
        candidateZ = closest.PathZ + ((shoulderZ - closest.PathZ) * normalizedDistance);
        candidateZ = ClampBetween(candidateZ, closest.PathZ, shoulderZ);
        if (Math.Abs(candidateZ - originalZ) <= GradingTolerances.VertexAdjustmentZTolerance(tolerance))
            return false;

        weight = ComputeShoulderBlendWeight(distFromEdge, sectionReach);
        return weight > 1e-12;
    }

    private static bool TryInterpolatePathSection(
        PreparedPathSections preparedPath,
        ClosestPathLocation closest,
        out double edgeX,
        out double edgeY,
        out double shoulderX,
        out double shoulderY,
        out double shoulderZ,
        out PathSectionResolutionStatus sectionStatus)
    {
        edgeX = 0.0;
        edgeY = 0.0;
        shoulderX = 0.0;
        shoulderY = 0.0;
        shoulderZ = 0.0;
        sectionStatus = PathSectionResolutionStatus.Unresolved;

        double[] edgeXy = closest.SideSign >= 0.0 ? preparedPath.LeftEdgeXy : preparedPath.RightEdgeXy;
        double[] shoulderXy = closest.SideSign >= 0.0 ? preparedPath.LeftShoulderXy : preparedPath.RightShoulderXy;
        double[] shoulderZValues = closest.SideSign >= 0.0 ? preparedPath.LeftShoulderZ : preparedPath.RightShoulderZ;
        PathSectionResolutionStatus[] statuses = closest.SideSign >= 0.0 ? preparedPath.LeftStatuses : preparedPath.RightStatuses;
        int segmentIndex = closest.SegmentIndex;
        if (segmentIndex < 0 || segmentIndex >= preparedPath.SamplePath.VertexCount - 1)
            return false;

        PathSectionResolutionStatus startStatus = statuses[segmentIndex];
        PathSectionResolutionStatus endStatus = statuses[segmentIndex + 1];
        if (startStatus == PathSectionResolutionStatus.Unresolved ||
            endStatus == PathSectionResolutionStatus.Unresolved ||
            startStatus == PathSectionResolutionStatus.Blocked ||
            endStatus == PathSectionResolutionStatus.Blocked)
        {
            return false;
        }

        edgeX = InterpolateSectionValue(edgeXy[segmentIndex * 2], edgeXy[(segmentIndex + 1) * 2], closest.SegmentT);
        edgeY = InterpolateSectionValue(edgeXy[(segmentIndex * 2) + 1], edgeXy[((segmentIndex + 1) * 2) + 1], closest.SegmentT);
        shoulderX = InterpolateSectionValue(shoulderXy[segmentIndex * 2], shoulderXy[(segmentIndex + 1) * 2], closest.SegmentT);
        shoulderY = InterpolateSectionValue(shoulderXy[(segmentIndex * 2) + 1], shoulderXy[((segmentIndex + 1) * 2) + 1], closest.SegmentT);
        shoulderZ = InterpolateSectionValue(shoulderZValues[segmentIndex], shoulderZValues[segmentIndex + 1], closest.SegmentT);
        sectionStatus = CombineInterpolatedPathSectionStatus(startStatus, endStatus);
        return double.IsFinite(shoulderZ);
    }

    private static double InterpolateSectionValue(double start, double end, double t)
    {
        return start + ((end - start) * t);
    }

    private static double ClampBetween(double value, double a, double b)
    {
        double min = Math.Min(a, b);
        double max = Math.Max(a, b);
        return Math.Max(min, Math.Min(max, value));
    }

    private static void ApplyNoGradeFallbackShoulders(
        double[] edgeXy,
        double[] shoulderXy,
        double[] shoulderZ,
        PathSectionResolutionStatus[] statuses,
        double[] edgeZ,
        int vertexCount)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (statuses[i] != PathSectionResolutionStatus.NoGradeNeeded)
                continue;

            shoulderXy[i * 2] = edgeXy[i * 2];
            shoulderXy[(i * 2) + 1] = edgeXy[(i * 2) + 1];
            shoulderZ[i] = edgeZ[i];
        }
    }

    private static int RepairShortUnresolvedPathSectionRuns(
        double[] edgeXy,
        double[] shoulderXy,
        double[] shoulderZ,
        double[] edgeZ,
        PathSectionResolutionStatus[] statuses,
        int vertexCount)
    {
        int repairedCount = 0;
        int index = 0;
        while (index < vertexCount)
        {
            if (statuses[index] != PathSectionResolutionStatus.Unresolved)
            {
                index++;
                continue;
            }

            int runStart = index;
            while (index < vertexCount && statuses[index] == PathSectionResolutionStatus.Unresolved)
                index++;

            int runEnd = index - 1;
            int runLength = runEnd - runStart + 1;
            if (runLength > 2)
                continue;

            int previous = runStart - 1;
            int next = index < vertexCount ? index : -1;
            bool hasPrevious = previous >= 0 && IsRepairablePathSectionStatus(statuses[previous]);
            bool hasNext = next >= 0 && next < vertexCount && IsRepairablePathSectionStatus(statuses[next]);
            if (!hasPrevious && !hasNext)
                continue;

            for (int repairIndex = runStart; repairIndex <= runEnd; repairIndex++)
            {
                if (hasPrevious && hasNext)
                {
                    double t = (repairIndex - previous) / (double)(next - previous);
                    shoulderXy[repairIndex * 2] = InterpolateSectionValue(shoulderXy[previous * 2], shoulderXy[next * 2], t);
                    shoulderXy[(repairIndex * 2) + 1] = InterpolateSectionValue(shoulderXy[(previous * 2) + 1], shoulderXy[(next * 2) + 1], t);
                    shoulderZ[repairIndex] = InterpolateSectionValue(shoulderZ[previous], shoulderZ[next], t);
                    statuses[repairIndex] = CombineInterpolatedPathSectionStatus(statuses[previous], statuses[next]);
                }
                else if (hasPrevious)
                {
                    shoulderXy[repairIndex * 2] = shoulderXy[previous * 2];
                    shoulderXy[(repairIndex * 2) + 1] = shoulderXy[(previous * 2) + 1];
                    shoulderZ[repairIndex] = shoulderZ[previous];
                    statuses[repairIndex] = statuses[previous];
                }
                else
                {
                    shoulderXy[repairIndex * 2] = shoulderXy[next * 2];
                    shoulderXy[(repairIndex * 2) + 1] = shoulderXy[(next * 2) + 1];
                    shoulderZ[repairIndex] = shoulderZ[next];
                    statuses[repairIndex] = statuses[next];
                }

                if (statuses[repairIndex] == PathSectionResolutionStatus.NoGradeNeeded)
                {
                    shoulderXy[repairIndex * 2] = edgeXy[repairIndex * 2];
                    shoulderXy[(repairIndex * 2) + 1] = edgeXy[(repairIndex * 2) + 1];
                    shoulderZ[repairIndex] = edgeZ[repairIndex];
                }

                repairedCount++;
            }
        }

        return repairedCount;
    }

    private static bool IsRepairablePathSectionStatus(PathSectionResolutionStatus status)
    {
        return status != PathSectionResolutionStatus.Unresolved &&
               status != PathSectionResolutionStatus.Blocked;
    }

    private static PathSectionResolutionStatus CombineInterpolatedPathSectionStatus(
        PathSectionResolutionStatus startStatus,
        PathSectionResolutionStatus endStatus)
    {
        if (startStatus == endStatus)
            return startStatus;
        if (startStatus == PathSectionResolutionStatus.Blocked || endStatus == PathSectionResolutionStatus.Blocked)
            return PathSectionResolutionStatus.Blocked;
        if (startStatus == PathSectionResolutionStatus.Unresolved || endStatus == PathSectionResolutionStatus.Unresolved)
            return PathSectionResolutionStatus.Unresolved;
        if (startStatus == PathSectionResolutionStatus.ResolvedCap || endStatus == PathSectionResolutionStatus.ResolvedCap)
            return PathSectionResolutionStatus.ResolvedCap;
        if (startStatus == PathSectionResolutionStatus.NoGradeNeeded && endStatus == PathSectionResolutionStatus.NoGradeNeeded)
            return PathSectionResolutionStatus.NoGradeNeeded;
        return PathSectionResolutionStatus.ResolvedDaylight;
    }

    private static double ComputePathMaxShoulderReach(
        double[] leftEdgeXy,
        double[] rightEdgeXy,
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        int vertexCount)
    {
        double maxReach = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            double leftDx = leftShoulderXy[i * 2] - leftEdgeXy[i * 2];
            double leftDy = leftShoulderXy[(i * 2) + 1] - leftEdgeXy[(i * 2) + 1];
            double rightDx = rightShoulderXy[i * 2] - rightEdgeXy[i * 2];
            double rightDy = rightShoulderXy[(i * 2) + 1] - rightEdgeXy[(i * 2) + 1];
            maxReach = Math.Max(maxReach, Math.Sqrt((leftDx * leftDx) + (leftDy * leftDy)));
            maxReach = Math.Max(maxReach, Math.Sqrt((rightDx * rightDx) + (rightDy * rightDy)));
        }

        return maxReach;
    }

    private static double ComputePathSamplingDistance(PathDefinition path)
    {
        return path.MaxDistance > 1e-9 ? path.MaxDistance : path.Width;
    }

    private static double ComputePathSectionSearchDistance(PathDefinition path, Func<double, double, double> interpolateOriginalZ)
    {
        if (path.MaxDistance > 1e-9)
            return path.MaxDistance;

        double estimatedReach = ComputePathShoulderDistance(path, interpolateOriginalZ);
        if (!double.IsFinite(estimatedReach) || estimatedReach <= MoleHill.Core.Engine.ScaleAwareTolerance.LengthFloor(path.Width))
            return path.Width * 4.0;

        return Math.Max(estimatedReach, path.Width * 4.0);
    }

    private static void SolvePathSectionEndpoint(
        Func<double, double, double> interpolateOriginalZ,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double cutSlopeRatio,
        double fillSlopeRatio,
        double maxSearchDistance,
        double width,
        double tolerance,
        bool allowCapFallback,
        out PathSectionResolutionStatus status,
        out double resolvedX,
        out double resolvedY,
        out double resolvedZ)
    {
        status = PathSectionResolutionStatus.Unresolved;
        resolvedX = edgeX;
        resolvedY = edgeY;
        resolvedZ = edgeZ;

        if (maxSearchDistance <= 1e-9 || (cutSlopeRatio <= 1e-12 && fillSlopeRatio <= 1e-12))
        {
            status = PathSectionResolutionStatus.NoGradeNeeded;
            return;
        }

        ResolveShoulderRayEndpoint(
            barriers,
            barrierScratch,
            barrierCandidates,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            boundaryTolerance,
            edgeX,
            edgeY,
            edgeX + (dirX * maxSearchDistance),
            edgeY + (dirY * maxSearchDistance),
            out ShoulderRayClipKind clipKind,
            out double clippedX,
            out double clippedY);

        double clippedReach = Math.Sqrt(((clippedX - edgeX) * (clippedX - edgeX)) + ((clippedY - edgeY) * (clippedY - edgeY)));
        if (clippedReach <= 1e-9)
        {
            status = PathSectionResolutionStatus.Blocked;
            return;
        }

        PathSectionBranchStatus branchStatus = TryDeterminePathSectionBranch(
            interpolateOriginalZ,
            edgeX,
            edgeY,
            edgeZ,
            dirX,
            dirY,
            clippedReach,
            tolerance,
            out double branchSign);
        if (branchStatus == PathSectionBranchStatus.NoGradeNeeded)
        {
            status = PathSectionResolutionStatus.NoGradeNeeded;
            return;
        }

        if (branchStatus != PathSectionBranchStatus.Resolved)
        {
            status = PathSectionResolutionStatus.Unresolved;
            return;
        }

        // Cut (terrain above grade) and fill (below) can use different batter slopes.
        double slopeRatio = branchSign < 0.0 ? fillSlopeRatio : cutSlopeRatio;
        if (slopeRatio <= 1e-12)
        {
            status = PathSectionResolutionStatus.NoGradeNeeded;
            return;
        }

        double resolvedReach = 0.0;
        bool capToTerrainZ = false;
        if (TryFindPathDaylightReach(
                interpolateOriginalZ,
                edgeX,
                edgeY,
                edgeZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                clippedReach,
                width,
                out double daylightReach,
                out double bestApproachReach))
        {
            resolvedReach = daylightReach;
            status = PathSectionResolutionStatus.ResolvedDaylight;
        }
        else if (allowCapFallback || clipKind == ShoulderRayClipKind.Barrier)
        {
            resolvedReach = clippedReach;
            status = PathSectionResolutionStatus.ResolvedCap;
        }
        else if (bestApproachReach > 1e-6)
        {
            resolvedReach = bestApproachReach;
            status = PathSectionResolutionStatus.ResolvedDaylight;
        }
        else if (clipKind == ShoulderRayClipKind.Boundary)
        {
            // The ray ran off the surveyed terrain before daylighting. Cap the section AT the terrain
            // boundary with the terrain's own elevation there (the terrain outline keeps its Z by
            // convention) so the shoulder blends continuously from road edge to rim. Leaving the
            // section unresolved keeps its vertices at terrain Z next to graded neighbours, which
            // reads as a near-vertical rim spike along the boundary.
            resolvedReach = clippedReach;
            status = PathSectionResolutionStatus.ResolvedCap;
            capToTerrainZ = true;
        }

        if (resolvedReach <= 1e-9)
        {
            if (status == PathSectionResolutionStatus.ResolvedDaylight)
            {
                status = PathSectionResolutionStatus.NoGradeNeeded;
                resolvedX = edgeX;
                resolvedY = edgeY;
                resolvedZ = edgeZ;
                return;
            }

            status = PathSectionResolutionStatus.Unresolved;
            return;
        }

        resolvedX = edgeX + (dirX * resolvedReach);
        resolvedY = edgeY + (dirY * resolvedReach);
        if (capToTerrainZ)
        {
            double terrainZ = interpolateOriginalZ(resolvedX, resolvedY);
            resolvedZ = double.IsFinite(terrainZ)
                ? terrainZ
                : edgeZ + (branchSign * slopeRatio * resolvedReach);
        }
        else
        {
            resolvedZ = edgeZ + (branchSign * slopeRatio * resolvedReach);
        }
    }

    private static PathSectionBranchStatus TryDeterminePathSectionBranch(
        Func<double, double, double> interpolateOriginalZ,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double maxReach,
        double tolerance,
        out double branchSign)
    {
        branchSign = 0.0;
        if (maxReach <= 1e-9)
            return PathSectionBranchStatus.Unresolved;

        double[] candidateDistances =
        {
            0.0,
            maxReach / 32.0,
            maxReach * 0.25,
            maxReach * 0.5,
            maxReach
        };

        double maxAbsDelta = 0.0;
        double dominantDelta = 0.0;
        bool sawPositive = false;
        bool sawNegative = false;
        double firstSignificantDelta = 0.0;
        double lastReach = -1.0;
        for (int i = 0; i < candidateDistances.Length; i++)
        {
            double reach = candidateDistances[i];
            if (reach < -1e-9 || Math.Abs(reach - lastReach) <= 1e-9)
                continue;

            double terrainZ = interpolateOriginalZ(edgeX + (dirX * reach), edgeY + (dirY * reach));
            if (!double.IsFinite(terrainZ))
                return PathSectionBranchStatus.Unresolved;

            double terrainDelta = terrainZ - edgeZ;
            double absDelta = Math.Abs(terrainDelta);
            if (absDelta > maxAbsDelta)
            {
                maxAbsDelta = absDelta;
                dominantDelta = terrainDelta;
            }

            if (terrainDelta > GradingTolerances.AtGradeZTolerance(tolerance))
            {
                sawPositive = true;
                if (Math.Abs(firstSignificantDelta) <= 1e-12)
                    firstSignificantDelta = terrainDelta;
            }

            if (terrainDelta < -GradingTolerances.AtGradeZTolerance(tolerance))
            {
                sawNegative = true;
                if (Math.Abs(firstSignificantDelta) <= 1e-12)
                    firstSignificantDelta = terrainDelta;
            }

            lastReach = reach;
        }

        if (sawPositive || sawNegative)
        {
            branchSign = sawPositive && sawNegative
                ? Math.Sign(dominantDelta)
                : Math.Sign(firstSignificantDelta);
            return Math.Abs(branchSign) > 0.0 ? PathSectionBranchStatus.Resolved : PathSectionBranchStatus.Unresolved;
        }

        return maxAbsDelta <= GradingTolerances.AtGradeZTolerance(tolerance)
            ? PathSectionBranchStatus.NoGradeNeeded
            : PathSectionBranchStatus.Unresolved;
    }

    private static double ComputePathShoulderDistance(PathDefinition path, Func<double, double, double> interpolateOriginalZ)
    {
        if (path.MaxDistance > 0)
            return path.MaxDistance;

        double slopeRatio = Math.Tan(path.FlattestCutAngleDeg() * Math.PI / 180.0);
        if (slopeRatio <= 1e-12)
            return 100.0;

        double maxZDiff = 0.0;
        for (int i = 0; i < path.VertexCount; i++)
        {
            double x = path.XyVertices[i * 2];
            double y = path.XyVertices[i * 2 + 1];
            double dz = Math.Abs(path.ZValues[i] - interpolateOriginalZ(x, y));
            if (dz > maxZDiff)
                maxZDiff = dz;
        }

        return maxZDiff / slopeRatio;
    }

    private static double ComputePathShoulderDistance(double[] vertices, int vertexCount, PathDefinition path)
    {
        var xy = new double[vertexCount * 2];
        var z = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            xy[i * 2] = vertices[i * 3];
            xy[i * 2 + 1] = vertices[i * 3 + 1];
            z[i] = vertices[i * 3 + 2];
        }

        var scratch = new SpatialHashGrid2D.QueryScratch(1);
        return ComputePathShoulderDistance(xy, z, vertexCount, path, PreparedBarriers.Empty, scratch, new List<int>());
    }

    private static double ComputePathShoulderDistance(double[] xy, double[] z, int vertexCount, PathDefinition path,
        PreparedBarriers barriers, SpatialHashGrid2D.QueryScratch barrierScratch, List<int> barrierCandidates)
    {
        if (path.MaxDistance > 0)
            return path.MaxDistance;

        double slopeRatio = Math.Tan(path.FlattestCutAngleDeg() * Math.PI / 180.0);
        if (slopeRatio <= 1e-12)
            return 100.0;

        double halfWidth = path.MaximumHalfWidth();
        double mnX = double.MaxValue, mxX = double.MinValue;
        double mnY = double.MaxValue, mxY = double.MinValue;
        for (int i = 0; i < path.VertexCount; i++)
        {
            double x = path.XyVertices[i * 2];
            double y = path.XyVertices[i * 2 + 1];
            if (x < mnX) mnX = x;
            if (x > mxX) mxX = x;
            if (y < mnY) mnY = y;
            if (y > mxY) mxY = y;
        }

        double maxZDiff = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double px = xy[i * 2];
            double py = xy[i * 2 + 1];
            if (px < mnX - 200 || px > mxX + 200 || py < mnY - 200 || py > mxY + 200)
                continue;

            if (!TryFindClosestPathLocation(path.XyVertices, path.ZValues, path.VertexCount, px, py, out ClosestPathLocation closest))
                continue;

            if (barriers.Segments.Length > 0 && IsBlockedByBarrier(barriers, halfWidth, closest, px, py, barrierScratch, barrierCandidates))
                continue;

            double dz = Math.Abs(z[i] - closest.PathZ);
            if (dz > maxZDiff)
                maxZDiff = dz;
        }

        return maxZDiff / slopeRatio;
    }
}
