using MoleHill.Core.Engine;
using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh along path curves (roads, sidewalks, etc.).
/// Adds road edges (path offset by half-width) as constrained edges,
/// re-triangulates, then grades Z: inside road = path Z, outside = slope transition.
/// Falls back to Z-only modification if triangulation fails.
/// </summary>
public static partial class PathGrader
{
    /// <summary>
    /// Apply path grading to a terrain mesh.
    /// Overlapping paths are blended by proximity so junction behavior is stable
    /// regardless of the input order.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        out string? errorMessage,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        return Grade(vertices, vertexCount, faces, faceCount, paths, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out errorMessage, modelTolerance);
    }

    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        out string? errorMessage,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        errorMessage = null;

        if (paths.Length == 0)
        {
            errorMessage = "No path definitions provided.";
            return null;
        }

        foreach (var path in paths)
        {
            if (path.VertexCount < 2)
            {
                errorMessage = "Each path must have at least 2 vertices.";
                return null;
            }
            if (path.Width <= 0)
            {
                errorMessage = "Path width must be positive.";
                return null;
            }
        }

        // Grade Path must own and rebuild topology. Do not silently fall back to Z-only grading.
        var result = GradeWithEdges(vertices, vertexCount, faces, faceCount, paths, hardConstraints, modelTolerance, out string? topologyError, out _);
        if (result != null)
        {
            errorMessage = null;
            return result;
        }

        errorMessage = string.IsNullOrWhiteSpace(topologyError)
            ? "Grade Path topology rebuild failed."
            : $"Grade Path topology rebuild failed: {topologyError}";
        return null;
    }

    public static double[] ApplyGradingZ(double[] topologyVertices, int vertexCount, PathDefinition[] paths)
    {
        return ApplyGradingZ(topologyVertices, vertexCount, paths, out _);
    }

    public static GradingResult? TryRepairRejectedStitchedResult(GradingResult result, out string? diagnostic)
    {
        diagnostic = null;
        if (result.VertexCount <= 0 || result.FaceCount <= 0)
            return null;

        MeshArtifactCleaner.CleanupResult cleanup = MeshArtifactCleaner.Clean(
            result.Vertices,
            result.VertexCount,
            result.Faces,
            result.FaceCount,
            new MeshArtifactCleaner.Options(
                MinComponentFaceCount: 1,
                MinComponentAreaRatio: 0.0,
                MinFaceAngleDegrees: 1.0,
                MaxAspectRatio: 100.0,
                KeepLargestComponentOnly: true));

        if (cleanup.RemovedFaceCount <= 0)
        {
            diagnostic = $"Grade Path stitched repair found no removable detached components (components {cleanup.Before.ComponentCount}, boundary edges {cleanup.Before.BoundaryEdgeCount}).";
            return null;
        }

        diagnostic =
            $"Grade Path stitched repair kept the largest connected component only (components {cleanup.Before.ComponentCount}->{cleanup.After.ComponentCount}, removed components={cleanup.RemovedComponentCount}, removed faces={cleanup.RemovedFaceCount}, boundary edges {cleanup.Before.BoundaryEdgeCount}->{cleanup.After.BoundaryEdgeCount}).";

        string[] diagnostics = result.Diagnostics.Count == 0
            ? new[] { diagnostic }
            : result.Diagnostics.Concat(new[] { diagnostic }).ToArray();
        GradingDiagnostic[] structuredDiagnostics = result.StructuredDiagnostics
            .Concat(new[]
            {
                GradingDiagnostic.Warning(
                    "grade_path.stitched_repair.detached_components_removed",
                    diagnostic,
                    operation: "Grade Path")
            })
            .ToArray();

        return new GradingResult(
            cleanup.Vertices,
            cleanup.VertexCount,
            cleanup.Faces,
            cleanup.FaceCount,
            result.CutVolume,
            result.FillVolume,
            result.DaylightVertices,
            result.DaylightVertexCount,
            result.OutputPolylines,
            diagnostics,
            result.PatchSummaries,
            structuredDiagnostics);
    }

    public static double[] ApplyGradingZ(double[] topologyVertices, int vertexCount, PathDefinition[] paths, out int changedVertexCount)
    {
        return ApplyGradingZ(topologyVertices, vertexCount, paths, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out changedVertexCount);
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out int changedVertexCount)
    {
        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = topologyVertices[i * 3];
            outXy[i * 2 + 1] = topologyVertices[i * 3 + 1];
            origZ[i] = topologyVertices[i * 3 + 2];
            newZ[i] = topologyVertices[i * 3 + 2];
        }

        ApplyPathGrading(paths, barrierConstraints, outXy, origZ, newZ, vertexCount);

        var gradedVertices = new double[vertexCount * 3];
        changedVertexCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            gradedVertices[i * 3] = topologyVertices[i * 3];
            gradedVertices[i * 3 + 1] = topologyVertices[i * 3 + 1];
            gradedVertices[i * 3 + 2] = newZ[i];
            if (Math.Abs(newZ[i] - origZ[i]) > 1e-9)
                changedVertexCount++;
        }

        return gradedVertices;
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        out int changedVertexCount)
    {
        return ApplyGradingZ(
            topologyVertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            out changedVertexCount);
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out int changedVertexCount)
    {
        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];
        bool hasBoundaryLoop = PadGrader.TryBuildBoundaryLoop(topologyVertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        var faceGrid = new PadGrader.FaceGrid(topologyVertices, vertexCount, faces, faceCount);

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = topologyVertices[i * 3];
            outXy[i * 2 + 1] = topologyVertices[i * 3 + 1];
            origZ[i] = topologyVertices[i * 3 + 2];
            newZ[i] = topologyVertices[i * 3 + 2];
        }

        ApplyPathGrading(
            paths,
            barrierConstraints,
            outXy,
            origZ,
            newZ,
            vertexCount,
            faceGrid.InterpolateZ,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            boundaryTolerance: 1e-3);

        var gradedVertices = new double[vertexCount * 3];
        changedVertexCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            gradedVertices[i * 3] = topologyVertices[i * 3];
            gradedVertices[i * 3 + 1] = topologyVertices[i * 3 + 1];
            gradedVertices[i * 3 + 2] = newZ[i];
            if (Math.Abs(newZ[i] - origZ[i]) > 1e-9)
                changedVertexCount++;
        }

        return gradedVertices;
    }

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

    private static double ComputeConstraintSegmentLength(PathDefinition path, double shoulderDistance)
    {
        double baseSpacing = shoulderDistance > 1e-6 ? shoulderDistance * 0.5 : path.Width;
        double minSpacing = Math.Max(path.Width * 0.5, 1.0);
        double maxSpacing = Math.Max(minSpacing, path.Width * 2.0);
        return Math.Clamp(baseSpacing, minSpacing, maxSpacing);
    }

    private static ConstraintPath MakeGeometryConstraintPath(double[] xy, double[] z, int count)
        => new ConstraintPath(xy, z, count, Array.Empty<double>(), Array.Empty<double>());

    private static ConstraintPath BuildConstraintPolyline(PathDefinition path, double maxSegmentLength, double dedupTol)
    {
        if (path.VertexCount < 2 || maxSegmentLength <= dedupTol)
        {
            double[] xyOut = (double[])path.XyVertices.Clone();
            int n = path.VertexCount;
            ComputeSmoothedTangents(xyOut, n, out double[] tx, out double[] ty);
            return new ConstraintPath(xyOut, (double[])path.ZValues.Clone(), n, tx, ty);
        }

        var xy = new List<double>(path.VertexCount * 4);
        var z = new List<double>(path.VertexCount * 2);
        for (int segmentIndex = 0; segmentIndex < path.VertexCount - 1; segmentIndex++)
        {
            double ax = path.XyVertices[segmentIndex * 2];
            double ay = path.XyVertices[segmentIndex * 2 + 1];
            double bx = path.XyVertices[(segmentIndex + 1) * 2];
            double by = path.XyVertices[(segmentIndex + 1) * 2 + 1];
            double az = path.ZValues[segmentIndex];
            double bz = path.ZValues[segmentIndex + 1];

            double segLen = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int divisions = Math.Max(1, (int)Math.Ceiling(segLen / maxSegmentLength));

            for (int step = 0; step < divisions; step++)
            {
                double t = (double)step / divisions;
                AddConstraintSample(xy, z, ax + (bx - ax) * t, ay + (by - ay) * t, az + ((bz - az) * t), dedupTol);
            }
        }

        AddConstraintSample(
            xy,
            z,
            path.XyVertices[(path.VertexCount - 1) * 2],
            path.XyVertices[(path.VertexCount - 1) * 2 + 1],
            path.ZValues[path.VertexCount - 1],
            dedupTol);

        double[] xyArr = xy.ToArray();
        int count = xy.Count / 2;
        ComputeSmoothedTangents(xyArr, count, out double[] tangentX, out double[] tangentY);
        return new ConstraintPath(xyArr, z.ToArray(), count, tangentX, tangentY);
    }

    private static ConstraintPath ResampleConstraintPath(ConstraintPath path, double maxSegmentLength, double dedupTol)
    {
        if (path.VertexCount < 3 || maxSegmentLength <= dedupTol)
            return path;

        var cumulativeLengths = BuildConstraintCumulativeLengths(path.XyVertices, path.VertexCount);
        double totalLength = cumulativeLengths[^1];
        if (totalLength <= maxSegmentLength + dedupTol)
            return path;

        var xy = new List<double>(path.VertexCount * 2);
        var z = new List<double>(path.VertexCount);
        int sampleCount = Math.Max(2, (int)Math.Ceiling(totalLength / maxSegmentLength) + 1);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double targetDistance = sampleIndex == sampleCount - 1
                ? totalLength
                : Math.Min(sampleIndex * maxSegmentLength, totalLength);
            SampleConstraintPathAtDistance(path, cumulativeLengths, targetDistance, out double x, out double y, out double elevation);
            AddConstraintSample(xy, z, x, y, elevation, dedupTol);
        }

        double[] xyArr = xy.ToArray();
        int count = xyArr.Length / 2;
        ComputeSmoothedTangents(xyArr, count, out double[] tangentX, out double[] tangentY);
        return new ConstraintPath(xyArr, z.ToArray(), count, tangentX, tangentY);
    }

    private static double[] ResampleConstraintRow(
        double[] rowXy,
        double[] referenceXy,
        double maxSegmentLength,
        double dedupTol)
    {
        int vertexCount = Math.Min(rowXy.Length, referenceXy.Length) / 2;
        if (vertexCount < 3 || maxSegmentLength <= dedupTol)
            return rowXy;

        double[] cumulativeLengths = BuildConstraintCumulativeLengths(referenceXy, vertexCount);
        double totalLength = cumulativeLengths[^1];
        if (totalLength <= maxSegmentLength + dedupTol)
            return rowXy;

        var resampled = new List<double>(rowXy.Length);
        int sampleCount = Math.Max(2, (int)Math.Ceiling(totalLength / maxSegmentLength) + 1);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double targetDistance = sampleIndex == sampleCount - 1
                ? totalLength
                : Math.Min(sampleIndex * maxSegmentLength, totalLength);
            SampleConstraintRowAtDistance(rowXy, cumulativeLengths, vertexCount, targetDistance, out double x, out double y);
            if (resampled.Count >= 2)
            {
                double dx = x - resampled[^2];
                double dy = y - resampled[^1];
                if ((dx * dx) + (dy * dy) <= dedupTol * dedupTol)
                    continue;
            }

            resampled.Add(x);
            resampled.Add(y);
        }

        return resampled.ToArray();
    }

    private static double[] BuildConstraintCumulativeLengths(double[] xyVertices, int vertexCount)
    {
        var cumulativeLengths = new double[vertexCount];
        for (int i = 1; i < vertexCount; i++)
        {
            double dx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
            double dy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
            cumulativeLengths[i] = cumulativeLengths[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
        }

        return cumulativeLengths;
    }

    private static void SampleConstraintPathAtDistance(
        ConstraintPath path,
        double[] cumulativeLengths,
        double targetDistance,
        out double x,
        out double y,
        out double elevation)
    {
        int segmentIndex = FindConstraintDistanceSegment(cumulativeLengths, targetDistance);
        double segmentStartDistance = cumulativeLengths[segmentIndex - 1];
        double segmentEndDistance = cumulativeLengths[segmentIndex];
        double blend = segmentEndDistance <= segmentStartDistance + 1e-12
            ? 0.0
            : (targetDistance - segmentStartDistance) / (segmentEndDistance - segmentStartDistance);

        double ax = path.XyVertices[(segmentIndex - 1) * 2];
        double ay = path.XyVertices[(segmentIndex - 1) * 2 + 1];
        double bx = path.XyVertices[segmentIndex * 2];
        double by = path.XyVertices[segmentIndex * 2 + 1];
        double az = path.ZValues[segmentIndex - 1];
        double bz = path.ZValues[segmentIndex];
        x = ax + ((bx - ax) * blend);
        y = ay + ((by - ay) * blend);
        elevation = az + ((bz - az) * blend);
    }

    private static void SampleConstraintRowAtDistance(
        double[] rowXy,
        double[] cumulativeLengths,
        int vertexCount,
        double targetDistance,
        out double x,
        out double y)
    {
        int segmentIndex = FindConstraintDistanceSegment(cumulativeLengths, targetDistance);
        double segmentStartDistance = cumulativeLengths[segmentIndex - 1];
        double segmentEndDistance = cumulativeLengths[segmentIndex];
        double blend = segmentEndDistance <= segmentStartDistance + 1e-12
            ? 0.0
            : (targetDistance - segmentStartDistance) / (segmentEndDistance - segmentStartDistance);

        double ax = rowXy[(segmentIndex - 1) * 2];
        double ay = rowXy[(segmentIndex - 1) * 2 + 1];
        double bx = rowXy[segmentIndex * 2];
        double by = rowXy[segmentIndex * 2 + 1];
        x = ax + ((bx - ax) * blend);
        y = ay + ((by - ay) * blend);
    }

    private static int FindConstraintDistanceSegment(double[] cumulativeLengths, double targetDistance)
    {
        if (cumulativeLengths.Length < 2)
            return 1;

        int segmentIndex = 1;
        while (segmentIndex < cumulativeLengths.Length && cumulativeLengths[segmentIndex] < targetDistance)
            segmentIndex++;

        return Math.Min(segmentIndex, cumulativeLengths.Length - 1);
    }

    /// <summary>
    /// Computes a smooth unit tangent at each station of a polyline.
    /// Uses length-weighted averaging of adjacent segment directions.
    /// Zero-length segments are skipped. If adjacent segments are nearly
    /// anti-parallel (dot &lt; -0.7, i.e. ≥ 135° reversal), only the longer
    /// segment contributes to avoid spurious bisector normals at tight U-turns.
    /// Falls back to the longer adjacent segment if the weighted sum is degenerate.
    /// </summary>
    private static void ComputeSmoothedTangents(
        double[] xyVertices,
        int vertexCount,
        out double[] tangentX,
        out double[] tangentY)
    {
        tangentX = new double[vertexCount];
        tangentY = new double[vertexCount];

        if (vertexCount < 2)
            return;

        for (int i = 0; i < vertexCount; i++)
        {
            double sumX = 0.0, sumY = 0.0;

            // Previous segment: from i-1 to i
            double prevDx = 0, prevDy = 0, prevLen = 0;
            if (i > 0)
            {
                prevDx = xyVertices[i * 2]     - xyVertices[(i - 1) * 2];
                prevDy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
                prevLen = Math.Sqrt(prevDx * prevDx + prevDy * prevDy);
                if (prevLen < 1e-9) prevLen = 0; // zero-length — skip
            }

            // Next segment: from i to i+1
            double nextDx = 0, nextDy = 0, nextLen = 0;
            if (i < vertexCount - 1)
            {
                nextDx = xyVertices[(i + 1) * 2]     - xyVertices[i * 2];
                nextDy = xyVertices[(i + 1) * 2 + 1] - xyVertices[i * 2 + 1];
                nextLen = Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
                if (nextLen < 1e-9) nextLen = 0;
            }

            bool hasPrev = prevLen > 1e-9;
            bool hasNext = nextLen > 1e-9;

            if (hasPrev && hasNext)
            {
                // Normalized directions
                double pnx = prevDx / prevLen, pny = prevDy / prevLen;
                double nnx = nextDx / nextLen, nny = nextDy / nextLen;

                // Check for near-U-turn (dot < -0.7, roughly ≥ 135°)
                double dot = pnx * nnx + pny * nny;
                if (dot < -0.7)
                {
                    // Use only the longer segment to avoid bisector artifacts
                    if (prevLen >= nextLen) { sumX = pnx * prevLen; sumY = pny * prevLen; }
                    else                    { sumX = nnx * nextLen; sumY = nny * nextLen; }
                }
                else
                {
                    // Length-weighted blend of both segments
                    sumX = pnx * prevLen + nnx * nextLen;
                    sumY = pny * prevLen + nny * nextLen;
                }
            }
            else if (hasPrev)
            {
                sumX = prevDx; sumY = prevDy;
            }
            else if (hasNext)
            {
                sumX = nextDx; sumY = nextDy;
            }

            double len = Math.Sqrt(sumX * sumX + sumY * sumY);
            if (len < 1e-9)
            {
                // Fully degenerate — fall back to whichever segment is longer
                if (prevLen >= nextLen && prevLen > 1e-9)      { len = prevLen; sumX = prevDx; sumY = prevDy; }
                else if (nextLen > 1e-9)                        { len = nextLen; sumX = nextDx; sumY = nextDy; }
                else                                            { tangentX[i] = 1.0; tangentY[i] = 0.0; continue; }
                len = Math.Sqrt(sumX * sumX + sumY * sumY);
            }

            tangentX[i] = sumX / len;
            tangentY[i] = sumY / len;
        }
    }

    private static void ComputeInsideCornerGuideSuppression(
        double[] xyVertices,
        int vertexCount,
        out bool[] suppressLeftGuides,
        out bool[] suppressRightGuides)
    {
        suppressLeftGuides = new bool[vertexCount];
        suppressRightGuides = new bool[vertexCount];

        if (vertexCount < 3)
            return;

        const double minTurnAngleDeg = 30.0;
        double dotThreshold = Math.Cos(minTurnAngleDeg * Math.PI / 180.0);

        for (int i = 1; i < vertexCount - 1; i++)
        {
            double prevDx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
            double prevDy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
            double nextDx = xyVertices[(i + 1) * 2] - xyVertices[i * 2];
            double nextDy = xyVertices[(i + 1) * 2 + 1] - xyVertices[i * 2 + 1];

            double prevLen = Math.Sqrt(prevDx * prevDx + prevDy * prevDy);
            double nextLen = Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
            if (prevLen <= 1e-9 || nextLen <= 1e-9)
                continue;

            prevDx /= prevLen;
            prevDy /= prevLen;
            nextDx /= nextLen;
            nextDy /= nextLen;

            double dot = Math.Clamp((prevDx * nextDx) + (prevDy * nextDy), -1.0, 1.0);
            if (dot >= dotThreshold)
                continue;

            double turn = (prevDx * nextDy) - (prevDy * nextDx);
            if (Math.Abs(turn) <= 1e-9)
                continue;

            bool[] target = turn > 0.0 ? suppressLeftGuides : suppressRightGuides;
            int window = dot <= 0.5 ? 2 : 1;
            int start = Math.Max(0, i - window);
            int end = Math.Min(vertexCount - 1, i + window);
            for (int j = start; j <= end; j++)
                target[j] = true;
        }
    }

    private static double ComputeGuideSpacing(double width, double shoulderDistance)
    {
        double baseSpacing = Math.Max(width * 2.0, shoulderDistance * 2.0);
        return Math.Clamp(baseSpacing, 4.0, 15.0);
    }

    private static bool[] ComputeGuideSelection(
        double[] xyVertices,
        int vertexCount,
        double targetSpacing,
        bool[] suppressGuides)
    {
        var keepGuides = new bool[vertexCount];
        if (vertexCount == 0)
            return keepGuides;

        keepGuides[0] = true;
        if (vertexCount == 1)
            return keepGuides;

        int last = vertexCount - 1;
        keepGuides[last] = true;
        if (vertexCount == 2)
            return keepGuides;

        var cumulativeLength = new double[vertexCount];
        for (int i = 1; i < vertexCount; i++)
        {
            double dx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
            double dy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
            cumulativeLength[i] = cumulativeLength[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
        }

        double lastKeptLength = cumulativeLength[0];
        int keptInteriorCount = 0;
        for (int i = 1; i < last; i++)
        {
            if (suppressGuides[i])
                continue;

            if (cumulativeLength[i] - lastKeptLength < targetSpacing - 1e-9)
                continue;

            keepGuides[i] = true;
            lastKeptLength = cumulativeLength[i];
            keptInteriorCount++;
        }

        double totalLength = cumulativeLength[last];
        if ((totalLength - lastKeptLength) > targetSpacing * 1.5)
        {
            double targetLength = totalLength - targetSpacing;
            int bestIndex = -1;
            double bestError = double.MaxValue;
            for (int i = 1; i < last; i++)
            {
                if (suppressGuides[i] || keepGuides[i])
                    continue;

                double fromPrevious = cumulativeLength[i] - lastKeptLength;
                double toEnd = totalLength - cumulativeLength[i];
                if (fromPrevious < targetSpacing * 0.5 || toEnd < targetSpacing * 0.5)
                    continue;

                double error = Math.Abs(cumulativeLength[i] - targetLength);
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
                keepGuides[bestIndex] = true;
        }

        if (keptInteriorCount == 0)
        {
            int bestIndex = -1;
            double bestError = double.MaxValue;
            double targetLength = totalLength * 0.5;
            for (int i = 1; i < last; i++)
            {
                if (suppressGuides[i])
                    continue;

                double error = Math.Abs(cumulativeLength[i] - targetLength);
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
                keepGuides[bestIndex] = true;
        }

        return keepGuides;
    }

    private static bool[] BuildStationConstraintSelection(
        ConstraintPath constraintPath,
        PathSectionResolutionStatus[] leftStatuses,
        PathSectionResolutionStatus[] rightStatuses,
        double targetSpacing)
    {
        int vertexCount = constraintPath.VertexCount;
        var keepStations = new bool[vertexCount];
        if (vertexCount == 0)
            return keepStations;

        ComputeInsideCornerGuideSuppression(
            constraintPath.XyVertices,
            vertexCount,
            out bool[] suppressLeftGuides,
            out bool[] suppressRightGuides);

        var suppressStations = new bool[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            suppressStations[i] = suppressLeftGuides[i] || suppressRightGuides[i];

        keepStations = ComputeGuideSelection(
            constraintPath.XyVertices,
            vertexCount,
            Math.Max(targetSpacing, 1e-6),
            suppressStations);

        keepStations[0] = true;
        keepStations[vertexCount - 1] = true;
        int statusCount = Math.Min(vertexCount, Math.Min(leftStatuses.Length, rightStatuses.Length));
        for (int i = 1; i < statusCount; i++)
        {
            if (leftStatuses[i] == leftStatuses[i - 1] &&
                rightStatuses[i] == rightStatuses[i - 1])
            {
                continue;
            }

            keepStations[i - 1] = true;
            keepStations[i] = true;
        }

        return keepStations;
    }

    private static void AddConstraintSample(List<double> xy, List<double> z, double x, double y, double elevation, double dedupTol)
    {
        if (xy.Count >= 2)
        {
            double dx = x - xy[^2];
            double dy = y - xy[^1];
            if (dx * dx + dy * dy <= dedupTol * dedupTol)
                return;
        }

        xy.Add(x);
        xy.Add(y);
        z.Add(elevation);
    }

    private static void ComputeDirection(double[] xyVertices, int vertexCount, int index, out double dx, out double dy)
    {
        if (index == 0)
        {
            dx = xyVertices[2] - xyVertices[0];
            dy = xyVertices[3] - xyVertices[1];
        }
        else if (index == vertexCount - 1)
        {
            dx = xyVertices[index * 2] - xyVertices[(index - 1) * 2];
            dy = xyVertices[index * 2 + 1] - xyVertices[(index - 1) * 2 + 1];
        }
        else
        {
            dx = xyVertices[(index + 1) * 2] - xyVertices[(index - 1) * 2];
            dy = xyVertices[(index + 1) * 2 + 1] - xyVertices[(index - 1) * 2 + 1];
        }

        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12)
            len = 1.0;

        dx /= len;
        dy /= len;
    }

    private static void GetConstraintPathTangent(ConstraintPath path, int index, out double dx, out double dy)
    {
        if (path.TangentX.Length == path.VertexCount &&
            path.TangentY.Length == path.VertexCount &&
            index >= 0 &&
            index < path.VertexCount)
        {
            dx = path.TangentX[index];
            dy = path.TangentY[index];
            return;
        }

        ComputeDirection(path.XyVertices, path.VertexCount, index, out dx, out dy);
    }

    private static IEnumerable<ConstraintPath> CreateBoundaryClippedRuns(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        return CreateClippedRuns(
            xyVertices,
            zValues,
            vertexCount,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            PreparedBarriers.Empty,
            new SpatialHashGrid2D.QueryScratch(1),
            new List<int>(8));
    }

    private static IEnumerable<ConstraintPath> CreateClippedRuns(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (!hasBoundaryLoop)
        {
            yield return MakeGeometryConstraintPath(CopyLeadingDoubles(xyVertices, vertexCount * 2), CopyLeadingDoubles(zValues, vertexCount), vertexCount);
            yield break;
        }

        var runXy = new List<double>(vertexCount * 2);
        var runZ = new List<double>(vertexCount);

        for (int i = 0; i < vertexCount - 1; i++)
        {
            double startX = xyVertices[i * 2];
            double startY = xyVertices[i * 2 + 1];
            double startZ = zValues[i];
            double endX = xyVertices[(i + 1) * 2];
            double endY = xyVertices[(i + 1) * 2 + 1];
            double endZ = zValues[i + 1];

            if (barriers.Segments.Length > 0 &&
                GradingBarriers.TryClipSegment(
                    barriers,
                    startX,
                    startY,
                    endX,
                    endY,
                    barrierScratch,
                    barrierCandidates,
                    out double clippedEndX,
                    out double clippedEndY))
            {
                double segmentDx = endX - startX;
                double segmentDy = endY - startY;
                double segmentLengthSquared = (segmentDx * segmentDx) + (segmentDy * segmentDy);
                double clipT = segmentLengthSquared <= 1e-12
                    ? 0.0
                    : (((clippedEndX - startX) * segmentDx) + ((clippedEndY - startY) * segmentDy)) / segmentLengthSquared;
                endX = clippedEndX;
                endY = clippedEndY;
                endZ = InterpolateSectionValue(startZ, endZ, Math.Clamp(clipT, 0.0, 1.0));
            }

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            if (pieces.Count == 0)
            {
                    if (runZ.Count >= 2)
                        yield return SimplifyConstraintPathWithClipper(
                            MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count),
                            tolerance);

                    runXy.Clear();
                    runZ.Clear();
                    continue;
            }

            foreach (ClippedSegment piece in pieces)
            {
                if (runZ.Count > 0)
                {
                    double dx = runXy[^2] - piece.StartX;
                    double dy = runXy[^1] - piece.StartY;
                    if ((dx * dx) + (dy * dy) > tolerance * tolerance)
                    {
                        yield return SimplifyConstraintPathWithClipper(
                            MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count),
                            tolerance);
                        runXy.Clear();
                        runZ.Clear();
                    }
                }

                AppendRunPoint(runXy, runZ, piece.StartX, piece.StartY, piece.StartZ, tolerance);
                AppendRunPoint(runXy, runZ, piece.EndX, piece.EndY, piece.EndZ, tolerance);
            }
        }

        if (runZ.Count >= 2)
            yield return SimplifyConstraintPathWithClipper(
                MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count),
                tolerance);
    }

    private static void AppendRunPoint(
        List<double> runXy,
        List<double> runZ,
        double x,
        double y,
        double z,
        double tolerance)
    {
        if (runZ.Count > 0)
        {
            double dx = runXy[^2] - x;
            double dy = runXy[^1] - y;
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
            {
                runXy[^2] = x;
                runXy[^1] = y;
                runZ[^1] = z;
                return;
            }
        }

        runXy.Add(x);
        runXy.Add(y);
        runZ.Add(z);
    }

    private static void AddConstraintPolyline(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] xyVertices,
        double[] zValues,
        int vertexCount)
    {
        if (vertexCount < 2)
            return;

        var points = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            points[i * 3] = xyVertices[i * 2];
            points[i * 3 + 1] = xyVertices[i * 2 + 1];
            points[i * 3 + 2] = zValues[i];
        }

        constraints.Add(new SurfaceRemesher.ConstraintPolyline(points, vertexCount, IsClosed: false, PreserveInputElevation: false));
    }

    private static void AddBoundaryClippedConstraintRuns(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        foreach (ConstraintPath run in CreateBoundaryClippedRuns(
                     xyVertices,
                     zValues,
                     vertexCount,
                     hasBoundaryLoop,
                     boundaryLoop,
                     boundaryVertexCount,
                     tolerance))
        {
            AddConstraintPolyline(constraints, run.XyVertices, run.ZValues, run.VertexCount);
        }
    }

    private static void AddPathStationConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        ConstraintPath centerPath,
        double[] leftRoadXy,
        double[] rightRoadXy,
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        double[] leftShoulderZ,
        double[] rightShoulderZ,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        bool[]? keepStations,
        int vertexCount,
        double tolerance)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (keepStations != null && (i >= keepStations.Length || !keepStations[i]))
                continue;

            var points = new List<double>(15);
            double centerX = centerPath.XyVertices[i * 2];
            double centerY = centerPath.XyVertices[i * 2 + 1];
            double centerZ = centerPath.ZValues[i];

            AppendConstraintPoint(points, leftShoulderXy[i * 2], leftShoulderXy[i * 2 + 1], leftShoulderZ[i], tolerance);
            AppendConstraintPoint(points, leftRoadXy[i * 2], leftRoadXy[i * 2 + 1], centerZ, tolerance);
            AppendConstraintPoint(points, centerX, centerY, centerZ, tolerance);
            AppendConstraintPoint(points, rightRoadXy[i * 2], rightRoadXy[i * 2 + 1], centerZ, tolerance);
            AppendConstraintPoint(points, rightShoulderXy[i * 2], rightShoulderXy[i * 2 + 1], rightShoulderZ[i], tolerance);

            int pointCount = points.Count / 3;
            if (pointCount < 2)
                continue;

            var stationXy = new double[pointCount * 2];
            var stationZ = new double[pointCount];
            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                stationXy[pointIndex * 2] = points[pointIndex * 3];
                stationXy[(pointIndex * 2) + 1] = points[(pointIndex * 3) + 1];
                stationZ[pointIndex] = points[(pointIndex * 3) + 2];
            }

            AddBoundaryClippedConstraintRuns(
                constraints,
                stationXy,
                stationZ,
                pointCount,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);
        }
    }

    private static void AppendConstraintPoint(List<double> points, double x, double y, double z, double tolerance)
    {
        if (points.Count >= 3)
        {
            double dx = x - points[^3];
            double dy = y - points[^2];
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
            {
                points[^3] = x;
                points[^2] = y;
                points[^1] = z;
                return;
            }
        }

        points.Add(x);
        points.Add(y);
        points.Add(z);
    }

    private static ConstraintPath SimplifyConstraintPathWithClipper(ConstraintPath path, double tolerance)
    {
        if (path.VertexCount < 3 ||
            IsApproximatelyStraight(path.XyVertices, path.VertexCount, tolerance) ||
            !ClipperGeometry.TrySimplifyOpenPolyline(path.XyVertices, tolerance, out double[] simplifiedXy))
        {
            return path;
        }

        int simplifiedCount = simplifiedXy.Length / 2;
        if (simplifiedCount < 2)
            return path;

        var simplifiedZ = new double[simplifiedCount];
        for (int i = 0; i < simplifiedCount; i++)
        {
            if (!TrySampleConstraintPathZ(
                path.XyVertices,
                path.ZValues,
                path.VertexCount,
                simplifiedXy[i * 2],
                simplifiedXy[i * 2 + 1],
                out simplifiedZ[i]))
            {
                return path;
            }
        }

        return MakeGeometryConstraintPath(simplifiedXy, simplifiedZ, simplifiedCount);
    }

    private static bool IsApproximatelyStraight(double[] xyVertices, int vertexCount, double tolerance)
    {
        if (vertexCount < 3)
            return true;

        double ax = xyVertices[0];
        double ay = xyVertices[1];
        double bx = xyVertices[(vertexCount - 1) * 2];
        double by = xyVertices[((vertexCount - 1) * 2) + 1];
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSq = (dx * dx) + (dy * dy);
        if (lengthSq <= tolerance * tolerance)
            return true;

        double toleranceSq = tolerance * tolerance;
        for (int i = 1; i < vertexCount - 1; i++)
        {
            double px = xyVertices[i * 2];
            double py = xyVertices[i * 2 + 1];
            double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSq, 0.0, 1.0);
            double projectedX = ax + (dx * t);
            double projectedY = ay + (dy * t);
            double offsetX = px - projectedX;
            double offsetY = py - projectedY;
            if (((offsetX * offsetX) + (offsetY * offsetY)) > toleranceSq)
                return false;
        }

        return true;
    }

    private static bool TrySampleConstraintPathZ(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double px,
        double py,
        out double sampledZ)
    {
        sampledZ = 0.0;
        double bestDistanceSq = double.MaxValue;
        if (vertexCount < 2)
            return false;

        for (int i = 0; i < vertexCount - 1; i++)
        {
            double ax = xyVertices[i * 2];
            double ay = xyVertices[i * 2 + 1];
            double bx = xyVertices[(i + 1) * 2];
            double by = xyVertices[(i + 1) * 2 + 1];
            double dx = bx - ax;
            double dy = by - ay;
            double segmentLengthSq = (dx * dx) + (dy * dy);
            double t = 0.0;
            if (segmentLengthSq > 1e-18)
                t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / segmentLengthSq, 0.0, 1.0);

            double projectedX = ax + (dx * t);
            double projectedY = ay + (dy * t);
            double offsetX = px - projectedX;
            double offsetY = py - projectedY;
            double distanceSq = (offsetX * offsetX) + (offsetY * offsetY);
            if (distanceSq >= bestDistanceSq)
                continue;

            bestDistanceSq = distanceSq;
            sampledZ = InterpolateSectionValue(zValues[i], zValues[i + 1], t);
        }

        return bestDistanceSq < double.MaxValue;
    }

    private static int NormalizeConstraintPointCount(SurfaceRemesher.ConstraintPolyline constraint, double tolerance)
    {
        if (!constraint.IsClosed || constraint.PointCount < 3)
            return constraint.PointCount;

        int last = constraint.PointCount - 1;
        double dx = constraint.Points[last * 3] - constraint.Points[0];
        double dy = constraint.Points[last * 3 + 1] - constraint.Points[1];
        return dx * dx + dy * dy <= tolerance * tolerance
            ? last
            : constraint.PointCount;
    }

    private static bool IsBlockedByBarrier(
        PreparedBarriers preparedBarriers,
        double halfWidth,
        ClosestPathLocation closest,
        double px,
        double py,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (preparedBarriers.Segments.Length == 0)
            return false;

        double side = closest.SideSign >= 0.0 ? 1.0 : -1.0;
        double normalX = -closest.DirectionY;
        double normalY = closest.DirectionX;
        double edgeX = closest.ProjectedX + (normalX * halfWidth * side);
        double edgeY = closest.ProjectedY + (normalY * halfWidth * side);

        return GradingBarriers.IsCrossedByBarrier(
            preparedBarriers, edgeX, edgeY, px, py, barrierScratch, barrierCandidates);
    }

    private static double[] CopyLeadingDoubles(double[] values, int length)
    {
        var copy = new double[length];
        Array.Copy(values, copy, length);
        return copy;
    }

    private static bool HasDistinctShoulderSamples(double[] edgeXy, double[] shoulderXy, int vertexCount, double tolerance)
    {
        double tolSq = tolerance * tolerance;
        for (int i = 0; i < vertexCount; i++)
        {
            double dx = shoulderXy[i * 2] - edgeXy[i * 2];
            double dy = shoulderXy[(i * 2) + 1] - edgeXy[(i * 2) + 1];
            if ((dx * dx) + (dy * dy) > tolSq)
                return true;
        }

        return false;
    }

    private static int[]? AddShoulderConstraint(
        double[] shoulderXy,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        Func<double, double, int> addVertex,
        List<(int a, int b)> segList,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        var indices = new int[vertexCount];
        Array.Fill(indices, -1);
        bool hasSegment = false;

        for (int i = 0; i < vertexCount - 1; i++)
        {
            double startX = shoulderXy[i * 2];
            double startY = shoulderXy[i * 2 + 1];
            double endX = shoulderXy[(i + 1) * 2];
            double endY = shoulderXy[(i + 1) * 2 + 1];

            bool clippedByBarrier = GradingBarriers.TryClipSegment(
                barriers, startX, startY, endX, endY,
                barrierScratch, barrierCandidates,
                out endX, out endY);

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                startX, startY, 0.0,
                endX, endY, 0.0,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            foreach (ClippedSegment piece in pieces)
            {
                int startIndex = piece.StartT <= 1e-9
                    ? EnsureShoulderSampleIndex(indices, i, shoulderXy, addVertex)
                    : addVertex(piece.StartX, piece.StartY);
                int endIndex = (!clippedByBarrier && piece.EndT >= 1.0 - 1e-9)
                    ? EnsureShoulderSampleIndex(indices, i + 1, shoulderXy, addVertex)
                    : addVertex(piece.EndX, piece.EndY);

                if (startIndex == endIndex)
                    continue;

                segList.Add((startIndex, endIndex));
                hasSegment = true;
            }
        }

        return hasSegment ? indices : null;
    }

    private static void AddShoulderGuideConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] roadXy,
        double[] shoulderXy,
        double[] roadZValues,
        double[] shoulderZValues,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        AddShoulderGuideConstraints(
            constraints,
            roadXy,
            shoulderXy,
            roadZValues,
            shoulderZValues,
            vertexCount,
            keepGuides,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            PreparedBarriers.Empty,
            new SpatialHashGrid2D.QueryScratch(1),
            new List<int>(8));
    }

    private static void AddShoulderGuideConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] roadXy,
        double[] shoulderXy,
        double[] roadZValues,
        double[] shoulderZValues,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (keepGuides != null && !keepGuides[i])
                continue;

            double roadX = roadXy[i * 2];
            double roadY = roadXy[i * 2 + 1];
            double shoulderX = shoulderXy[i * 2];
            double shoulderY = shoulderXy[i * 2 + 1];
            double shoulderZ = shoulderZValues[i];

            if (barriers.Segments.Length > 0)
            {
                double originalShoulderX = shoulderX;
                double originalShoulderY = shoulderY;
                GradingBarriers.TryClipSegment(
                    barriers,
                    roadX,
                    roadY,
                    shoulderX,
                    shoulderY,
                    barrierScratch,
                    barrierCandidates,
                    out shoulderX,
                    out shoulderY);

                double dx = originalShoulderX - roadX;
                double dy = originalShoulderY - roadY;
                double lengthSquared = (dx * dx) + (dy * dy);
                if (lengthSquared > 1e-12)
                {
                    double clipT = (((shoulderX - roadX) * dx) + ((shoulderY - roadY) * dy)) / lengthSquared;
                    shoulderZ = InterpolateSectionValue(roadZValues[i], shoulderZValues[i], Math.Clamp(clipT, 0.0, 1.0));
                }
            }

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                roadX,
                roadY,
                roadZValues[i],
                shoulderX,
                shoulderY,
                shoulderZ,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            foreach (ClippedSegment piece in pieces)
            {
                double dx = piece.EndX - piece.StartX;
                double dy = piece.EndY - piece.StartY;
                if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                    continue;

                constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                    new[]
                    {
                        piece.StartX, piece.StartY, piece.StartZ,
                        piece.EndX, piece.EndY, piece.EndZ
                    },
                    PointCount: 2,
                    IsClosed: false,
                    PreserveInputElevation: false));
            }
        }
    }

    private static void AddShoulderGuideSegments(
        List<double> xyList,
        int[] roadIndices,
        double[]? shoulderXy,
        int[]? shoulderIndices,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        Func<double, double, int> addVertex,
        List<(int a, int b)> segList,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (shoulderXy == null)
            return;

        for (int i = 0; i < vertexCount; i++)
        {
            if (keepGuides != null && !keepGuides[i])
                continue;

            double roadX = xyList[roadIndices[i] * 2];
            double roadY = xyList[roadIndices[i] * 2 + 1];
            double shoulderX = shoulderXy[i * 2];
            double shoulderY = shoulderXy[i * 2 + 1];

            bool clippedByBarrier = GradingBarriers.TryClipSegment(
                barriers, roadX, roadY, shoulderX, shoulderY,
                barrierScratch, barrierCandidates,
                out shoulderX, out shoulderY);

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                roadX, roadY, 0.0,
                shoulderX, shoulderY, 0.0,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            foreach (ClippedSegment piece in pieces)
            {
                int startIndex = piece.StartT <= 1e-9
                    ? roadIndices[i]
                    : addVertex(piece.StartX, piece.StartY);
                int endIndex = (!clippedByBarrier && piece.EndT >= 1.0 - 1e-9 && shoulderIndices != null && shoulderIndices[i] >= 0)
                    ? shoulderIndices[i]
                    : addVertex(piece.EndX, piece.EndY);

                if (startIndex != endIndex)
                    segList.Add((startIndex, endIndex));
            }
        }
    }

    private static int EnsureShoulderSampleIndex(
        int[] indices,
        int pointIndex,
        double[] shoulderXy,
        Func<double, double, int> addVertex)
    {
        if (indices[pointIndex] >= 0)
            return indices[pointIndex];

        indices[pointIndex] = addVertex(shoulderXy[pointIndex * 2], shoulderXy[pointIndex * 2 + 1]);
        return indices[pointIndex];
    }

    private static bool IsInsideOrOnBoundary(
        double x,
        double y,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        return BoundaryClipper.IsInsideOrOnBoundary(x, y, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, tolerance);
    }

    private static bool TryFindClosestPathSample(PathDefinition path, double px, double py, out double closestDist, out double closestPathZ)
    {
        if (!TryFindClosestPathLocation(path.XyVertices, path.ZValues, path.VertexCount, px, py, out ClosestPathLocation closest))
        {
            closestDist = double.MaxValue;
            closestPathZ = 0.0;
            return false;
        }

        closestDist = closest.Distance;
        closestPathZ = closest.PathZ;
        return true;
    }

    private static bool TryFindClosestPathLocation(
        ConstraintPath path,
        double px,
        double py,
        out ClosestPathLocation closest)
    {
        return TryFindClosestPathLocation(
            path.XyVertices, path.ZValues, path.VertexCount,
            path.TangentX, path.TangentY,
            px, py, out closest);
    }

    private static bool TryFindClosestPathLocation(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double px,
        double py,
        out ClosestPathLocation closest)
    {
        return TryFindClosestPathLocation(
            xyVertices, zValues, vertexCount,
            null, null,
            px, py, out closest);
    }

    /// <summary>
    /// Finds the closest point on the path polyline and returns a
    /// <see cref="ClosestPathLocation"/>. When smooth tangent arrays are
    /// provided, <c>SideSign</c> and <c>DirectionX/Y</c> are computed from
    /// the smoothly interpolated tangent at the closest position rather than
    /// the raw segment direction. This eliminates the discrete SideSign flip
    /// that occurs at segment-ownership (Voronoi) boundaries near path bends,
    /// which was the primary cause of cut/fill polarity inversions in the
    /// shoulder reference-profile lookup.
    /// </summary>
    private static bool TryFindClosestPathLocation(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double[]? tangentX,
        double[]? tangentY,
        double px,
        double py,
        out ClosestPathLocation closest)
    {
        double closestDistSq = double.MaxValue;
        closest = default;

        for (int s = 0; s < vertexCount - 1; s++)
        {
            double ax = xyVertices[s * 2];
            double ay = xyVertices[s * 2 + 1];
            double bx = xyVertices[(s + 1) * 2];
            double by = xyVertices[(s + 1) * 2 + 1];

            double sdx = bx - ax;
            double sdy = by - ay;
            double segLen = sdx * sdx + sdy * sdy;
            if (segLen < 1e-20)
                continue;

            double t = ((px - ax) * sdx + (py - ay) * sdy) / segLen;
            t = Math.Clamp(t, 0.0, 1.0);

            double projX = ax + t * sdx;
            double projY = ay + t * sdy;
            double dx = px - projX;
            double dy = py - projY;
            double distSq = (dx * dx) + (dy * dy);

            if (distSq < closestDistSq)
            {
                double segLength = Math.Sqrt(segLen);

                // Compute direction and SideSign from smooth tangent when available.
                // Linearly interpolate station tangents at parameter t, then normalize.
                // Falls back to raw segment direction when no tangents are provided.
                double dirX, dirY, sideSign;
                if (tangentX != null && tangentY != null &&
                    s < tangentX.Length && s + 1 < tangentX.Length)
                {
                    double blendX = tangentX[s] + t * (tangentX[s + 1] - tangentX[s]);
                    double blendY = tangentY[s] + t * (tangentY[s + 1] - tangentY[s]);
                    double blendLen = Math.Sqrt(blendX * blendX + blendY * blendY);
                    if (blendLen > 1e-9)
                    {
                        dirX = blendX / blendLen;
                        dirY = blendY / blendLen;
                    }
                    else
                    {
                        // Interpolated tangent degenerate — fall back to segment direction
                        dirX = sdx / segLength;
                        dirY = sdy / segLength;
                    }
                    // SideSign: cross product of smooth tangent with (query − projected)
                    sideSign = dirX * (py - projY) - dirY * (px - projX);
                }
                else
                {
                    dirX = sdx / segLength;
                    dirY = sdy / segLength;
                    sideSign = (sdx * (py - ay)) - (sdy * (px - ax));
                }

                closestDistSq = distSq;
                closest = new ClosestPathLocation(
                    s,
                    t,
                    Math.Sqrt(distSq),
                    zValues[s] + t * (zValues[s + 1] - zValues[s]),
                    sideSign,
                    projX,
                    projY,
                    dirX,
                    dirY);
            }
        }

        return closestDistSq < double.MaxValue;
    }

    /// <summary>
    /// Build GradingResult with volumes and daylight line.
    /// </summary>
    private static GradingResult BuildResult(
        double[] outXy, double[] origZ, double[] newZ,
        double[] finalVerts, int vertCount,
        int[] finalFaces, int faceCount,
        IReadOnlyList<OutputPolyline>? outputPolylines = null,
        IReadOnlyList<GradingPatch>? patchSummaries = null,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<GradingDiagnostic>? structuredDiagnostics = null)
    {
        double cutVol = 0, fillVol = 0;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = finalFaces[f * 3], i1 = finalFaces[f * 3 + 1], i2 = finalFaces[f * 3 + 2];

            double area2d = Math.Abs(
                (outXy[i1 * 2] - outXy[i0 * 2]) * (outXy[i2 * 2 + 1] - outXy[i0 * 2 + 1])
              - (outXy[i2 * 2] - outXy[i0 * 2]) * (outXy[i1 * 2 + 1] - outXy[i0 * 2 + 1])
            ) * 0.5;

            double dz0 = newZ[i0] - origZ[i0];
            double dz1 = newZ[i1] - origZ[i1];
            double dz2 = newZ[i2] - origZ[i2];
            double avgDz = (dz0 + dz1 + dz2) / 3.0;

            double vol = area2d * avgDz;
            if (vol > 0) fillVol += vol;
            else cutVol += -vol;
        }

        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = finalFaces[f * 3], i1 = finalFaces[f * 3 + 1], i2 = finalFaces[f * 3 + 2];
            CheckDaylightEdge(i0, i1, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i1, i2, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i2, i0, outXy, newZ, origZ, processedEdges, daylightPts);
        }

        return new GradingResult(
            finalVerts, vertCount,
            finalFaces, faceCount,
            cutVol, fillVol,
            daylightPts.ToArray(), daylightPts.Count / 3,
            outputPolylines,
            diagnostics,
            patchSummaries: patchSummaries,
            structuredDiagnostics: structuredDiagnostics);
    }

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }

    private static void CheckDaylightEdge(int a, int b,
        double[] xy, double[] newZ, double[] origZ,
        HashSet<long> processed, List<double> pts)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!processed.Add(key)) return;

        double dzA = newZ[a] - origZ[a];
        double dzB = newZ[b] - origZ[b];
        const double threshold = 0.001;

        if ((dzA > threshold && dzB < -threshold) || (dzA < -threshold && dzB > threshold))
        {
            double t = dzA / (dzA - dzB);
            pts.Add(xy[a * 2] + t * (xy[b * 2] - xy[a * 2]));
            pts.Add(xy[a * 2 + 1] + t * (xy[b * 2 + 1] - xy[a * 2 + 1]));
            pts.Add(newZ[a] + t * (newZ[b] - newZ[a]));
        }
        else if (Math.Abs(dzA) <= threshold && Math.Abs(dzB) > threshold)
        {
            pts.Add(xy[a * 2]); pts.Add(xy[a * 2 + 1]); pts.Add(newZ[a]);
        }
        else if (Math.Abs(dzB) <= threshold && Math.Abs(dzA) > threshold)
        {
            pts.Add(xy[b * 2]); pts.Add(xy[b * 2 + 1]); pts.Add(newZ[b]);
        }
    }
}
