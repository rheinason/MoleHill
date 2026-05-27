using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    /// <summary>
    /// Fallback: modify Z values of existing mesh without re-triangulation.
    /// </summary>
    private static GradingResult? GradeZOnly(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        out string? errorMessage)
    {
        return GradeZOnly(vertices, vertexCount, faces, faceCount, paths, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out errorMessage);
    }

    private static GradingResult? GradeZOnly(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        out string? errorMessage)
    {
        errorMessage = "Using Z-only grading (road edges may not be sharp).";

        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];
        bool hasBoundaryLoop = PadGrader.TryBuildBoundaryLoop(vertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        var faceGrid = new PadGrader.FaceGrid(vertices, vertexCount, faces, faceCount);

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = vertices[i * 3];
            outXy[i * 2 + 1] = vertices[i * 3 + 1];
            origZ[i] = vertices[i * 3 + 2];
            newZ[i] = vertices[i * 3 + 2];
        }

        ApplyPathGrading(
            paths,
            hardConstraints,
            outXy,
            origZ,
            newZ,
            vertexCount,
            faceGrid.InterpolateZ,
            hasBoundaryLoop: hasBoundaryLoop,
            boundaryLoop: boundaryLoop,
            boundaryVertexCount: boundaryVertexCount);

        var finalVerts = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            finalVerts[i * 3] = vertices[i * 3];
            finalVerts[i * 3 + 1] = vertices[i * 3 + 1];
            finalVerts[i * 3 + 2] = newZ[i];
        }

        return BuildResult(
            outXy,
            origZ,
            newZ,
            finalVerts,
            vertexCount,
            (int[])faces.Clone(),
            faceCount,
            outputPolylines: null,
            patchSummaries: BuildPathPatchSummaries(paths));
    }

    private static List<GradingPatch> BuildPathPatchSummaries(IReadOnlyList<PathDefinition> paths)
    {
        var patches = new List<GradingPatch>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
            patches.Add(BuildPathPatchSummary(BuildConservativePathOwnedLoop(paths[i]), paths[i], i));

        return patches;
    }

    private static GradingPatch BuildPathPatchSummary(double[] stitchLoopXy, PathDefinition path, int pathIndex)
    {
        return BuildPathPatchSummary(stitchLoopXy, stitchLoopXy, path, pathIndex);
    }

    private static GradingPatch BuildPathPatchSummary(double[] ownedRegionLoopXy, double[] stitchLoopXy, PathDefinition path, int pathIndex)
    {
        return new GradingPatch
        {
            OwnerKey = $"path:{pathIndex}",
            Kind = GradingPatchKind.Path,
            Priority = pathIndex,
            OwnedRegionLoopXy = (double[])ownedRegionLoopXy.Clone(),
            DaylightLoopXy = (double[])stitchLoopXy.Clone(),
            StitchLoopXy = (double[])stitchLoopXy.Clone(),
            DirtyBounds = GradingPatch.ComputeBounds(ownedRegionLoopXy),
            UsesFallbackBand = false
        };
    }

    private static string[] BuildPathStitchDiagnostics(
        int pathIndex,
        double[] shoulderLoopXy,
        double[] seamLoopXy,
        double[] patchLoopXy,
        double[] patchVertices,
        int[] patchFaces,
        int patchFaceCount,
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        double tolerance)
    {
        ComputeLoopDeviation(seamLoopXy, patchLoopXy, out double seamToPatchMax, out int seamMissCount, tolerance * 2.0);
        ComputeLoopDeviation(patchLoopXy, seamLoopXy, out double patchToSeamMax, out int patchMissCount, tolerance * 2.0);
        SeamGraph seamGraph = SeamGraph.Build(
            seamLoopXy,
            patchVertices,
            patchFaces,
            patchFaceCount,
            outsideVertices,
            outsideFaces,
            outsideFaceCount,
            tolerance);

        return
        [
            $"Grade Path[{pathIndex}] seam vertices: split={seamLoopXy.Length / 2}, patch={patchLoopXy.Length / 2}.",
            $"Grade Path[{pathIndex}] seam deviation: split->patch max={seamToPatchMax:F6} ({seamMissCount} misses), patch->split max={patchToSeamMax:F6} ({patchMissCount} misses).",
            BuildPathTopologyBandWidthDiagnostic(pathIndex, shoulderLoopXy, seamLoopXy),
            $"Grade Path[{pathIndex}] patch boundary edges near seam: {seamGraph.PatchBoundaryEdgesNearSeam}.",
            $"Grade Path[{pathIndex}] outside-mesh naked edges near seam: {seamGraph.TerrainBoundaryEdgesNearSeam}.",
            $"Grade Path[{pathIndex}] seam segment matches: patch={seamGraph.PatchMatchedSegments}/{seamGraph.SeamVertexCount}, outside={seamGraph.TerrainMatchedSegments}/{seamGraph.SeamVertexCount}.",
            $"Grade Path[{pathIndex}] seam-near boundary segments: patch={seamGraph.PatchBoundarySegmentsNearSeam}, outside={seamGraph.TerrainBoundarySegmentsNearSeam}."
        ];
    }

    private static string BuildPathSectionStatusDiagnostic(
        int pathIndex,
        string sideLabel,
        PathSectionResolutionStatus[] statuses,
        int repairedSectionCount)
    {
        int daylightCount = 0;
        int capCount = 0;
        int noGradeCount = 0;
        int blockedCount = 0;
        int unresolvedCount = 0;
        for (int i = 0; i < statuses.Length; i++)
        {
            switch (statuses[i])
            {
                case PathSectionResolutionStatus.ResolvedDaylight:
                    daylightCount++;
                    break;
                case PathSectionResolutionStatus.ResolvedCap:
                    capCount++;
                    break;
                case PathSectionResolutionStatus.NoGradeNeeded:
                    noGradeCount++;
                    break;
                case PathSectionResolutionStatus.Blocked:
                    blockedCount++;
                    break;
                default:
                    unresolvedCount++;
                    break;
            }
        }

        return $"Grade Path[{pathIndex}] {sideLabel} sections: daylight={daylightCount}, cap={capCount}, no-grade={noGradeCount}, blocked={blockedCount}, unresolved={unresolvedCount}, repaired={repairedSectionCount}.";
    }

    private static string BuildPathTopologyBandWidthDiagnostic(int pathIndex, double[] shoulderLoopXy, double[] seamLoopXy)
    {
        ComputeClosedLoopDistanceStats(shoulderLoopXy, seamLoopXy, out double shoulderToSeamMin, out double shoulderToSeamMax);
        ComputeClosedLoopDistanceStats(seamLoopXy, shoulderLoopXy, out double seamToShoulderMin, out double seamToShoulderMax);
        return $"Grade Path[{pathIndex}] topology band width: shoulder->seam min={shoulderToSeamMin:F6}, max={shoulderToSeamMax:F6}; seam->shoulder min={seamToShoulderMin:F6}, max={seamToShoulderMax:F6}.";
    }

    private static void ComputeClosedLoopDistanceStats(
        double[] sourceLoopXy,
        double[] targetLoopXy,
        out double minDistance,
        out double maxDistance)
    {
        minDistance = double.MaxValue;
        maxDistance = 0.0;
        int sourceCount = sourceLoopXy.Length / 2;
        int targetCount = targetLoopXy.Length / 2;
        for (int i = 0; i < sourceCount; i++)
        {
            double px = sourceLoopXy[i * 2];
            double py = sourceLoopXy[i * 2 + 1];
            double bestDistance = double.MaxValue;
            if (TryFindClosestClosedLoopLocation(targetLoopXy, targetCount, px, py, out ClosestClosedLoopLocation closest))
                bestDistance = closest.Distance;

            if (bestDistance < minDistance)
                minDistance = bestDistance;
            if (bestDistance > maxDistance)
                maxDistance = bestDistance;
        }

        if (minDistance == double.MaxValue)
            minDistance = 0.0;
    }

    private static double[] BuildConservativePathOwnedLoop(PathDefinition path)
    {
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        for (int i = 0; i < path.VertexCount; i++)
        {
            double x = path.XyVertices[i * 2];
            double y = path.XyVertices[i * 2 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double halfWidth = path.Width * 0.5;
        double shoulderAllowance = path.MaxDistance > 0.0
            ? path.MaxDistance
            : Math.Max(path.Width * 2.0, halfWidth);
        double expansion = halfWidth + shoulderAllowance;
        return new double[]
        {
            minX - expansion, minY - expansion,
            maxX + expansion, minY - expansion,
            maxX + expansion, maxY + expansion,
            minX - expansion, maxY + expansion
        };
    }

    /// <summary>
    /// Shared grading logic: evaluate all nearby paths per vertex so overlapping
    /// corridors blend by proximity instead of depending on input order.
    /// </summary>
    /// <param name="interpolateOriginalZ">
    /// Optional sampler returning original terrain Z at any XY point.
    /// Must represent the unmodified input terrain — not any already-graded
    /// or remeshed geometry. When null, falls back to vertex-accumulation
    /// for the reference shoulder profile.
    /// </param>
    private static void ApplyPathGrading(
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        double[] outXy, double[] origZ, double[] newZ, int vertCount,
        Func<double, double, double>? interpolateOriginalZ = null,
        bool hasBoundaryLoop = false,
        double[]? boundaryLoop = null,
        int boundaryVertexCount = 0,
        double boundaryTolerance = 1e-3)
    {
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
            return;
        }

        PreparedBarriers preparedBarriers = GradingBarriers.Build(barrierConstraints);
        var setupScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(preparedBarriers.Segments.Length, 1));
        var setupCandidates = new List<int>(8);
        var preparedPaths = new PreparedPath[paths.Length];
        for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
        {
            PathDefinition path = paths[pathIndex];
            double halfWidth = path.Width * 0.5;
            double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);
            double shoulderDistance = ComputePathShoulderDistance(outXy, origZ, vertCount, path, preparedBarriers, setupScratch, setupCandidates);
            var samplePath = BuildConstraintPolyline(
                path,
                ComputeConstraintSegmentLength(path, shoulderDistance),
                dedupTol: 1e-6);

            double[] leftReferenceDz, rightReferenceDz;
            if (interpolateOriginalZ != null)
            {
                BuildShoulderReferenceProfileDirect(
                    samplePath,
                    halfWidth,
                    shoulderDistance,
                    interpolateOriginalZ,
                    preparedBarriers,
                    setupScratch,
                    setupCandidates,
                    hasBoundaryLoop,
                    boundaryLoop ?? Array.Empty<double>(),
                    boundaryVertexCount,
                    boundaryTolerance,
                    out leftReferenceDz,
                    out rightReferenceDz);
            }
            else
            {
                BuildShoulderReferenceProfile(
                    samplePath,
                    halfWidth,
                    shoulderDistance,
                    outXy,
                    origZ,
                    vertCount,
                    preparedBarriers,
                    setupScratch,
                    setupCandidates,
                    out leftReferenceDz,
                    out rightReferenceDz);
            }

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
                path.MaxDistance,
                shoulderDistance,
                maxInfluence,
                samplePath,
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

            preparedPaths[pathIndex] = new PreparedPathSections(
                path.Width * 0.5,
                maxInfluence,
                samplePath,
                leftEdgeXy,
                rightEdgeXy,
                leftShoulderXy,
                rightShoulderXy,
                leftShoulderZ,
                rightShoulderZ,
                leftStatuses,
                rightStatuses,
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
        double halfWidth = path.Width * 0.5;
        double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);
        bool allowCapFallback = path.MaxDistance > 1e-9;

        for (int i = 0; i < n; i++)
        {
            double cx = samplePath.XyVertices[i * 2];
            double cy = samplePath.XyVertices[i * 2 + 1];

            GetConstraintPathTangent(samplePath, i, out double tangentX, out double tangentY);
            double normalX = -tangentY;
            double normalY = tangentX;

            double leftEdgeX = cx + (normalX * halfWidth);
            double leftEdgeY = cy + (normalY * halfWidth);
            double rightEdgeX = cx - (normalX * halfWidth);
            double rightEdgeY = cy - (normalY * halfWidth);
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
                normalX,
                normalY,
                slopeRatio,
                maxSearchDistance,
                path.Width,
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
                -normalX,
                -normalY,
                slopeRatio,
                maxSearchDistance,
                path.Width,
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
        maxInfluence = halfWidth + maxReach;
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

        if (!TryFindClosestPathLocation(preparedPath.SamplePath, px, py, out ClosestPathLocation closest))
            return false;

        if (closest.Distance > preparedPath.MaxInfluence + 1e-6)
            return false;

            if (closest.Distance <= preparedPath.HalfWidth + 1e-6)
            {
                insideRoad = true;
                candidateZ = closest.PathZ;
                weight = ComputeRoadBlendWeight(preparedPath.HalfWidth, closest.Distance);
            return true;
        }

        if (IsBlockedByBarrier(preparedBarriers, preparedPath.HalfWidth, closest, px, py, barrierScratch, barrierCandidates))
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

        if (sectionStatus == PathSectionResolutionStatus.Unresolved ||
            sectionStatus == PathSectionResolutionStatus.Blocked)
        {
            return false;
        }

        double sectionReach = Math.Sqrt(((shoulderX - edgeX) * (shoulderX - edgeX)) + ((shoulderY - edgeY) * (shoulderY - edgeY)));
        double distFromEdge = closest.Distance - preparedPath.HalfWidth;
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
        if (!double.IsFinite(estimatedReach) || estimatedReach <= 1e-6)
            return Math.Max(path.Width * 4.0, 1.0);

        return Math.Max(estimatedReach, Math.Max(path.Width * 4.0, 1.0));
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
        double slopeRatio,
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

        if (maxSearchDistance <= 1e-9 || slopeRatio <= 1e-12)
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

        double resolvedReach = 0.0;
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
        resolvedZ = edgeZ + (branchSign * slopeRatio * resolvedReach);
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
            Math.Min(maxReach, Math.Max(maxReach / 32.0, 0.1)),
            Math.Min(maxReach, Math.Max(maxReach * 0.25, 0.2)),
            Math.Min(maxReach, Math.Max(maxReach * 0.5, 0.4)),
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

    private static double ComputePathShoulderDistance(PathDefinition path, Func<double, double, double> interpolateOriginalZ)
    {
        if (path.MaxDistance > 0)
            return path.MaxDistance;

        double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);
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

    private static bool TryComputePathInfluence(
        PreparedPath preparedPath,
        PreparedBarriers preparedBarriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        double px,
        double py,
        double originalZ,
        out bool insideRoad,
        out double candidateZ,
        out double weight)
    {
        insideRoad = false;
        candidateZ = 0.0;
        weight = 0.0;

        if (px < preparedPath.MinX || px > preparedPath.MaxX || py < preparedPath.MinY || py > preparedPath.MaxY)
            return false;

        if (!TryFindClosestPathLocation(preparedPath.SamplePath, px, py, out ClosestPathLocation closest))
            return false;

        if (closest.Distance > preparedPath.MaxInfluence + 1e-6)
            return false;

        if (closest.Distance <= preparedPath.HalfWidth + 1e-6)
        {
            insideRoad = true;
            candidateZ = closest.PathZ;
            weight = ComputeRoadBlendWeight(preparedPath.HalfWidth, closest.Distance);
            return true;
        }

        double distFromEdge = closest.Distance - preparedPath.HalfWidth;
        if (IsBlockedByBarrier(preparedBarriers, preparedPath.HalfWidth, closest, px, py, barrierScratch, barrierCandidates))
            return false;

        double dzActual = originalZ - closest.PathZ;
        double dz = dzActual;
        if (TryGetShoulderReferenceDz(preparedPath, closest, out double referenceDz) &&
            Math.Abs(referenceDz) > 1e-12)
        {
            // Only use the reference when it agrees with the vertex's actual terrain
            // direction (same sign) or when dzActual is near-zero (flat terrain dip/bump
            // that needs lifting/lowering by the smooth profile).
            // Opposite-sign means the reference profile came from the wrong side
            // (SideSign flip) — using it would push the vertex in the wrong direction.
            if (referenceDz * dzActual >= -1e-12)
                dz = referenceDz;
        }

        double absDz = Math.Abs(dz);
        if (absDz <= 1e-12)
            return false;

        double neededDist = preparedPath.SlopeRatio > 1e-12
            ? absDz / preparedPath.SlopeRatio
            : double.MaxValue;
        if (preparedPath.MaxDistance > 0)
            neededDist = Math.Min(neededDist, preparedPath.MaxDistance);
        if (preparedPath.ShoulderDistance > 0)
            neededDist = Math.Min(neededDist, preparedPath.ShoulderDistance);

        if (distFromEdge >= neededDist)
            return false;

        double rise = distFromEdge * preparedPath.SlopeRatio;
        if (rise >= absDz)
            return false;

        candidateZ = closest.PathZ + Math.Sign(dz) * rise;

        weight = ComputeShoulderBlendWeight(distFromEdge, neededDist);
        return weight > 1e-12;
    }

    private static double ComputeRoadBlendWeight(double halfWidth, double closestDist)
    {
        if (halfWidth <= 1e-9)
            return 4.0;

        double closeness = 1.0 - Math.Clamp(closestDist / halfWidth, 0.0, 1.0);
        return 1.0 + (closeness * closeness * 3.0);
    }

    private static double ComputeShoulderBlendWeight(double distFromEdge, double neededDist)
    {
        if (neededDist <= 1e-9)
            return 0.0;

        double closeness = 1.0 - Math.Clamp(distFromEdge / neededDist, 0.0, 1.0);
        return closeness * closeness;
    }

    private static void BuildShoulderReferenceProfile(
        ConstraintPath samplePath,
        double halfWidth,
        double shoulderDistance,
        double[] xy,
        double[] z,
        int vertexCount,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        out double[] leftReferenceDz,
        out double[] rightReferenceDz)
    {
        leftReferenceDz = CreateNaNArray(samplePath.VertexCount);
        rightReferenceDz = CreateNaNArray(samplePath.VertexCount);
        if (samplePath.VertexCount < 2 || shoulderDistance <= 1e-9)
            return;

        var leftSum = new double[samplePath.VertexCount];
        var leftWeight = new double[samplePath.VertexCount];
        var rightSum = new double[samplePath.VertexCount];
        var rightWeight = new double[samplePath.VertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            double px = xy[i * 2];
            double py = xy[i * 2 + 1];
            if (!TryFindClosestPathLocation(samplePath, px, py, out ClosestPathLocation closest))
                continue;

            double distFromEdge = closest.Distance - halfWidth;
            if (distFromEdge <= 1e-6 || distFromEdge > shoulderDistance + 1e-6)
                continue;

            if (barriers.Segments.Length > 0 && IsBlockedByBarrier(barriers, halfWidth, closest, px, py, barrierScratch, barrierCandidates))
                continue;

            double dz = z[i] - closest.PathZ;
            if (Math.Abs(dz) <= 1e-9)
                continue;

            double normalizedDistance = Math.Clamp(distFromEdge / shoulderDistance, 0.0, 1.0);
            double sampleWeight = Math.Max(normalizedDistance * normalizedDistance, 1e-3);
            if (closest.SideSign >= 0.0)
                AccumulateReferenceSample(leftSum, leftWeight, closest.SegmentIndex, closest.SegmentT, dz, sampleWeight);
            else
                AccumulateReferenceSample(rightSum, rightWeight, closest.SegmentIndex, closest.SegmentT, dz, sampleWeight);
        }

        FinalizeReferenceProfile(leftSum, leftWeight, leftReferenceDz);
        FinalizeReferenceProfile(rightSum, rightWeight, rightReferenceDz);
    }

    /// <summary>
    /// Builds the shoulder reference dz profile by directly sampling the original
    /// terrain field at each path station, rather than accumulating from sparse
    /// mesh vertices. Uses a 3-point transverse aggregate (50 %, 75 %, 100 % of
    /// the shoulder reach) with equal weights to reduce sensitivity to local terrain
    /// anomalies at any single offset. Retains one smoothing pass for longitudinal
    /// continuity. Produces no NaN gaps so gap-filling is not required.
    /// </summary>
    private static void BuildShoulderReferenceProfileDirect(
        ConstraintPath samplePath,
        double halfWidth,
        double shoulderDistance,
        Func<double, double, double> interpolateOriginalZ,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        out double[] leftReferenceDz,
        out double[] rightReferenceDz)
    {
        int n = samplePath.VertexCount;
        leftReferenceDz  = new double[n];
        rightReferenceDz = new double[n];

        if (n < 2 || shoulderDistance <= 1e-9)
            return;

        for (int i = 0; i < n; i++)
        {
            double cx = samplePath.XyVertices[i * 2];
            double cy = samplePath.XyVertices[i * 2 + 1];
            double pathZ = samplePath.ZValues[i];

            // Use smooth tangent when available; fall back to ComputeDirection.
            double dx, dy;
            if (samplePath.TangentX != null && samplePath.TangentX.Length == n)
            { dx = samplePath.TangentX[i]; dy = samplePath.TangentY[i]; }
            else
            { ComputeDirection(samplePath.XyVertices, n, i, out dx, out dy); }
            double nx = -dy;  // left normal
            double ny =  dx;

            // Sample at 50 %, 75 %, 100 % of the shoulder reach measured from the road edge.
            for (int side = -1; side <= 1; side += 2)  // -1 = right, +1 = left
            {
                double roadEdgeX = cx + side * nx * halfWidth;
                double roadEdgeY = cy + side * ny * halfWidth;
                double s0 = SampleOriginalTerrainAlongShoulderRay(
                    interpolateOriginalZ,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    boundaryTolerance,
                    roadEdgeX,
                    roadEdgeY,
                    roadEdgeX + side * nx * (shoulderDistance * 0.50),
                    roadEdgeY + side * ny * (shoulderDistance * 0.50));
                double s1 = SampleOriginalTerrainAlongShoulderRay(
                    interpolateOriginalZ,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    boundaryTolerance,
                    roadEdgeX,
                    roadEdgeY,
                    roadEdgeX + side * nx * (shoulderDistance * 0.75),
                    roadEdgeY + side * ny * (shoulderDistance * 0.75));
                double s2 = SampleOriginalTerrainAlongShoulderRay(
                    interpolateOriginalZ,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    boundaryTolerance,
                    roadEdgeX,
                    roadEdgeY,
                    roadEdgeX + side * nx * shoulderDistance,
                    roadEdgeY + side * ny * shoulderDistance);
                double dz = (s0 + s1 + s2) / 3.0 - pathZ;

                if (side > 0)
                    leftReferenceDz[i]  = dz;
                else
                    rightReferenceDz[i] = dz;
            }
        }

        // One smoothing pass to avoid raw terrain frequency becoming grading oscillation.
        SmoothReferenceSamples(leftReferenceDz,  1);
        SmoothReferenceSamples(rightReferenceDz, 1);
    }

    private static double SampleOriginalTerrainAlongShoulderRay(
        Func<double, double, double> interpolateOriginalZ,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        double startX,
        double startY,
        double targetX,
        double targetY)
    {
        ResolveShoulderRayEndpoint(
            barriers,
            barrierScratch,
            barrierCandidates,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            boundaryTolerance,
            startX,
            startY,
            targetX,
            targetY,
            out _,
            out targetX,
            out targetY);
        return interpolateOriginalZ(targetX, targetY);
    }

    private static void ResolveShoulderRayEndpoint(
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        double startX,
        double startY,
        double targetX,
        double targetY,
        out ShoulderRayClipKind clipKind,
        out double resolvedX,
        out double resolvedY)
    {
        clipKind = ShoulderRayClipKind.None;
        resolvedX = targetX;
        resolvedY = targetY;

        if (barriers.Segments.Length > 0)
        {
            if (GradingBarriers.TryClipSegment(
                barriers,
                startX,
                startY,
                resolvedX,
                resolvedY,
                barrierScratch,
                barrierCandidates,
                out resolvedX,
                out resolvedY))
            {
                clipKind = ShoulderRayClipKind.Barrier;
            }
        }

        if (!hasBoundaryLoop)
            return;

        List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
            startX,
            startY,
            0.0,
            resolvedX,
            resolvedY,
            0.0,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            boundaryTolerance);

        double furthestEndT = double.MinValue;
        bool foundPiece = false;
        foreach (ClippedSegment piece in pieces)
        {
            if (piece.StartT > 1e-9 || piece.EndT <= furthestEndT)
                continue;

            resolvedX = piece.EndX;
            resolvedY = piece.EndY;
            furthestEndT = piece.EndT;
            foundPiece = true;
        }

        if (foundPiece)
        {
            if (clipKind == ShoulderRayClipKind.None)
                clipKind = ShoulderRayClipKind.Boundary;
            return;
        }

        if (!BoundaryClipper.IsInsideOrOnBoundary(startX, startY, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, boundaryTolerance))
        {
            clipKind = ShoulderRayClipKind.Boundary;
            resolvedX = startX;
            resolvedY = startY;
            return;
        }

        clipKind = ShoulderRayClipKind.Boundary;
        resolvedX = startX;
        resolvedY = startY;
    }

    private static void AccumulateReferenceSample(
        double[] sum,
        double[] weight,
        int segmentIndex,
        double segmentT,
        double dz,
        double sampleWeight)
    {
        if (segmentIndex < 0 || segmentIndex >= sum.Length - 1 || sampleWeight <= 0.0)
            return;

        double startWeight = sampleWeight * (1.0 - segmentT);
        double endWeight = sampleWeight * segmentT;
        sum[segmentIndex] += dz * startWeight;
        weight[segmentIndex] += startWeight;
        sum[segmentIndex + 1] += dz * endWeight;
        weight[segmentIndex + 1] += endWeight;
    }

    private static void FinalizeReferenceProfile(double[] sum, double[] weight, double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (weight[i] > 1e-9)
                values[i] = sum[i] / weight[i];
        }

        FillMissingReferenceSamples(values);
        SmoothReferenceSamples(values, 3);
    }

    private static void FillMissingReferenceSamples(double[] values)
    {
        int firstFinite = -1;
        for (int i = 0; i < values.Length; i++)
        {
            if (double.IsFinite(values[i]))
            {
                firstFinite = i;
                break;
            }
        }

        if (firstFinite < 0)
            return;

        for (int i = 0; i < firstFinite; i++)
            values[i] = values[firstFinite];

        int previousFinite = firstFinite;
        for (int i = firstFinite + 1; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
                continue;

            int nextFinite = i;
            if (nextFinite - previousFinite > 1)
            {
                double start = values[previousFinite];
                double end = values[nextFinite];
                int gap = nextFinite - previousFinite;
                for (int gapIndex = 1; gapIndex < gap; gapIndex++)
                {
                    double t = gapIndex / (double)gap;
                    values[previousFinite + gapIndex] = start + ((end - start) * t);
                }
            }

            previousFinite = nextFinite;
        }

        for (int i = previousFinite + 1; i < values.Length; i++)
            values[i] = values[previousFinite];
    }

    private static void SmoothReferenceSamples(double[] values, int passes)
    {
        if (values.Length < 3 || passes <= 0 || !HasFiniteSamples(values))
            return;

        var scratch = new double[values.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!double.IsFinite(values[i]))
                {
                    scratch[i] = values[i];
                    continue;
                }

                double weightedSum = values[i] * 0.5;
                double totalWeight = 0.5;

                if (i > 0 && double.IsFinite(values[i - 1]))
                {
                    weightedSum += values[i - 1] * 0.25;
                    totalWeight += 0.25;
                }

                if (i < values.Length - 1 && double.IsFinite(values[i + 1]))
                {
                    weightedSum += values[i + 1] * 0.25;
                    totalWeight += 0.25;
                }

                scratch[i] = weightedSum / totalWeight;
            }

            Array.Copy(scratch, values, values.Length);
        }
    }

    private static double[] CreateNaNArray(int length)
    {
        var values = new double[length];
        Array.Fill(values, double.NaN);
        return values;
    }

    private static bool HasFiniteSamples(double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (double.IsFinite(values[i]))
                return true;
        }

        return false;
    }

    private static bool TryGetShoulderReferenceDz(
        PreparedPath preparedPath,
        ClosestPathLocation closest,
        out double referenceDz)
    {
        referenceDz = 0.0;
        double[] values = closest.SideSign >= 0.0
            ? preparedPath.LeftReferenceDz
            : preparedPath.RightReferenceDz;
        if (values.Length < 2 || closest.SegmentIndex < 0 || closest.SegmentIndex >= values.Length - 1)
            return false;

        double start = values[closest.SegmentIndex];
        double end = values[closest.SegmentIndex + 1];
        if (!double.IsFinite(start) && !double.IsFinite(end))
            return false;

        if (!double.IsFinite(start))
            start = end;
        else if (!double.IsFinite(end))
            end = start;

        referenceDz = start + ((end - start) * closest.SegmentT);
        return double.IsFinite(referenceDz);
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

        double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);
        if (slopeRatio <= 1e-12)
            return 100.0;

        double halfWidth = path.Width * 0.5;
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
