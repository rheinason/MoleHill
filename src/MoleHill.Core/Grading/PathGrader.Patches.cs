using MoleHill.Core.Engine;
using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    /// <summary>
    /// Re-triangulate with road edges as constrained segments, then grade Z.
    /// </summary>
    private static GradingResult? GradeWithEdges(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double modelTolerance,
        out string? errorMessage,
        out bool fatalError)
    {
        errorMessage = null;
        fatalError = false;

        // Build barrier index from hard constraints for shoulder clipping and road-edge validation.
        PreparedBarriers roadBarriers = GradingBarriers.Build(hardConstraints);
        var barrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(roadBarriers.Segments.Length, 1));
        var barrierCandidates = new List<int>(8);

        // Validate that no road center/edge segment crosses a hard constraint.
        if (roadBarriers.Segments.Length > 0)
        {
            double endpointTouchTolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
            foreach (var path in paths)
            {
                double halfWidth = path.Width * 0.5;
                int vc = path.VertexCount;
                for (int i = 0; i < vc - 1; i++)
                {
                    double endpointCapTolerance = Math.Max(endpointTouchTolerance, halfWidth);
                    double startTouchTolerance = i == 0 ? endpointCapTolerance : endpointTouchTolerance;
                    double endTouchTolerance = i == vc - 2 ? endpointCapTolerance : endpointTouchTolerance;
                    double cx0 = path.XyVertices[i * 2],     cy0 = path.XyVertices[i * 2 + 1];
                    double cx1 = path.XyVertices[(i + 1) * 2], cy1 = path.XyVertices[(i + 1) * 2 + 1];
                    ComputeDirection(path.XyVertices, vc, i, out double dx, out double dy);
                    double roadPx = -dy * halfWidth, roadPy = dx * halfWidth;

                    // Check center, left edge, right edge
                    if (GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0, cy0, cx1, cy1, startTouchTolerance, endTouchTolerance, barrierScratch, barrierCandidates) ||
                        GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0 + roadPx, cy0 + roadPy, cx1 + roadPx, cy1 + roadPy, startTouchTolerance, endTouchTolerance, barrierScratch, barrierCandidates) ||
                        GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0 - roadPx, cy0 - roadPy, cx1 - roadPx, cy1 - roadPy, startTouchTolerance, endTouchTolerance, barrierScratch, barrierCandidates))
                    {
                        errorMessage = "Road edge crosses a hard constraint. Redesign the path or convert the conflicting constraint to a contour.";
                        fatalError = true;
                        return null;
                    }
                }
            }
        }
        return GradeWithConstraintInsertionTopology(
            vertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            hardConstraints,
            modelTolerance,
            out errorMessage);
    }

    private static GradingResult? GradeWithConstraintInsertionTopology(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double modelTolerance,
        out string? errorMessage)
    {
        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        var diagnostics = new GradingDiagnosticCollector();
        var outputPolylines = new List<OutputPolyline>(paths.Length * 2);
        ConstraintSet pathConstraintSet = BuildConstraintSetInternal(
            vertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            tolerance,
            hardConstraints,
            outputPolylines,
            diagnostics);

        if (!MeshConstraintTopologyInserter.TryInsert(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pathConstraintSet.Constraints,
                tolerance,
                out double[] topologyVertices,
                out int topologyVertexCount,
                out int[] topologyFaces,
                out int topologyFaceCount,
                out string? topologyError))
        {
            errorMessage = topologyError ?? "Topology-preserving path insertion failed.";
            return null;
        }

        double[] gradedVertices = ApplyGradingZ(
            topologyVertices,
            topologyVertexCount,
            topologyFaces,
            topologyFaceCount,
            paths,
            hardConstraints,
            out _);

        var outXy = new double[topologyVertexCount * 2];
        var origZ = new double[topologyVertexCount];
        var newZ = new double[topologyVertexCount];
        for (int i = 0; i < topologyVertexCount; i++)
        {
            outXy[i * 2] = topologyVertices[i * 3];
            outXy[i * 2 + 1] = topologyVertices[i * 3 + 1];
            origZ[i] = topologyVertices[i * 3 + 2];
            newZ[i] = gradedVertices[i * 3 + 2];
        }

        diagnostics.AddInformation(
            "grade_path.topology_mode.constraint_insertion",
            $"Grade Path topology mode: constraint insertion ({vertexCount:N0} verts/{faceCount:N0} faces -> {topologyVertexCount:N0} verts/{topologyFaceCount:N0} faces).",
            operation: "Grade Path");
        errorMessage = null;
        return BuildResult(
            outXy,
            origZ,
            newZ,
            gradedVertices,
            topologyVertexCount,
            topologyFaces,
            topologyFaceCount,
            outputPolylines,
            BuildPathPatchSummaries(paths),
            diagnostics.ToMessages(),
            diagnostics.ToStructuredDiagnostics());
    }

    private static bool TryBuildPathSeamLoop(
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        ConstraintPath samplePath,
        double tolerance,
        out double[] seamLoopXy)
    {
        seamLoopXy = Array.Empty<double>();
        int vertexCount = samplePath.VertexCount;
        if (vertexCount < 2)
            return false;

        if (!IsClosedPath(samplePath.XyVertices, vertexCount, tolerance))
        {
            double startCapDepth = ComputeStructuredPathEndCapDepth(
                leftShoulderXy,
                rightShoulderXy,
                samplePath.XyVertices,
                vertexCount,
                sectionIndex: 0,
                tolerance);
            double endCapDepth = ComputeStructuredPathEndCapDepth(
                leftShoulderXy,
                rightShoulderXy,
                samplePath.XyVertices,
                vertexCount,
                sectionIndex: vertexCount - 1,
                tolerance);
            leftShoulderXy = BuildStructuredPathRowXyWithCaps(leftShoulderXy, samplePath, vertexCount, startCapDepth, endCapDepth);
            rightShoulderXy = BuildStructuredPathRowXyWithCaps(rightShoulderXy, samplePath, vertexCount, startCapDepth, endCapDepth);
            vertexCount += 2;
        }

        var points = new List<double>(vertexCount * 4);
        for (int i = 0; i < vertexCount; i++)
            AddLoopPoint(points, leftShoulderXy[i * 2], leftShoulderXy[i * 2 + 1], tolerance);
        for (int i = vertexCount - 1; i >= 0; i--)
            AddLoopPoint(points, rightShoulderXy[i * 2], rightShoulderXy[i * 2 + 1], tolerance);

        if (points.Count < 6)
            return false;

        double[] rawLoop = points.ToArray();
        if (!TryRegularizePathClosedLoop(rawLoop, tolerance, out seamLoopXy))
            seamLoopXy = rawLoop;

        return seamLoopXy.Length >= 6 && Math.Abs(ClipperGeometry.SignedArea(seamLoopXy)) > tolerance * tolerance;
    }

    private static PatchMeshResult? TryBuildPathPatchMesh(
        int pathIndex,
        PathDefinition[] allPaths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        TerrainFaceGrid originalFaceGrid,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        ConstraintPath samplePath,
        double[] leftRoadXy,
        double[] rightRoadXy,
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        double[] leftShoulderZ,
        double[] rightShoulderZ,
        int vertexCount,
        double[] seamLoopXy,
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        double tolerance,
        out string? errorMessage)
    {
        errorMessage = null;
        int seamVertexCount = seamLoopXy.Length / 2;
        if (seamVertexCount < 3)
        {
            errorMessage = "Grade Path patch requires a valid seam loop.";
            return null;
        }

        var patchDiagnostics = new List<string>(2);
        PatchMeshResult? structuredPatch = TryBuildStructuredPathPatchMesh(
            allPaths,
            barrierConstraints,
            originalFaceGrid,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            samplePath,
            leftRoadXy,
            rightRoadXy,
            leftShoulderXy,
            rightShoulderXy,
            leftShoulderZ,
            rightShoulderZ,
            vertexCount,
            seamLoopXy,
            tolerance);
        if (structuredPatch != null)
        {
            if (TryValidatePathPatchForStitching(
                    seamLoopXy,
                    structuredPatch.Vertices,
                    structuredPatch.Faces,
                    structuredPatch.FaceCount,
                    outsideVertices,
                    outsideFaces,
                    outsideFaceCount,
                    tolerance,
                    out double[] structuredBoundaryLoopXy,
                    out SeamGraph? structuredSeamGraph,
                    out string? structuredValidationError))
            {
                patchDiagnostics.Add($"Grade Path[{pathIndex}] patch builder: structured patch selected.");
                return new PatchMeshResult
                {
                    Vertices = structuredPatch.Vertices,
                    VertexCount = structuredPatch.VertexCount,
                    Faces = structuredPatch.Faces,
                    FaceCount = structuredPatch.FaceCount,
                    StitchLoopXy = structuredBoundaryLoopXy,
                    Diagnostics = patchDiagnostics
                };
            }

            string rejectionDetail = structuredValidationError ?? "structured patch failed seam validation.";
            if (structuredSeamGraph != null)
            {
                rejectionDetail =
                    $"{rejectionDetail} (outside={structuredSeamGraph.TerrainMatchedSegments}/{structuredSeamGraph.SeamVertexCount}, patch-near={structuredSeamGraph.PatchBoundarySegmentsNearSeam}, outside-near={structuredSeamGraph.TerrainBoundarySegmentsNearSeam}).";
            }

            patchDiagnostics.Add($"Grade Path[{pathIndex}] structured patch rejected before merge: {rejectionDetail}");
        }
        else if (!IsClosedPath(samplePath.XyVertices, samplePath.VertexCount, tolerance))
        {
            patchDiagnostics.Add($"Grade Path[{pathIndex}] structured patch builder returned no result; trying triangulated patch.");
        }

        int shoulderGuideRowCount = DeterminePathShoulderGuideRowCount(
            leftRoadXy,
            rightRoadXy,
            leftShoulderXy,
            rightShoulderXy,
            vertexCount,
            tolerance);
        bool leftShoulderPlanar = ShouldSimplifyPlanarPathStrip(leftRoadXy, leftShoulderXy, samplePath.ZValues, leftShoulderZ, vertexCount, tolerance);
        bool rightShoulderPlanar = ShouldSimplifyPlanarPathStrip(rightRoadXy, rightShoulderXy, samplePath.ZValues, rightShoulderZ, vertexCount, tolerance);
        bool roadSurfacePlanar = IsIsoElevationStrip(samplePath.ZValues, samplePath.ZValues, vertexCount, tolerance);
        bool shouldTryPlanarStripSimplification = roadSurfacePlanar || leftShoulderPlanar || rightShoulderPlanar;
        if (shouldTryPlanarStripSimplification)
        {
            patchDiagnostics.Add(
                $"Grade Path[{pathIndex}] planar strip checks: road={roadSurfacePlanar}, left shoulder={leftShoulderPlanar}, right shoulder={rightShoulderPlanar}; trying simplified triangulated patch first.");
        }

        string? lastTriangulationError = null;
        string? lastTriangulatedValidationError = null;
        int[] guideRowAttempts = shoulderGuideRowCount > 0
            ? new[] { shoulderGuideRowCount, 0 }
            : new[] { 0 };
        bool[] shoulderRailAttempts = new[] { true, false };
        bool[] planarStripAttempts = shouldTryPlanarStripSimplification
            ? new[] { true, false }
            : new[] { false };

        foreach (bool simplifyPlanarStrips in planarStripAttempts)
        {
            foreach (int guideRowAttempt in guideRowAttempts)
            {
                foreach (bool includeShoulderRails in shoulderRailAttempts)
                {
                    int leftGuideRowCount = simplifyPlanarStrips && leftShoulderPlanar ? 0 : guideRowAttempt;
                    int rightGuideRowCount = simplifyPlanarStrips && rightShoulderPlanar ? 0 : guideRowAttempt;
                    bool includeRoadCenterline = !(simplifyPlanarStrips && roadSurfacePlanar);

                    if ((leftGuideRowCount > 0 || rightGuideRowCount > 0) && !includeShoulderRails)
                        continue;

                    if (!TryTriangulatePathPatchMesh(
                            seamLoopXy,
                            samplePath,
                            leftRoadXy,
                            rightRoadXy,
                            leftShoulderXy,
                            rightShoulderXy,
                            vertexCount,
                            tolerance,
                            leftGuideRowCount,
                            rightGuideRowCount,
                            includeShoulderRails,
                            includeRoadCenterline,
                            out IMesh? mesh,
                            out lastTriangulationError))
                        continue;

                    PatchMeshResult? triangulatedPatch = TryBuildValidatedTriangulatedPathPatch(
                        pathIndex,
                        patchDiagnostics,
                        mesh,
                        seamLoopXy,
                        samplePath,
                        allPaths,
                        barrierConstraints,
                        originalFaceGrid,
                        hasBoundaryLoop,
                        boundaryLoop,
                        boundaryVertexCount,
                        outsideVertices,
                        outsideFaces,
                        outsideFaceCount,
                        usedPlanarStripSimplification: simplifyPlanarStrips,
                        roadSurfacePlanar,
                        leftShoulderPlanar,
                        rightShoulderPlanar,
                        tolerance,
                        out lastTriangulatedValidationError);
                    if (triangulatedPatch != null)
                        return triangulatedPatch;
                }
            }
        }

        string triangulationFailure = string.IsNullOrWhiteSpace(lastTriangulationError)
            ? "Grade Path patch triangulation failed."
            : $"Grade Path patch triangulation failed: {lastTriangulationError}";
        if (!string.IsNullOrWhiteSpace(lastTriangulatedValidationError))
            triangulationFailure = $"{triangulationFailure} Last triangulated patch validation failure: {lastTriangulatedValidationError}";
        errorMessage = patchDiagnostics.Count == 0
            ? triangulationFailure
            : $"{string.Join(" ", patchDiagnostics)} {triangulationFailure}";
        return null;
    }

    private static PatchMeshResult? TryBuildValidatedTriangulatedPathPatch(
        int pathIndex,
        List<string> patchDiagnostics,
        IMesh mesh,
        double[] seamLoopXy,
        ConstraintPath samplePath,
        PathDefinition[] allPaths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        TerrainFaceGrid originalFaceGrid,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        bool usedPlanarStripSimplification,
        bool roadSurfacePlanar,
        bool leftShoulderPlanar,
        bool rightShoulderPlanar,
        double tolerance,
        out string? validationError)
    {
        validationError = null;
        if (mesh.Triangles.Count == 0)
        {
            validationError = "Grade Path patch triangulation produced no triangles.";
            return null;
        }

        int seamVertexCount = seamLoopXy.Length / 2;
        var extracted = TriangleNetExtractor.Extract(mesh);
        var patchVertices = new double[extracted.VertexCount * 3];
        var outXy = new double[extracted.VertexCount * 2];
        var origZ = new double[extracted.VertexCount];
        var newZ = new double[extracted.VertexCount];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            outXy[i * 2] = x;
            outXy[i * 2 + 1] = y;
            double z = originalFaceGrid.InterpolateZ(x, y);
            origZ[i] = z;
            newZ[i] = z;
        }

        ApplyPathGrading(
            allPaths,
            barrierConstraints,
            outXy,
            origZ,
            newZ,
            extracted.VertexCount,
            originalFaceGrid.InterpolateZ,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance);

        for (int i = 0; i < extracted.VertexCount; i++)
        {
            patchVertices[i * 3] = outXy[i * 2];
            patchVertices[i * 3 + 1] = outXy[i * 2 + 1];
            patchVertices[i * 3 + 2] = newZ[i];
        }

        // The daylight seam is the stitch boundary, so pin it to the original terrain surface.
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = patchVertices[i * 3];
            double y = patchVertices[i * 3 + 1];
            for (int seamIndex = 0; seamIndex < seamVertexCount; seamIndex++)
            {
                double dx = x - seamLoopXy[seamIndex * 2];
                double dy = y - seamLoopXy[seamIndex * 2 + 1];
                if ((dx * dx) + (dy * dy) > tolerance * tolerance)
                    continue;

                patchVertices[i * 3 + 2] = originalFaceGrid.InterpolateZ(x, y);
                break;
            }
        }

        if (!TryValidatePathPatchForStitching(
                seamLoopXy,
                patchVertices,
                extracted.Faces,
                extracted.FaceCount,
                outsideVertices,
                outsideFaces,
                outsideFaceCount,
                tolerance,
                out double[] triangulatedBoundaryLoopXy,
                out SeamGraph? triangulatedSeamGraph,
                out string? triangulatedValidationError))
        {
            validationError = triangulatedValidationError ?? "triangulated patch failed seam validation.";
            if (triangulatedSeamGraph != null)
            {
                validationError =
                    $"{validationError} (outside={triangulatedSeamGraph.TerrainMatchedSegments}/{triangulatedSeamGraph.SeamVertexCount}, patch-near={triangulatedSeamGraph.PatchBoundarySegmentsNearSeam}, outside-near={triangulatedSeamGraph.TerrainBoundarySegmentsNearSeam}).";
            }

            patchDiagnostics.Add($"Grade Path[{pathIndex}] triangulated patch rejected before merge: {validationError}");
            return null;
        }

        return new PatchMeshResult
        {
            Vertices = patchVertices,
            VertexCount = extracted.VertexCount,
            Faces = extracted.Faces,
            FaceCount = extracted.FaceCount,
            StitchLoopXy = triangulatedBoundaryLoopXy,
            Diagnostics = BuildTriangulatedPatchDiagnostics(
                patchDiagnostics,
                pathIndex,
                usedPlanarStripSimplification,
                roadSurfacePlanar,
                leftShoulderPlanar,
                rightShoulderPlanar)
        };
    }

    private static string[] BuildTriangulatedPatchDiagnostics(
        List<string> existingDiagnostics,
        int pathIndex,
        bool usedPlanarStripSimplification,
        bool roadSurfacePlanar,
        bool leftShoulderPlanar,
        bool rightShoulderPlanar)
    {
        var diagnostics = new List<string>(existingDiagnostics.Count + 2);
        diagnostics.AddRange(existingDiagnostics);
        if (usedPlanarStripSimplification)
        {
            diagnostics.Add(
                $"Grade Path[{pathIndex}] triangulated patch used planar-strip simplification (road={roadSurfacePlanar}, left shoulder={leftShoulderPlanar}, right shoulder={rightShoulderPlanar}).");
        }

        diagnostics.Add($"Grade Path[{pathIndex}] patch builder: triangulated patch selected.");
        return diagnostics.ToArray();
    }

    internal static bool TryValidatePathPatchForStitching(
        double[] seamLoopXy,
        double[] patchVertices,
        int[] patchFaces,
        int patchFaceCount,
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        double tolerance,
        out double[] patchBoundaryLoopXy,
        out SeamGraph? seamGraph,
        out string? errorMessage)
    {
        seamGraph = null;
        errorMessage = null;
        if (!TryBuildBoundaryLoopFromMesh(patchVertices, patchFaces, patchFaceCount, tolerance, out patchBoundaryLoopXy, out _))
        {
            seamGraph = SeamGraph.Build(
                seamLoopXy,
                patchVertices,
                patchFaces,
                patchFaceCount,
                outsideVertices,
                outsideFaces,
                outsideFaceCount,
                tolerance);
            if (seamGraph.HasExcessiveNearBoundaryFragmentation)
            {
                patchBoundaryLoopXy = Array.Empty<double>();
                errorMessage =
                    $"stitched patch did not produce a single closed stitch boundary and seam-adjacent boundary fragmentation was too high (patch={seamGraph.PatchBoundarySegmentsNearSeam}, outside={seamGraph.TerrainBoundarySegmentsNearSeam}, matched={seamGraph.PatchMatchedSegments}/{seamGraph.SeamVertexCount}).";
                return false;
            }

            patchBoundaryLoopXy = (double[])seamLoopXy.Clone();
            return true;
        }

        ComputeLoopDeviation(seamLoopXy, patchBoundaryLoopXy, out double seamToPatchMax, out int seamMissCount, tolerance * 2.0);
        ComputeLoopDeviation(patchBoundaryLoopXy, seamLoopXy, out double patchToSeamMax, out int patchMissCount, tolerance * 2.0);
        seamGraph = SeamGraph.Build(
            seamLoopXy,
            patchVertices,
            patchFaces,
            patchFaceCount,
            outsideVertices,
            outsideFaces,
            outsideFaceCount,
            tolerance);
        if (seamMissCount > 0 || patchMissCount > 0)
        {
            errorMessage =
                $"stitched seam geometry check failed (split misses={seamMissCount}, patch misses={patchMissCount}, split max={seamToPatchMax:F6}, patch max={patchToSeamMax:F6}).";
            return false;
        }

        return true;
    }

    private static PatchMeshResult? TryBuildStructuredPathPatchMesh(
        PathDefinition[] allPaths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        TerrainFaceGrid originalFaceGrid,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        ConstraintPath samplePath,
        double[] leftRoadXy,
        double[] rightRoadXy,
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        double[] leftShoulderZ,
        double[] rightShoulderZ,
        int vertexCount,
        double[] seamLoopXy,
        double tolerance)
    {
        if (IsClosedPath(samplePath.XyVertices, samplePath.VertexCount, tolerance))
            return null;

        int seamVertexCount = seamLoopXy.Length / 2;
        if (seamVertexCount < 3)
            return null;

        int structuredStationCount = vertexCount + 2;
        double startCapDepth = ComputeStructuredPathEndCapDepth(
            leftShoulderXy,
            rightShoulderXy,
            samplePath.XyVertices,
            vertexCount,
            sectionIndex: 0,
            tolerance);
        double endCapDepth = ComputeStructuredPathEndCapDepth(
            leftShoulderXy,
            rightShoulderXy,
            samplePath.XyVertices,
            vertexCount,
            sectionIndex: vertexCount - 1,
            tolerance);

        double[] structuredLeftShoulderXy = BuildStructuredPathRowWithCaps(
            leftShoulderXy,
            leftShoulderZ,
            samplePath,
            vertexCount,
            startCapDepth,
            endCapDepth,
            tolerance,
            out double[] structuredLeftShoulderZ);
        double[] structuredLeftRoadXy = BuildStructuredPathRowWithCaps(
            leftRoadXy,
            samplePath.ZValues,
            samplePath,
            vertexCount,
            startCapDepth,
            endCapDepth,
            tolerance,
            out double[] structuredLeftRoadZ);
        double[] structuredCenterXy = BuildStructuredPathRowWithCaps(
            samplePath.XyVertices,
            samplePath.ZValues,
            samplePath,
            vertexCount,
            startCapDepth,
            endCapDepth,
            tolerance,
            out double[] structuredCenterZ);
        double[] structuredRightRoadXy = BuildStructuredPathRowWithCaps(
            rightRoadXy,
            samplePath.ZValues,
            samplePath,
            vertexCount,
            startCapDepth,
            endCapDepth,
            tolerance,
            out double[] structuredRightRoadZ);
        double[] structuredRightShoulderXy = BuildStructuredPathRowWithCaps(
            rightShoulderXy,
            rightShoulderZ,
            samplePath,
            vertexCount,
            startCapDepth,
            endCapDepth,
            tolerance,
            out double[] structuredRightShoulderZ);

        PatchMeshResult structuredPatch = BuildStructuredOpenPathPatchMesh(
            structuredLeftShoulderXy,
            structuredLeftShoulderZ,
            structuredLeftRoadXy,
            structuredLeftRoadZ,
            structuredCenterXy,
            structuredCenterZ,
            structuredRightRoadXy,
            structuredRightRoadZ,
            structuredRightShoulderXy,
            structuredRightShoulderZ,
            structuredStationCount,
            tolerance);
        double[] mergedVertices = structuredPatch.Vertices;
        int mergedVertexCount = structuredPatch.VertexCount;
        int[] mergedFaces = structuredPatch.Faces;
        int mergedFaceCount = structuredPatch.FaceCount;

        double[] topologyInnerLoopXy = BuildClosedPathLoop(structuredLeftShoulderXy, structuredRightShoulderXy, structuredStationCount);
        double[] topologyInnerLoopZ = BuildClosedPathLoopZ(
            structuredLeftShoulderZ,
            structuredRightShoulderZ,
            structuredStationCount);
        if (TryBuildBoundaryLoopFromMesh(mergedVertices, mergedFaces, mergedFaceCount, tolerance, out double[] mergedBoundaryLoopXy, out double[] mergedBoundaryLoopZ))
        {
            topologyInnerLoopXy = mergedBoundaryLoopXy;
            topologyInnerLoopZ = mergedBoundaryLoopZ;
        }

        if (!LoopsCoincide(topologyInnerLoopXy, seamLoopXy, tolerance))
        {
            PatchMeshResult? topologyBand = TryBuildPathTopologyBandMesh(
                originalFaceGrid,
                topologyInnerLoopXy,
                topologyInnerLoopZ,
                seamLoopXy,
                tolerance);
            if (topologyBand == null)
                return null;

            MergeMeshes(
                mergedVertices,
                mergedVertexCount,
                mergedFaces,
                mergedFaceCount,
                topologyBand.Vertices,
                topologyBand.VertexCount,
                topologyBand.Faces,
                topologyBand.FaceCount,
                tolerance,
                out mergedVertices,
                out mergedVertexCount,
                out mergedFaces,
                out mergedFaceCount);
        }

        for (int i = 0; i < mergedVertexCount; i++)
        {
            double x = mergedVertices[i * 3];
            double y = mergedVertices[i * 3 + 1];
            for (int seamIndex = 0; seamIndex < seamVertexCount; seamIndex++)
            {
                double dx = x - seamLoopXy[seamIndex * 2];
                double dy = y - seamLoopXy[seamIndex * 2 + 1];
                if ((dx * dx) + (dy * dy) > tolerance * tolerance)
                    continue;

                mergedVertices[i * 3 + 2] = originalFaceGrid.InterpolateZ(x, y);
                break;
            }
        }

        return new PatchMeshResult
        {
            Vertices = mergedVertices,
            VertexCount = mergedVertexCount,
            Faces = mergedFaces,
            FaceCount = mergedFaceCount,
            StitchLoopXy = (double[])seamLoopXy.Clone()
        };
    }

    private static double[] BuildStructuredPathRowWithCaps(
        double[] rowXy,
        double[] rowZ,
        ConstraintPath samplePath,
        int vertexCount,
        double startCapDepth,
        double endCapDepth,
        double tolerance,
        out double[] expandedRowZ)
    {
        double[] expandedRowXy = BuildStructuredPathRowXyWithCaps(rowXy, samplePath, vertexCount, startCapDepth, endCapDepth);
        expandedRowZ = new double[vertexCount + 2];
        Array.Copy(rowZ, 0, expandedRowZ, 1, rowZ.Length);
        expandedRowZ[0] = rowZ[0];
        expandedRowZ[^1] = rowZ[^1];

        return expandedRowXy;
    }

    private static double[] BuildStructuredPathRowXyWithCaps(
        double[] rowXy,
        ConstraintPath samplePath,
        int vertexCount,
        double startCapDepth,
        double endCapDepth)
    {
        var expandedRowXy = new double[(vertexCount + 2) * 2];
        Array.Copy(rowXy, 0, expandedRowXy, 2, rowXy.Length);

        GetConstraintPathTangent(samplePath, 0, out double startTangentX, out double startTangentY);
        expandedRowXy[0] = rowXy[0] - (startTangentX * startCapDepth);
        expandedRowXy[1] = rowXy[1] - (startTangentY * startCapDepth);

        int endTargetIndex = vertexCount + 1;
        int endSourceIndex = vertexCount - 1;
        GetConstraintPathTangent(samplePath, endSourceIndex, out double endTangentX, out double endTangentY);
        expandedRowXy[endTargetIndex * 2] = rowXy[endSourceIndex * 2] + (endTangentX * endCapDepth);
        expandedRowXy[endTargetIndex * 2 + 1] = rowXy[endSourceIndex * 2 + 1] + (endTangentY * endCapDepth);

        return expandedRowXy;
    }

    private static double ComputeStructuredPathEndCapDepth(
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        double[] centerXy,
        int vertexCount,
        int sectionIndex,
        double tolerance)
    {
        int xyIndex = sectionIndex * 2;
        double leftDx = leftShoulderXy[xyIndex] - centerXy[xyIndex];
        double leftDy = leftShoulderXy[xyIndex + 1] - centerXy[xyIndex + 1];
        double rightDx = rightShoulderXy[xyIndex] - centerXy[xyIndex];
        double rightDy = rightShoulderXy[xyIndex + 1] - centerXy[xyIndex + 1];
        double leftReach = Math.Sqrt((leftDx * leftDx) + (leftDy * leftDy));
        double rightReach = Math.Sqrt((rightDx * rightDx) + (rightDy * rightDy));
        double reach = Math.Max(leftReach, rightReach);
        return Math.Max(reach, tolerance * 8.0);
    }

    private static PatchMeshResult BuildStructuredOpenPathPatchMesh(
        double[] leftShoulderXy,
        double[] leftShoulderZ,
        double[] leftRoadXy,
        double[] leftRoadZ,
        double[] centerXy,
        double[] centerZ,
        double[] rightRoadXy,
        double[] rightRoadZ,
        double[] rightShoulderXy,
        double[] rightShoulderZ,
        int stationCount,
        double tolerance)
    {
        const int rowCount = 5;
        var vertices = new double[stationCount * rowCount * 3];
        var faces = new List<int>(Math.Max(stationCount - 1, 0) * (rowCount - 1) * 6);

        for (int stationIndex = 0; stationIndex < stationCount; stationIndex++)
        {
            SetStructuredPathVertex(vertices, rowCount, stationIndex, 0, leftShoulderXy[stationIndex * 2], leftShoulderXy[stationIndex * 2 + 1], leftShoulderZ[stationIndex]);
            SetStructuredPathVertex(vertices, rowCount, stationIndex, 1, leftRoadXy[stationIndex * 2], leftRoadXy[stationIndex * 2 + 1], leftRoadZ[stationIndex]);
            SetStructuredPathVertex(vertices, rowCount, stationIndex, 2, centerXy[stationIndex * 2], centerXy[stationIndex * 2 + 1], centerZ[stationIndex]);
            SetStructuredPathVertex(vertices, rowCount, stationIndex, 3, rightRoadXy[stationIndex * 2], rightRoadXy[stationIndex * 2 + 1], rightRoadZ[stationIndex]);
            SetStructuredPathVertex(vertices, rowCount, stationIndex, 4, rightShoulderXy[stationIndex * 2], rightShoulderXy[stationIndex * 2 + 1], rightShoulderZ[stationIndex]);
        }

        for (int stationIndex = 0; stationIndex < stationCount - 1; stationIndex++)
        {
            for (int rowIndex = 0; rowIndex < rowCount - 1; rowIndex++)
                AddStructuredPathQuad(vertices, faces, rowCount, stationIndex, rowIndex, tolerance);
        }

        return new PatchMeshResult
        {
            Vertices = vertices,
            VertexCount = vertices.Length / 3,
            Faces = faces.ToArray(),
            FaceCount = faces.Count / 3
        };
    }

    private static void SetStructuredPathVertex(double[] vertices, int rowCount, int stationIndex, int rowIndex, double x, double y, double z)
    {
        int vertexIndex = (stationIndex * rowCount) + rowIndex;
        vertices[vertexIndex * 3] = x;
        vertices[vertexIndex * 3 + 1] = y;
        vertices[vertexIndex * 3 + 2] = z;
    }

    private static void AddStructuredPathQuad(
        double[] vertices,
        List<int> faces,
        int rowCount,
        int stationIndex,
        int rowIndex,
        double tolerance)
    {
        int currentInner = (stationIndex * rowCount) + rowIndex;
        int currentOuter = currentInner + 1;
        int nextInner = ((stationIndex + 1) * rowCount) + rowIndex;
        int nextOuter = nextInner + 1;

        bool innerCollapsed = VerticesCoincident(vertices, currentInner, nextInner, tolerance);
        bool outerCollapsed = VerticesCoincident(vertices, currentOuter, nextOuter, tolerance);
        if (innerCollapsed && outerCollapsed)
            return;

        if (innerCollapsed)
        {
            AddTriangleIfNonDegenerate(vertices, faces, currentInner, nextOuter, currentOuter, tolerance);
            return;
        }

        if (outerCollapsed)
        {
            AddTriangleIfNonDegenerate(vertices, faces, currentInner, nextInner, currentOuter, tolerance);
            return;
        }

        double diagonalA = DistanceSquaredXY(vertices, currentInner, nextOuter);
        double diagonalB = DistanceSquaredXY(vertices, nextInner, currentOuter);
        if (diagonalA <= diagonalB)
        {
            AddTriangleIfNonDegenerate(vertices, faces, currentInner, nextInner, nextOuter, tolerance);
            AddTriangleIfNonDegenerate(vertices, faces, currentInner, nextOuter, currentOuter, tolerance);
            return;
        }

        AddTriangleIfNonDegenerate(vertices, faces, currentInner, nextInner, currentOuter, tolerance);
        AddTriangleIfNonDegenerate(vertices, faces, nextInner, nextOuter, currentOuter, tolerance);
    }

    private static double[] BuildClosedPathLoopZ(
        double[] leftShoulderZ,
        double[] rightShoulderZ,
        int vertexCount)
    {
        var loopZ = new double[vertexCount * 2];
        for (int i = 0; i < vertexCount; i++)
            loopZ[i] = leftShoulderZ[i];
        for (int i = 0; i < vertexCount; i++)
            loopZ[vertexCount + i] = rightShoulderZ[vertexCount - 1 - i];
        return loopZ;
    }

    private static PatchMeshResult? TryBuildPathTopologyBandMesh(
        TerrainFaceGrid originalFaceGrid,
        double[] shoulderLoopXy,
        double[] shoulderLoopZ,
        double[] seamLoopXy,
        double tolerance)
    {
        int shoulderVertexCount = shoulderLoopXy.Length / 2;
        int seamVertexCount = seamLoopXy.Length / 2;
        if (shoulderVertexCount < 3 || seamVertexCount < 3)
            return null;

        double[] alignedSeamLoopXy = AlignClosedLoopToReference(seamLoopXy, shoulderLoopXy, tolerance);
        if (!TryBuildClosedLoopNormalizedStations(shoulderLoopXy, shoulderVertexCount, out double[] normalizedStations) ||
            !SampleClosedLoopAtNormalizedStationsWithTerrainZ(
                alignedSeamLoopXy,
                seamVertexCount,
                normalizedStations,
                originalFaceGrid,
                out double[] sampledSeamLoopXy,
                out double[] sampledSeamLoopZ) ||
            !TryFindInteriorPointForClosedLoop(shoulderLoopXy, shoulderVertexCount, tolerance, out double holeX, out double holeY))
        {
            return null;
        }

        var polygon = new Polygon(seamVertexCount + shoulderVertexCount);
        var seamVertices = new Vertex[seamVertexCount];
        for (int i = 0; i < seamVertexCount; i++)
            seamVertices[i] = new Vertex(seamLoopXy[i * 2], seamLoopXy[i * 2 + 1]) { ID = i };
        polygon.Add(new Contour(seamVertices), false);

        var shoulderVertices = new Vertex[shoulderVertexCount];
        for (int i = 0; i < shoulderVertexCount; i++)
            shoulderVertices[i] = new Vertex(shoulderLoopXy[i * 2], shoulderLoopXy[i * 2 + 1]) { ID = seamVertexCount + i };
        polygon.Add(new Contour(shoulderVertices), new TriangleNet.Geometry.Point(holeX, holeY));

        IMesh mesh;
        try
        {
            mesh = new GenericMesher().Triangulate(
                polygon,
                new ConstraintOptions { ConformingDelaunay = false, Convex = false, SegmentSplitting = 0 },
                null);
        }
        catch
        {
            return null;
        }

        if (mesh.Triangles.Count == 0)
            return null;

        var extracted = TriangleNetExtractor.Extract(mesh);
        var allVertices = new double[extracted.VertexCount * 3];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            allVertices[i * 3] = x;
            allVertices[i * 3 + 1] = y;
            allVertices[i * 3 + 2] = EvaluateClosedStripZ(
                x,
                y,
                shoulderLoopXy,
                shoulderLoopZ,
                sampledSeamLoopXy,
                sampledSeamLoopZ,
                shoulderVertexCount);
        }

        double[] seamLoopZ = BuildSeamLoopZ(seamLoopXy, originalFaceGrid);
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = allVertices[i * 3];
            double y = allVertices[i * 3 + 1];
            for (int seamIndex = 0; seamIndex < seamVertexCount; seamIndex++)
            {
                double dx = x - seamLoopXy[seamIndex * 2];
                double dy = y - seamLoopXy[seamIndex * 2 + 1];
                if ((dx * dx) + (dy * dy) > tolerance * tolerance)
                    continue;

                allVertices[i * 3 + 2] = seamLoopZ[seamIndex];
                break;
            }
        }

        if (!TryFilterPathTopologyBandFaces(
                allVertices,
                extracted.VertexCount,
                extracted.Faces,
                extracted.FaceCount,
                shoulderLoopXy,
                shoulderVertexCount,
                seamLoopXy,
                seamVertexCount,
                out double[] vertices,
                out int vertexCount,
                out int[] faces,
                out int faceCount))
        {
            vertices = allVertices;
            vertexCount = extracted.VertexCount;
            faces = extracted.Faces;
            faceCount = extracted.FaceCount;
        }

        return new PatchMeshResult
        {
            Vertices = vertices,
            VertexCount = vertexCount,
            Faces = faces,
            FaceCount = faceCount
        };
    }

    private static double[] BuildClosedPathLoop(double[] leftXy, double[] rightXy, int vertexCount)
    {
        var loop = new double[vertexCount * 4];
        for (int i = 0; i < vertexCount; i++)
        {
            loop[i * 2] = leftXy[i * 2];
            loop[i * 2 + 1] = leftXy[i * 2 + 1];
        }

        for (int i = 0; i < vertexCount; i++)
        {
            int source = vertexCount - 1 - i;
            int target = vertexCount + i;
            loop[target * 2] = rightXy[source * 2];
            loop[target * 2 + 1] = rightXy[source * 2 + 1];
        }

        return loop;
    }

    private static PatchMeshResult? TryBuildPathTopMesh(
        double[] boundaryLoopXy,
        int vertexCount,
        double[] centerlineXy,
        int centerlineVertexCount,
        double tolerance)
    {
        var polygon = new Polygon(vertexCount);
        var boundaryVertices = new Vertex[vertexCount];
        var allVertices = new List<Vertex>(vertexCount + centerlineVertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            boundaryVertices[i] = new Vertex(boundaryLoopXy[i * 2], boundaryLoopXy[i * 2 + 1]) { ID = i };
            allVertices.Add(boundaryVertices[i]);
        }

        polygon.Add(new Contour(boundaryVertices), false);

        Vertex ResolveVertex(double x, double y)
        {
            for (int i = 0; i < allVertices.Count; i++)
            {
                double dx = allVertices[i].X - x;
                double dy = allVertices[i].Y - y;
                if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                    return allVertices[i];
            }

            var vertex = new Vertex(x, y) { ID = allVertices.Count };
            allVertices.Add(vertex);
            polygon.Add(vertex);
            return vertex;
        }

        if (centerlineVertexCount >= 2)
        {
            Vertex previous = ResolveVertex(centerlineXy[0], centerlineXy[1]);
            for (int i = 1; i < centerlineVertexCount; i++)
            {
                Vertex current = ResolveVertex(centerlineXy[i * 2], centerlineXy[i * 2 + 1]);
                if (!ReferenceEquals(previous, current))
                    polygon.Add(new Segment(previous, current, 1), false);
                previous = current;
            }
        }

        IMesh mesh;
        try
        {
            mesh = new GenericMesher().Triangulate(
                polygon,
                new ConstraintOptions { ConformingDelaunay = false, Convex = false, SegmentSplitting = 0 },
                null);
        }
        catch
        {
            return null;
        }

        if (mesh.Triangles.Count == 0)
            return null;

        var extracted = TriangleNetExtractor.Extract(mesh);
        var vertices = new double[extracted.VertexCount * 3];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            vertices[i * 3] = extracted.Xy[i * 2];
            vertices[i * 3 + 1] = extracted.Xy[i * 2 + 1];
            vertices[i * 3 + 2] = 0.0;
        }

        return new PatchMeshResult
        {
            Vertices = vertices,
            VertexCount = extracted.VertexCount,
            Faces = extracted.Faces,
            FaceCount = extracted.FaceCount
        };
    }

    private static PatchMeshResult BuildClosedRuledStripMesh(
        double[] innerLoopXy,
        double[] outerLoopXy,
        int ringCount,
        double tolerance)
    {
        var vertices = new double[ringCount * 2 * 3];
        var faces = new List<int>(ringCount * 6);
        for (int i = 0; i < ringCount; i++)
        {
            vertices[(i * 2) * 3] = innerLoopXy[i * 2];
            vertices[(i * 2) * 3 + 1] = innerLoopXy[i * 2 + 1];
            vertices[(i * 2) * 3 + 2] = 0.0;

            vertices[((i * 2) + 1) * 3] = outerLoopXy[i * 2];
            vertices[((i * 2) + 1) * 3 + 1] = outerLoopXy[i * 2 + 1];
            vertices[((i * 2) + 1) * 3 + 2] = 0.0;
        }

        for (int i = 0; i < ringCount; i++)
        {
            int next = (i + 1) % ringCount;
            int i0 = i * 2;
            int o0 = i0 + 1;
            int i1 = next * 2;
            int o1 = i1 + 1;

            bool innerCollapsed = VerticesCoincident(vertices, i0, i1, tolerance);
            bool outerCollapsed = VerticesCoincident(vertices, o0, o1, tolerance);
            if (innerCollapsed && outerCollapsed)
                continue;

            if (innerCollapsed)
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, o1, o0, tolerance);
                continue;
            }

            if (outerCollapsed)
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o0, tolerance);
                continue;
            }

            double diagonalA = DistanceSquaredXY(vertices, i0, o1);
            double diagonalB = DistanceSquaredXY(vertices, i1, o0);
            if (diagonalA <= diagonalB)
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o1, tolerance);
                AddTriangleIfNonDegenerate(vertices, faces, i0, o1, o0, tolerance);
            }
            else
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o0, tolerance);
                AddTriangleIfNonDegenerate(vertices, faces, i1, o1, o0, tolerance);
            }
        }

        return new PatchMeshResult
        {
            Vertices = vertices,
            VertexCount = vertices.Length / 3,
            Faces = faces.ToArray(),
            FaceCount = faces.Count / 3
        };
    }

    private static PatchMeshResult BuildOpenRuledStripMesh(
        double[] innerRowXy,
        double[] innerRowZ,
        double[] outerRowXy,
        double[] outerRowZ,
        int rowCount,
        double tolerance)
    {
        var vertices = new double[rowCount * 2 * 3];
        var faces = new List<int>(Math.Max(rowCount - 1, 0) * 6);
        for (int i = 0; i < rowCount; i++)
        {
            vertices[(i * 2) * 3] = innerRowXy[i * 2];
            vertices[(i * 2) * 3 + 1] = innerRowXy[i * 2 + 1];
            vertices[(i * 2) * 3 + 2] = innerRowZ[i];

            vertices[((i * 2) + 1) * 3] = outerRowXy[i * 2];
            vertices[((i * 2) + 1) * 3 + 1] = outerRowXy[i * 2 + 1];
            vertices[((i * 2) + 1) * 3 + 2] = outerRowZ[i];
        }

        for (int i = 0; i < rowCount - 1; i++)
        {
            int i0 = i * 2;
            int o0 = i0 + 1;
            int i1 = (i + 1) * 2;
            int o1 = i1 + 1;

            bool innerCollapsed = VerticesCoincident(vertices, i0, i1, tolerance);
            bool outerCollapsed = VerticesCoincident(vertices, o0, o1, tolerance);
            if (innerCollapsed && outerCollapsed)
                continue;

            if (innerCollapsed)
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, o1, o0, tolerance);
                continue;
            }

            if (outerCollapsed)
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o0, tolerance);
                continue;
            }

            double diagonalA = DistanceSquaredXY(vertices, i0, o1);
            double diagonalB = DistanceSquaredXY(vertices, i1, o0);
            if (diagonalA <= diagonalB)
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o1, tolerance);
                AddTriangleIfNonDegenerate(vertices, faces, i0, o1, o0, tolerance);
            }
            else
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o0, tolerance);
                AddTriangleIfNonDegenerate(vertices, faces, i1, o1, o0, tolerance);
            }
        }

        return new PatchMeshResult
        {
            Vertices = vertices,
            VertexCount = vertices.Length / 3,
            Faces = faces.ToArray(),
            FaceCount = faces.Count / 3
        };
    }

    private static PatchMeshResult BuildClosedRuledStripMeshWithZ(
        double[] innerLoopXy,
        double[] innerLoopZ,
        double[] outerLoopXy,
        double[] outerLoopZ,
        int ringCount,
        double tolerance)
    {
        var vertices = new double[ringCount * 2 * 3];
        var faces = new List<int>(ringCount * 6);
        for (int i = 0; i < ringCount; i++)
        {
            vertices[(i * 2) * 3] = innerLoopXy[i * 2];
            vertices[(i * 2) * 3 + 1] = innerLoopXy[i * 2 + 1];
            vertices[(i * 2) * 3 + 2] = innerLoopZ[i];

            vertices[((i * 2) + 1) * 3] = outerLoopXy[i * 2];
            vertices[((i * 2) + 1) * 3 + 1] = outerLoopXy[i * 2 + 1];
            vertices[((i * 2) + 1) * 3 + 2] = outerLoopZ[i];
        }

        for (int i = 0; i < ringCount; i++)
        {
            int next = (i + 1) % ringCount;
            int i0 = i * 2;
            int o0 = i0 + 1;
            int i1 = next * 2;
            int o1 = i1 + 1;

            bool innerCollapsed = VerticesCoincident(vertices, i0, i1, tolerance);
            bool outerCollapsed = VerticesCoincident(vertices, o0, o1, tolerance);
            if (innerCollapsed && outerCollapsed)
                continue;

            if (innerCollapsed)
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, o1, o0, tolerance);
                continue;
            }

            if (outerCollapsed)
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o0, tolerance);
                continue;
            }

            double diagonalA = DistanceSquaredXY(vertices, i0, o1);
            double diagonalB = DistanceSquaredXY(vertices, i1, o0);
            if (diagonalA <= diagonalB)
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o1, tolerance);
                AddTriangleIfNonDegenerate(vertices, faces, i0, o1, o0, tolerance);
            }
            else
            {
                AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o0, tolerance);
                AddTriangleIfNonDegenerate(vertices, faces, i1, o1, o0, tolerance);
            }
        }

        return new PatchMeshResult
        {
            Vertices = vertices,
            VertexCount = vertices.Length / 3,
            Faces = faces.ToArray(),
            FaceCount = faces.Count / 3
        };
    }

    private static double[] BuildSeamLoopZ(double[] seamLoopXy, TerrainFaceGrid originalFaceGrid)
    {
        var seamLoopZ = new double[seamLoopXy.Length / 2];
        for (int i = 0; i < seamLoopZ.Length; i++)
            seamLoopZ[i] = originalFaceGrid.InterpolateZ(seamLoopXy[i * 2], seamLoopXy[i * 2 + 1]);
        return seamLoopZ;
    }

    private static bool TryTriangulatePathPatchMesh(
        double[] seamLoopXy,
        ConstraintPath samplePath,
        double[] leftRoadXy,
        double[] rightRoadXy,
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        int vertexCount,
        double tolerance,
        int leftShoulderGuideRowCount,
        int rightShoulderGuideRowCount,
        bool includeShoulderRails,
        bool includeRoadCenterline,
        out IMesh? mesh,
        out string? errorMessage)
    {
        mesh = null;
        errorMessage = null;
        int seamVertexCount = seamLoopXy.Length / 2;
        int guideVertexBudget = Math.Max(leftShoulderGuideRowCount, rightShoulderGuideRowCount);
        var polygon = new Polygon(seamVertexCount + vertexCount * (includeShoulderRails ? 7 : 5 + guideVertexBudget * 2));
        var allVertices = new List<Vertex>(seamVertexCount + vertexCount * (includeShoulderRails ? 7 : 5));
        var addedSegments = new HashSet<long>();

        var seamVertices = new Vertex[seamVertexCount];
        for (int i = 0; i < seamVertexCount; i++)
        {
            seamVertices[i] = new Vertex(seamLoopXy[i * 2], seamLoopXy[i * 2 + 1]) { ID = i };
            allVertices.Add(seamVertices[i]);
        }

        polygon.Add(new Contour(seamVertices), false);

        Vertex ResolveVertex(double x, double y)
        {
            for (int i = 0; i < allVertices.Count; i++)
            {
                double dx = allVertices[i].X - x;
                double dy = allVertices[i].Y - y;
                if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                    return allVertices[i];
            }

            var vertex = new Vertex(x, y) { ID = allVertices.Count };
            allVertices.Add(vertex);
            polygon.Add(vertex);
            return vertex;
        }

        static long SegmentKey(int a, int b)
        {
            if (a > b)
                (a, b) = (b, a);

            return ((long)a << 32) | (uint)b;
        }

        void AddSegmentIfDistinct(Vertex start, Vertex end)
        {
            if (ReferenceEquals(start, end))
                return;

            long key = SegmentKey(start.ID, end.ID);
            if (!addedSegments.Add(key))
                return;

            polygon.Add(new Segment(start, end, 1), false);
        }

        void AddOpenPolyline(double[] xy, int count)
        {
            if (count < 2)
                return;

            Vertex previous = ResolveVertex(xy[0], xy[1]);
            for (int i = 1; i < count; i++)
            {
                Vertex current = ResolveVertex(xy[i * 2], xy[i * 2 + 1]);
                AddSegmentIfDistinct(previous, current);
                previous = current;
            }
        }

        double[][] leftGuideRows = BuildInterpolatedPathGuideRows(leftRoadXy, leftShoulderXy, vertexCount, leftShoulderGuideRowCount);
        double[][] rightGuideRows = BuildInterpolatedPathGuideRows(rightRoadXy, rightShoulderXy, vertexCount, rightShoulderGuideRowCount);

        if (includeRoadCenterline)
            AddOpenPolyline(samplePath.XyVertices, vertexCount);
        AddOpenPolyline(leftRoadXy, vertexCount);
        AddOpenPolyline(rightRoadXy, vertexCount);
        if (includeShoulderRails)
        {
            AddOpenPolyline(leftShoulderXy, vertexCount);
            AddOpenPolyline(rightShoulderXy, vertexCount);
        }

        for (int rowIndex = 0; rowIndex < leftShoulderGuideRowCount; rowIndex++)
        {
            AddOpenPolyline(leftGuideRows[rowIndex], vertexCount);
        }

        for (int rowIndex = 0; rowIndex < rightShoulderGuideRowCount; rowIndex++)
        {
            AddOpenPolyline(rightGuideRows[rowIndex], vertexCount);
        }

        for (int i = 0; i < vertexCount; i++)
        {
            Vertex leftShoulder = ResolveVertex(leftShoulderXy[i * 2], leftShoulderXy[i * 2 + 1]);
            Vertex leftRoad = ResolveVertex(leftRoadXy[i * 2], leftRoadXy[i * 2 + 1]);
            Vertex center = ResolveVertex(samplePath.XyVertices[i * 2], samplePath.XyVertices[i * 2 + 1]);
            Vertex rightRoad = ResolveVertex(rightRoadXy[i * 2], rightRoadXy[i * 2 + 1]);
            Vertex rightShoulder = ResolveVertex(rightShoulderXy[i * 2], rightShoulderXy[i * 2 + 1]);

            Vertex previous = leftShoulder;
            for (int rowIndex = 0; rowIndex < leftShoulderGuideRowCount; rowIndex++)
            {
                Vertex guide = ResolveVertex(leftGuideRows[rowIndex][i * 2], leftGuideRows[rowIndex][i * 2 + 1]);
                AddSegmentIfDistinct(previous, guide);
                previous = guide;
            }

            AddSegmentIfDistinct(previous, leftRoad);
            AddSegmentIfDistinct(leftRoad, center);
            AddSegmentIfDistinct(center, rightRoad);

            previous = rightRoad;
            for (int rowIndex = rightShoulderGuideRowCount - 1; rowIndex >= 0; rowIndex--)
            {
                Vertex guide = ResolveVertex(rightGuideRows[rowIndex][i * 2], rightGuideRows[rowIndex][i * 2 + 1]);
                AddSegmentIfDistinct(previous, guide);
                previous = guide;
            }

            AddSegmentIfDistinct(previous, rightShoulder);
        }

        try
        {
            mesh = new GenericMesher().Triangulate(
                polygon,
                new ConstraintOptions { ConformingDelaunay = false, Convex = false, SegmentSplitting = 0 },
                null);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            mesh = null;
            return false;
        }
    }

    private static OutputPolyline BuildRoadEdgePolyline(ConstraintPath samplePath, double[] roadEdgeXy)
    {
        var xyz = new double[samplePath.VertexCount * 3];
        for (int i = 0; i < samplePath.VertexCount; i++)
        {
            xyz[i * 3] = roadEdgeXy[i * 2];
            xyz[i * 3 + 1] = roadEdgeXy[i * 2 + 1];
            xyz[i * 3 + 2] = samplePath.ZValues[i];
        }

        return new OutputPolyline(xyz, samplePath.VertexCount);
    }

    private static bool TryExtractAreaMesh(
        MeshAreaSplitter.SplitResult split,
        int areaIndex,
        out double[] vertices,
        out int vertexCount,
        out int[] faces,
        out int faceCount)
    {
        var selectedFaces = new List<int>();
        for (int faceIndex = 0; faceIndex < split.FaceCount; faceIndex++)
        {
            if (split.FaceAreaIndex[faceIndex] == areaIndex)
                selectedFaces.Add(faceIndex);
        }

        if (selectedFaces.Count == 0)
        {
            vertices = Array.Empty<double>();
            vertexCount = 0;
            faces = Array.Empty<int>();
            faceCount = 0;
            return false;
        }

        var usedVertices = new HashSet<int>();
        foreach (int faceIndex in selectedFaces)
        {
            usedVertices.Add(split.Faces[faceIndex * 3]);
            usedVertices.Add(split.Faces[faceIndex * 3 + 1]);
            usedVertices.Add(split.Faces[faceIndex * 3 + 2]);
        }

        var remap = new Dictionary<int, int>(usedVertices.Count);
        vertices = new double[usedVertices.Count * 3];
        int nextVertex = 0;
        foreach (int originalVertex in usedVertices.OrderBy(static value => value))
        {
            remap[originalVertex] = nextVertex;
            vertices[nextVertex * 3] = split.Vertices[originalVertex * 3];
            vertices[nextVertex * 3 + 1] = split.Vertices[originalVertex * 3 + 1];
            vertices[nextVertex * 3 + 2] = split.Vertices[originalVertex * 3 + 2];
            nextVertex++;
        }

        faces = new int[selectedFaces.Count * 3];
        for (int i = 0; i < selectedFaces.Count; i++)
        {
            int faceIndex = selectedFaces[i];
            faces[i * 3] = remap[split.Faces[faceIndex * 3]];
            faces[i * 3 + 1] = remap[split.Faces[faceIndex * 3 + 1]];
            faces[i * 3 + 2] = remap[split.Faces[faceIndex * 3 + 2]];
        }

        vertexCount = nextVertex;
        faceCount = selectedFaces.Count;
        return true;
    }

    private static bool IsClosedPath(double[] xyVertices, int vertexCount, double tolerance)
    {
        if (vertexCount < 3)
            return false;

        double dx = xyVertices[0] - xyVertices[(vertexCount - 1) * 2];
        double dy = xyVertices[1] - xyVertices[(vertexCount - 1) * 2 + 1];
        return (dx * dx) + (dy * dy) <= tolerance * tolerance;
    }

    private static bool LoopsCoincide(double[] leftLoopXy, double[] rightLoopXy, double tolerance)
    {
        ComputeLoopDeviation(leftLoopXy, rightLoopXy, out _, out int leftMisses, tolerance);
        ComputeLoopDeviation(rightLoopXy, leftLoopXy, out _, out int rightMisses, tolerance);
        return leftMisses == 0 && rightMisses == 0;
    }

    private static void ComputeLoopDeviation(
        double[] sourceLoopXy,
        double[] targetLoopXy,
        out double maxDistance,
        out int missCount,
        double tolerance)
    {
        maxDistance = 0.0;
        missCount = 0;
        int sourceCount = sourceLoopXy.Length / 2;
        int targetCount = targetLoopXy.Length / 2;
        if (targetCount < 2)
        {
            missCount = sourceCount;
            maxDistance = double.MaxValue;
            return;
        }

        for (int i = 0; i < sourceCount; i++)
        {
            double px = sourceLoopXy[i * 2];
            double py = sourceLoopXy[i * 2 + 1];
            double best = GradingGeometry2D.DistanceToPolygon(px, py, targetLoopXy, targetCount);

            if (best > tolerance)
                missCount++;
            if (best > maxDistance)
                maxDistance = best;
        }
    }

    private static double EvaluateClosedStripZ(
        double x,
        double y,
        double[] innerLoopXy,
        double[] innerLoopZ,
        double[] outerLoopXy,
        double[] outerLoopZ,
        int vertexCount)
    {
        if (vertexCount < 2 ||
            !TryFindClosestClosedLoopLocation(innerLoopXy, vertexCount, x, y, out ClosestClosedLoopLocation closest))
        {
            return innerLoopZ.Length > 0 ? innerLoopZ[0] : 0.0;
        }

        int segmentIndex = closest.SegmentIndex;
        int next = (segmentIndex + 1) % vertexCount;
        double t = closest.SegmentT;

        double innerX = LerpValue(innerLoopXy[segmentIndex * 2], innerLoopXy[next * 2], t);
        double innerY = LerpValue(innerLoopXy[(segmentIndex * 2) + 1], innerLoopXy[(next * 2) + 1], t);
        double innerZ = LerpValue(innerLoopZ[segmentIndex], innerLoopZ[next], t);
        double outerX = LerpValue(outerLoopXy[segmentIndex * 2], outerLoopXy[next * 2], t);
        double outerY = LerpValue(outerLoopXy[(segmentIndex * 2) + 1], outerLoopXy[(next * 2) + 1], t);
        double outerZ = LerpValue(outerLoopZ[segmentIndex], outerLoopZ[next], t);

        double dx = outerX - innerX;
        double dy = outerY - innerY;
        double lengthSq = (dx * dx) + (dy * dy);
        if (lengthSq <= 1e-12)
            return innerZ;

        double projectedT = (((x - innerX) * dx) + ((y - innerY) * dy)) / lengthSq;
        projectedT = Math.Clamp(projectedT, 0.0, 1.0);
        return LerpValue(innerZ, outerZ, projectedT);
    }

    private static bool TryFindInteriorPointForClosedLoop(
        double[] loopXy,
        int vertexCount,
        double tolerance,
        out double interiorX,
        out double interiorY)
    {
        interiorX = 0.0;
        interiorY = 0.0;
        if (vertexCount < 3)
            return false;

        double sumX = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            sumX += loopXy[i * 2];
            sumY += loopXy[i * 2 + 1];
        }

        double centroidX = sumX / vertexCount;
        double centroidY = sumY / vertexCount;
        if (GradingGeometry2D.PointInPolygon(centroidX, centroidY, loopXy, vertexCount) &&
            GradingGeometry2D.DistanceToPolygon(centroidX, centroidY, loopXy, vertexCount) > tolerance)
        {
            interiorX = centroidX;
            interiorY = centroidY;
            return true;
        }

        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double midX = (loopXy[i * 2] + loopXy[next * 2]) * 0.5;
            double midY = (loopXy[i * 2 + 1] + loopXy[next * 2 + 1]) * 0.5;
            double towardCentroidX = LerpValue(midX, centroidX, 0.25);
            double towardCentroidY = LerpValue(midY, centroidY, 0.25);
            if (GradingGeometry2D.PointInPolygon(towardCentroidX, towardCentroidY, loopXy, vertexCount) &&
                GradingGeometry2D.DistanceToPolygon(towardCentroidX, towardCentroidY, loopXy, vertexCount) > tolerance)
            {
                interiorX = towardCentroidX;
                interiorY = towardCentroidY;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindClosestClosedLoopLocation(
        double[] loopXy,
        int vertexCount,
        double px,
        double py,
        out ClosestClosedLoopLocation closest)
    {
        closest = default;
        if (vertexCount < 2)
            return false;

        double bestDistSq = double.MaxValue;
        bool found = false;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double ax = loopXy[i * 2];
            double ay = loopXy[i * 2 + 1];
            double bx = loopXy[next * 2];
            double by = loopXy[next * 2 + 1];
            double abx = bx - ax;
            double aby = by - ay;
            double lengthSq = (abx * abx) + (aby * aby);
            double t = lengthSq > 1e-12
                ? Math.Clamp((((px - ax) * abx) + ((py - ay) * aby)) / lengthSq, 0.0, 1.0)
                : 0.0;
            double qx = ax + (abx * t);
            double qy = ay + (aby * t);
            double dx = px - qx;
            double dy = py - qy;
            double distSq = (dx * dx) + (dy * dy);
            if (distSq >= bestDistSq)
                continue;

            bestDistSq = distSq;
            closest = new ClosestClosedLoopLocation(i, t, Math.Sqrt(distSq));
            found = true;
        }

        return found;
    }

    private static double[] AlignClosedLoopToReference(double[] loopXy, double[] referenceLoopXy, double tolerance)
    {
        if ((loopXy.Length / 2) < 3)
            return (double[])loopXy.Clone();

        double[] result = (double[])loopXy.Clone();
        if (ClipperGeometry.SignedArea(result) * ClipperGeometry.SignedArea(referenceLoopXy) < 0.0)
            result = ReverseClosedLoop(result);

        int vertexCount = result.Length / 2;
        double refX = referenceLoopXy[0];
        double refY = referenceLoopXy[1];
        int bestIndex = 0;
        double bestDistSq = double.MaxValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double dx = result[i * 2] - refX;
            double dy = result[i * 2 + 1] - refY;
            double distSq = (dx * dx) + (dy * dy);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIndex = i;
            }
        }

        if (bestIndex == 0 || bestDistSq <= tolerance * tolerance)
            return RotateClosedLoop(result, bestIndex);

        return RotateClosedLoop(result, bestIndex);
    }

    private static bool TryRegularizePathClosedLoop(double[] rawLoopXy, double tolerance, out double[] regularizedLoopXy)
    {
        regularizedLoopXy = Array.Empty<double>();
        if (rawLoopXy.Length < 6)
            return false;

        if (ClipperGeometry.TryUnionClosedLoops([rawLoopXy], tolerance, out List<double[]> unionLoops) &&
            ClipperGeometry.TryPickLargestLoop(unionLoops, out double[] unionLoop))
        {
            regularizedLoopXy = AlignClosedLoopToReference(unionLoop, rawLoopXy, tolerance);
        }
        else if (ClipperGeometry.TrySimplifyClosedLoop(rawLoopXy, tolerance, out double[] simplifiedLoop))
        {
            regularizedLoopXy = AlignClosedLoopToReference(simplifiedLoop, rawLoopXy, tolerance);
        }
        else
        {
            regularizedLoopXy = (double[])rawLoopXy.Clone();
        }

        return regularizedLoopXy.Length >= 6 &&
               Math.Abs(ClipperGeometry.SignedArea(regularizedLoopXy)) > tolerance * tolerance;
    }

    private static double[] DensifyClosedLoopWithMeshEdgeIntersections(
        double[] loopXy,
        double[] meshVertices,
        int[] meshFaces,
        int meshFaceCount,
        double tolerance)
    {
        int loopVertexCount = loopXy.Length / 2;
        if (loopVertexCount < 3 || meshFaceCount <= 0)
            return (double[])loopXy.Clone();

        var edgeKeys = new HashSet<long>(meshFaceCount * 3, IndexedMeshTools.EdgeKeyComparer.Instance);
        var meshEdges = new List<(double Ax, double Ay, double Bx, double By)>(meshFaceCount * 2);
        for (int faceIndex = 0; faceIndex < meshFaceCount; faceIndex++)
        {
            AddMeshEdge(meshFaces[faceIndex * 3], meshFaces[faceIndex * 3 + 1]);
            AddMeshEdge(meshFaces[faceIndex * 3 + 1], meshFaces[faceIndex * 3 + 2]);
            AddMeshEdge(meshFaces[faceIndex * 3 + 2], meshFaces[faceIndex * 3]);
        }

        var densified = new List<double>(loopXy.Length + (meshEdges.Count * 2));
        for (int segmentIndex = 0; segmentIndex < loopVertexCount; segmentIndex++)
        {
            int nextSegmentIndex = (segmentIndex + 1) % loopVertexCount;
            double ax = loopXy[segmentIndex * 2];
            double ay = loopXy[segmentIndex * 2 + 1];
            double bx = loopXy[nextSegmentIndex * 2];
            double by = loopXy[nextSegmentIndex * 2 + 1];

            var segmentPoints = new List<(double T, double X, double Y)>(4)
            {
                (0.0, ax, ay)
            };

            for (int edgeIndex = 0; edgeIndex < meshEdges.Count; edgeIndex++)
            {
                var edge = meshEdges[edgeIndex];
                if (!TryIntersectSegmentsWithParameters(
                        ax,
                        ay,
                        bx,
                        by,
                        edge.Ax,
                        edge.Ay,
                        edge.Bx,
                        edge.By,
                        tolerance,
                        out double seamT,
                        out double edgeT,
                        out double ix,
                        out double iy))
                {
                    continue;
                }

                if (seamT <= 1e-9 || seamT >= 1.0 - 1e-9)
                    continue;
                if (edgeT <= 1e-9 || edgeT >= 1.0 - 1e-9)
                    continue;

                bool exists = false;
                for (int i = 0; i < segmentPoints.Count; i++)
                {
                    if (Math.Abs(segmentPoints[i].T - seamT) <= 1e-9 ||
                        DistanceSquared(segmentPoints[i].X, segmentPoints[i].Y, ix, iy) <= tolerance * tolerance)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                    segmentPoints.Add((seamT, ix, iy));
            }

            segmentPoints.Sort(static (left, right) => left.T.CompareTo(right.T));
            for (int i = 0; i < segmentPoints.Count; i++)
                AddLoopPoint(densified, segmentPoints[i].X, segmentPoints[i].Y, tolerance);
        }

        return densified.Count >= 6 ? densified.ToArray() : (double[])loopXy.Clone();

        void AddMeshEdge(int a, int b)
        {
            long key = IndexedMeshTools.GetEdgeKey(a, b);
            if (!edgeKeys.Add(key))
                return;

            meshEdges.Add((
                meshVertices[a * 3],
                meshVertices[a * 3 + 1],
                meshVertices[b * 3],
                meshVertices[b * 3 + 1]));
        }
    }

    private static bool TryIntersectSegmentsWithParameters(
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy,
        double dx,
        double dy,
        double tolerance,
        out double abT,
        out double cdT,
        out double ix,
        out double iy)
    {
        abT = 0.0;
        cdT = 0.0;
        ix = 0.0;
        iy = 0.0;

        double abx = bx - ax;
        double aby = by - ay;
        double cdx = dx - cx;
        double cdy = dy - cy;
        double denominator = (abx * cdy) - (aby * cdx);
        if (Math.Abs(denominator) <= tolerance * tolerance)
            return false;

        double acx = cx - ax;
        double acy = cy - ay;
        abT = ((acx * cdy) - (acy * cdx)) / denominator;
        cdT = ((acx * aby) - (acy * abx)) / denominator;
        if (abT < -1e-9 || abT > 1.0 + 1e-9 || cdT < -1e-9 || cdT > 1.0 + 1e-9)
            return false;

        ix = ax + (abx * abT);
        iy = ay + (aby * abT);
        return true;
    }

    private static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        return (dx * dx) + (dy * dy);
    }

    private static double[] ReverseClosedLoop(double[] loopXy)
    {
        int vertexCount = loopXy.Length / 2;
        var reversed = new double[loopXy.Length];
        for (int i = 0; i < vertexCount; i++)
        {
            int source = vertexCount - 1 - i;
            reversed[i * 2] = loopXy[source * 2];
            reversed[i * 2 + 1] = loopXy[source * 2 + 1];
        }

        return reversed;
    }

    private static double[] RotateClosedLoop(double[] loopXy, int startIndex)
    {
        int vertexCount = loopXy.Length / 2;
        if (vertexCount == 0 || startIndex % vertexCount == 0)
            return (double[])loopXy.Clone();

        var rotated = new double[loopXy.Length];
        for (int i = 0; i < vertexCount; i++)
        {
            int source = (startIndex + i) % vertexCount;
            rotated[i * 2] = loopXy[source * 2];
            rotated[i * 2 + 1] = loopXy[source * 2 + 1];
        }

        return rotated;
    }

    private static bool TryBuildClosedLoopNormalizedStations(double[] loopXy, int vertexCount, out double[] stations)
    {
        stations = Array.Empty<double>();
        if (vertexCount < 3 || loopXy.Length < vertexCount * 2)
            return false;

        if (!TryBuildClosedLoopCumulativeDistances(loopXy, vertexCount, out double[] cumulative, out double perimeter) ||
            perimeter <= 1e-9)
        {
            return false;
        }

        stations = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            stations[i] = cumulative[i] / perimeter;

        return true;
    }

    private static bool TryBuildClosedLoopCumulativeDistances(
        double[] loopXy,
        int vertexCount,
        out double[] cumulative,
        out double perimeter)
    {
        cumulative = Array.Empty<double>();
        perimeter = 0.0;
        if (vertexCount < 3 || loopXy.Length < vertexCount * 2)
            return false;

        cumulative = new double[vertexCount + 1];
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double dx = loopXy[next * 2] - loopXy[i * 2];
            double dy = loopXy[next * 2 + 1] - loopXy[i * 2 + 1];
            perimeter += Math.Sqrt((dx * dx) + (dy * dy));
            cumulative[i + 1] = perimeter;
        }

        return perimeter > 1e-9;
    }

    private static bool SampleClosedLoopAtNormalizedStationsWithTerrainZ(
        double[] loopXy,
        int vertexCount,
        double[] normalizedStations,
        TerrainFaceGrid terrainFaceGrid,
        out double[] sampledXy,
        out double[] sampledZ)
    {
        sampledXy = Array.Empty<double>();
        sampledZ = Array.Empty<double>();
        if (!TryBuildClosedLoopCumulativeDistances(loopXy, vertexCount, out double[] cumulative, out double perimeter) ||
            perimeter <= 1e-9)
        {
            return false;
        }

        sampledXy = new double[normalizedStations.Length * 2];
        sampledZ = new double[normalizedStations.Length];
        int segmentIndex = 0;
        for (int i = 0; i < normalizedStations.Length; i++)
        {
            double distance = Math.Clamp(normalizedStations[i], 0.0, 1.0) * perimeter;
            while (segmentIndex < vertexCount - 1 && cumulative[segmentIndex + 1] < distance)
                segmentIndex++;

            int next = (segmentIndex + 1) % vertexCount;
            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = segmentEnd - segmentStart;
            double t = segmentLength > 1e-9 ? (distance - segmentStart) / segmentLength : 0.0;
            double x = LerpValue(loopXy[segmentIndex * 2], loopXy[next * 2], t);
            double y = LerpValue(loopXy[segmentIndex * 2 + 1], loopXy[next * 2 + 1], t);
            sampledXy[i * 2] = x;
            sampledXy[i * 2 + 1] = y;
            sampledZ[i] = terrainFaceGrid.InterpolateZ(x, y);
        }

        return true;
    }

    private static bool SampleClosedLoopAtNormalizedStationsWithZ(
        double[] loopXy,
        double[] loopZ,
        int vertexCount,
        double[] normalizedStations,
        out double[] sampledXy,
        out double[] sampledZ)
    {
        sampledXy = Array.Empty<double>();
        sampledZ = Array.Empty<double>();
        if (loopZ.Length < vertexCount)
            return false;
        if (!TryBuildClosedLoopCumulativeDistances(loopXy, vertexCount, out double[] cumulative, out double perimeter) ||
            perimeter <= 1e-9)
        {
            return false;
        }

        sampledXy = new double[normalizedStations.Length * 2];
        sampledZ = new double[normalizedStations.Length];
        int segmentIndex = 0;
        for (int i = 0; i < normalizedStations.Length; i++)
        {
            double distance = Math.Clamp(normalizedStations[i], 0.0, 1.0) * perimeter;
            while (segmentIndex < vertexCount - 1 && cumulative[segmentIndex + 1] < distance)
                segmentIndex++;

            int next = (segmentIndex + 1) % vertexCount;
            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = segmentEnd - segmentStart;
            double t = segmentLength > 1e-9 ? (distance - segmentStart) / segmentLength : 0.0;
            sampledXy[i * 2] = LerpValue(loopXy[segmentIndex * 2], loopXy[next * 2], t);
            sampledXy[i * 2 + 1] = LerpValue(loopXy[segmentIndex * 2 + 1], loopXy[next * 2 + 1], t);
            sampledZ[i] = LerpValue(loopZ[segmentIndex], loopZ[next], t);
        }

        return true;
    }

    private static bool TryBuildBoundaryLoopFromMesh(
        double[] vertices,
        int[] faces,
        int faceCount,
        double tolerance,
        out double[] boundaryLoopXy,
        out double[] boundaryLoopZ)
    {
        return MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(
            vertices,
            faces,
            faceCount,
            tolerance,
            out boundaryLoopXy,
            out boundaryLoopZ);
    }

    private static bool TryFilterPathTopologyBandFaces(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] innerLoopXy,
        int innerVertexCount,
        double[] outerLoopXy,
        int outerVertexCount,
        out double[] filteredVertices,
        out int filteredVertexCount,
        out int[] filteredFaces,
        out int filteredFaceCount)
    {
        filteredVertices = Array.Empty<double>();
        filteredFaces = Array.Empty<int>();
        filteredVertexCount = 0;
        filteredFaceCount = 0;

        var selectedFaces = new List<int>(faceCount);
        for (int i = 0; i < faceCount; i++)
        {
            int a = faces[i * 3];
            int b = faces[i * 3 + 1];
            int c = faces[i * 3 + 2];
            double cx = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            double cy = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;
            if (!GradingGeometry2D.PointInPolygon(cx, cy, outerLoopXy, outerVertexCount))
                continue;
            if (GradingGeometry2D.PointInPolygon(cx, cy, innerLoopXy, innerVertexCount))
                continue;

            selectedFaces.Add(i);
        }

        if (selectedFaces.Count == 0)
            return false;

        var usedVertices = new HashSet<int>();
        foreach (int faceIndex in selectedFaces)
        {
            usedVertices.Add(faces[faceIndex * 3]);
            usedVertices.Add(faces[faceIndex * 3 + 1]);
            usedVertices.Add(faces[faceIndex * 3 + 2]);
        }

        var remap = new Dictionary<int, int>(usedVertices.Count);
        filteredVertexCount = usedVertices.Count;
        filteredVertices = new double[filteredVertexCount * 3];
        int nextVertex = 0;
        foreach (int originalVertex in usedVertices.OrderBy(static value => value))
        {
            remap[originalVertex] = nextVertex;
            filteredVertices[nextVertex * 3] = vertices[originalVertex * 3];
            filteredVertices[nextVertex * 3 + 1] = vertices[originalVertex * 3 + 1];
            filteredVertices[nextVertex * 3 + 2] = vertices[originalVertex * 3 + 2];
            nextVertex++;
        }

        filteredFaceCount = selectedFaces.Count;
        filteredFaces = new int[filteredFaceCount * 3];
        for (int i = 0; i < selectedFaces.Count; i++)
        {
            int faceIndex = selectedFaces[i];
            filteredFaces[i * 3] = remap[faces[faceIndex * 3]];
            filteredFaces[i * 3 + 1] = remap[faces[faceIndex * 3 + 1]];
            filteredFaces[i * 3 + 2] = remap[faces[faceIndex * 3 + 2]];
        }

        return true;
    }

    private static bool TryBuildSeamLoopFromOutsideMesh(
        double[] vertices,
        int[] faces,
        int faceCount,
        double[] referenceLoopXy,
        double tolerance,
        out double[] seamLoopXy)
    {
        seamLoopXy = Array.Empty<double>();
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[f * 3 + 1];
            int c = faces[f * 3 + 2];
            IncrEdge(edgeFaceCount, a, b);
            IncrEdge(edgeFaceCount, b, c);
            IncrEdge(edgeFaceCount, c, a);
        }

        var adjacency = new Dictionary<int, List<int>>();
        int segmentCount = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double mx = (vertices[a * 3] + vertices[b * 3]) * 0.5;
            double my = (vertices[a * 3 + 1] + vertices[b * 3 + 1]) * 0.5;
            if (GradingGeometry2D.DistanceToPolygon(mx, my, referenceLoopXy, referenceLoopXy.Length / 2) > tolerance * 4.0)
                continue;

            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
            segmentCount++;
        }

        if (segmentCount < 3 || adjacency.Count < 3)
            return false;

        foreach (var neighbors in adjacency.Values)
        {
            if (neighbors.Count != 2)
                return false;
        }

        int start = adjacency.Keys.Min();
        var order = new List<int>(adjacency.Count);
        int previous = -1;
        int current = start;
        while (true)
        {
            order.Add(current);
            var neighbors = adjacency[current];
            int next = neighbors[0] != previous ? neighbors[0] : neighbors[1];
            previous = current;
            current = next;

            if (current == start)
                break;

            if (order.Count > adjacency.Count)
                return false;
        }

        if (order.Count < 3 || order.Count != adjacency.Count)
            return false;

        seamLoopXy = new double[order.Count * 2];
        for (int i = 0; i < order.Count; i++)
        {
            int vertexIndex = order[i];
            seamLoopXy[i * 2] = vertices[vertexIndex * 3];
            seamLoopXy[i * 2 + 1] = vertices[vertexIndex * 3 + 1];
        }

        return true;
    }

    private static void AddBoundaryNeighbor(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out List<int>? neighbors))
        {
            neighbors = new List<int>(2);
            adjacency[from] = neighbors;
        }

        if (!neighbors.Contains(to))
            neighbors.Add(to);
    }

    private static double LerpValue(double start, double end, double t)
        => start + ((end - start) * t);

    private static void MergeMeshes(
        double[] firstVertices,
        int firstVertexCount,
        int[] firstFaces,
        int firstFaceCount,
        double[] secondVertices,
        int secondVertexCount,
        int[] secondFaces,
        int secondFaceCount,
        double tolerance,
        out double[] mergedVertices,
        out int mergedVertexCount,
        out int[] mergedFaces,
        out int mergedFaceCount)
    {
        MeshTopologyOperations.MergeMeshes(
            firstVertices,
            firstVertexCount,
            firstFaces,
            firstFaceCount,
            secondVertices,
            secondVertexCount,
            secondFaces,
            secondFaceCount,
            tolerance,
            CoincidentVertexZPolicy.UseLatest,
            out mergedVertices,
            out mergedVertexCount,
            out mergedFaces,
            out mergedFaceCount);
    }


    private static void AddLoopPoint(List<double> points, double x, double y, double tolerance)
    {
        if (points.Count >= 2)
        {
            double dx = x - points[^2];
            double dy = y - points[^1];
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                return;
        }

        points.Add(x);
        points.Add(y);
    }

    private static bool VerticesCoincident(double[] vertices, int firstIndex, int secondIndex, double tolerance)
    {
        double dx = vertices[firstIndex * 3] - vertices[secondIndex * 3];
        double dy = vertices[firstIndex * 3 + 1] - vertices[secondIndex * 3 + 1];
        return (dx * dx) + (dy * dy) <= tolerance * tolerance;
    }

    private static double DistanceSquaredXY(double[] vertices, int firstIndex, int secondIndex)
    {
        double dx = vertices[firstIndex * 3] - vertices[secondIndex * 3];
        double dy = vertices[firstIndex * 3 + 1] - vertices[secondIndex * 3 + 1];
        return (dx * dx) + (dy * dy);
    }

    private static void AddTriangleIfNonDegenerate(double[] vertices, List<int> faces, int a, int b, int c, double tolerance)
    {
        if (a == b || b == c || c == a)
            return;

        double ax = vertices[a * 3];
        double ay = vertices[a * 3 + 1];
        double bx = vertices[b * 3];
        double by = vertices[b * 3 + 1];
        double cx = vertices[c * 3];
        double cy = vertices[c * 3 + 1];
        double area2 = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        if (Math.Abs(area2) <= tolerance * tolerance)
            return;

        faces.Add(a);
        faces.Add(b);
        faces.Add(c);
    }

    private static int DeterminePathShoulderGuideRowCount(
        double[] leftRoadXy,
        double[] rightRoadXy,
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        int vertexCount,
        double tolerance)
    {
        double maxWidth = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            double leftDx = leftShoulderXy[i * 2] - leftRoadXy[i * 2];
            double leftDy = leftShoulderXy[i * 2 + 1] - leftRoadXy[i * 2 + 1];
            double rightDx = rightShoulderXy[i * 2] - rightRoadXy[i * 2];
            double rightDy = rightShoulderXy[i * 2 + 1] - rightRoadXy[i * 2 + 1];
            maxWidth = Math.Max(maxWidth, Math.Sqrt((leftDx * leftDx) + (leftDy * leftDy)));
            maxWidth = Math.Max(maxWidth, Math.Sqrt((rightDx * rightDx) + (rightDy * rightDy)));
        }

        double targetStripWidth = Math.Max(tolerance * 100.0, 0.5);
        int stripCount = (int)Math.Ceiling(maxWidth / targetStripWidth);
        return Math.Clamp(stripCount - 1, 0, 2);
    }

    internal static bool IsIsoElevationStrip(double[] innerZ, double[] outerZ, int vertexCount, double tolerance)
    {
        if (vertexCount <= 0)
            return true;

        double zTolerance = Math.Max(tolerance * 2.0, 1e-4);
        for (int i = 0; i < vertexCount; i++)
        {
            if (Math.Abs(innerZ[i] - outerZ[i]) > zTolerance)
                return false;
        }

        return true;
    }

    internal static bool TryBuildLocalizedFallbackBoundary(
        double[] vertices,
        int vertexCount,
        PathDefinition path,
        double tolerance,
        out double[] boundaryLoopXy)
    {
        boundaryLoopXy = Array.Empty<double>();
        if (path.VertexCount < 2 || vertexCount <= 0 || vertices.Length < vertexCount * 3)
            return false;

        double dedupTol = Math.Clamp(tolerance, 1e-6, 1e-3);
        double shoulderDistance = ComputePathShoulderDistance(vertices, vertexCount, path);
        double minimumShoulderDistance = Math.Max(path.Width * 0.25, dedupTol * 8.0);
        double boundaryShoulderDistance = Math.Max(shoulderDistance, minimumShoulderDistance);
        // Localized road-edge fallback only needs enough surrounding topology to
        // support constrained road-edge insertion. It should not claim the full
        // grading shoulder reach, or the "localized" split expands back toward a
        // near-whole-mesh remesh on coarse terrain.
        double topologySupportDistance = Math.Min(
            boundaryShoulderDistance,
            Math.Max(path.Width, dedupTol * 16.0));
        double paddingDistance = Math.Max(dedupTol * 8.0, Math.Max(path.Width * 0.05, topologySupportDistance * 0.1));
        double offsetDistance = (path.Width * 0.5) + topologySupportDistance + paddingDistance;
        if (!double.IsFinite(offsetDistance) || offsetDistance <= dedupTol)
            return false;

        ConstraintPath samplePath = BuildConstraintPolyline(
            path,
            ComputeConstraintSegmentLength(path, boundaryShoulderDistance),
            dedupTol);
        if (samplePath.VertexCount < 2)
            return false;

        var leftBoundaryXy = new double[samplePath.VertexCount * 2];
        var rightBoundaryXy = new double[samplePath.VertexCount * 2];
        for (int i = 0; i < samplePath.VertexCount; i++)
        {
            double cx = samplePath.XyVertices[i * 2];
            double cy = samplePath.XyVertices[i * 2 + 1];
            GetConstraintPathTangent(samplePath, i, out double tangentX, out double tangentY);
            double normalX = -tangentY;
            double normalY = tangentX;

            leftBoundaryXy[i * 2] = cx + (normalX * offsetDistance);
            leftBoundaryXy[i * 2 + 1] = cy + (normalY * offsetDistance);
            rightBoundaryXy[i * 2] = cx - (normalX * offsetDistance);
            rightBoundaryXy[i * 2 + 1] = cy - (normalY * offsetDistance);
        }

        return TryBuildPathSeamLoop(leftBoundaryXy, rightBoundaryXy, samplePath, dedupTol, out boundaryLoopXy);
    }

    private static bool ShouldSimplifyPlanarPathStrip(
        double[] innerXy,
        double[] outerXy,
        double[] innerZ,
        double[] outerZ,
        int vertexCount,
        double tolerance)
    {
        return HasDistinctShoulderSamples(innerXy, outerXy, vertexCount, tolerance) &&
               IsIsoElevationStrip(innerZ, outerZ, vertexCount, tolerance);
    }

    private static double[][] BuildInterpolatedPathGuideRows(
        double[] innerRowXy,
        double[] outerRowXy,
        int vertexCount,
        int guideRowCount)
    {
        if (guideRowCount <= 0)
            return Array.Empty<double[]>();

        var rows = new double[guideRowCount][];
        for (int rowIndex = 0; rowIndex < guideRowCount; rowIndex++)
        {
            double t = (double)(rowIndex + 1) / (guideRowCount + 1);
            var row = new double[vertexCount * 2];
            for (int i = 0; i < vertexCount; i++)
            {
                row[i * 2] = innerRowXy[i * 2] + ((outerRowXy[i * 2] - innerRowXy[i * 2]) * t);
                row[i * 2 + 1] = innerRowXy[i * 2 + 1] + ((outerRowXy[i * 2 + 1] - innerRowXy[i * 2 + 1]) * t);
            }

            rows[rowIndex] = row;
        }

        return rows;
    }
}
