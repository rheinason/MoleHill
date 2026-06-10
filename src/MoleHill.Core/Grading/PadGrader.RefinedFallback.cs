using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private static GradingResult? GradeWithRefinedZOnlyFallback(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double modelTolerance,
        string fallbackReason,
        IReadOnlyList<GradingDiagnostic>? constraintFirstFailureDiagnostics = null,
        bool preferLocalRefinement = false)
    {
        if (!preferLocalRefinement &&
            pads.Length > 1 &&
            TryBuildWholeMeshRetriangulatedFallbackTopology(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pads,
                modelTolerance,
                out double[] preferredWholeMeshVertices,
                out int preferredWholeMeshVertexCount,
                out int[] preferredWholeMeshFaces,
                out int preferredWholeMeshFaceCount,
                out string? preferredWholeMeshWarning))
        {
            return BuildRefinedFallbackResult(
                vertexCount,
                faceCount,
                preferredWholeMeshVertices,
                preferredWholeMeshVertexCount,
                preferredWholeMeshFaces,
                preferredWholeMeshFaceCount,
                pads,
                lockCurves,
                modelTolerance,
                "grade_pad.topology.constrained_whole_mesh_retriangulation_fallback",
                $"Grade Pad constrained whole-mesh retriangulation fallback used for protected-pad topology after constraint-first could not preserve constraints. Reason: {fallbackReason}",
                "Grade Pad protected apron handled by constrained whole-mesh retriangulation fallback.",
                "Grade Pad interacting pad ownership resolved after constrained whole-mesh retriangulation fallback.",
                preferredWholeMeshWarning,
                constraintFirstFailureDiagnostics);
        }

        bool localTopologyBuilt = TryBuildLocallyRefinedFallbackTopology(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pads,
                lockCurves,
                modelTolerance,
                out double[] refinedOriginalVertices,
                out int refinedVertexCount,
                out int[] refinedFaces,
                out int refinedFaceCount,
                out int localSplitFaceCount,
                out int localCandidateFaceCount,
                out int localSplitFaceCap);
        if (!localTopologyBuilt)
        {
            return TryBuildDeferredWholeMeshFallback(
                $"Grade Pad whole-mesh retriangulation fallback used because local refinement was unavailable after constraint-first could not preserve constraints. Reason: {fallbackReason}");
        }

        MeshTopologyValidator.BoundaryGraphAnalysis localTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(refinedFaces, refinedFaceCount);
        if (!localTopology.HasSingleClosedBoundaryLoop)
        {
            return TryBuildDeferredWholeMeshFallback(
                $"Grade Pad whole-mesh retriangulation fallback used because local refinement kept unhealthy upstream boundaries after constraint-first could not preserve constraints. Reason: {fallbackReason}");
        }

        GradingResult localResult = BuildRefinedFallbackResult(
            vertexCount,
            faceCount,
            refinedOriginalVertices,
            refinedVertexCount,
            refinedFaces,
            refinedFaceCount,
            pads,
            lockCurves,
            modelTolerance,
            "grade_pad.topology.local_refinement_fallback",
            $"Grade Pad local refinement fallback used for protected-pad topology after constraint-first could not preserve constraints. Reason: {fallbackReason}",
            "Grade Pad protected apron handled by local refinement fallback.",
            "Grade Pad interacting pad ownership resolved after local refinement fallback.",
            $"Grade Pad local refinement fallback split {localSplitFaceCount:N0} upstream face(s) touched by pad influence ({localCandidateFaceCount:N0} candidate face(s), cap {localSplitFaceCap:N0}).",
            constraintFirstFailureDiagnostics: constraintFirstFailureDiagnostics);

        if (TryGetMaxSlopeDeviation(localResult, out double localMaxDelta) &&
            localMaxDelta > 20.0)
        {
            for (int refinementPass = 2; refinementPass <= 3 && localMaxDelta > 20.0; refinementPass++)
            {
                if (!TryBuildLocallyRefinedFallbackTopology(
                        refinedOriginalVertices,
                        refinedVertexCount,
                        refinedFaces,
                        refinedFaceCount,
                        pads,
                        lockCurves,
                        modelTolerance,
                        out double[] nextVertices,
                        out int nextVertexCount,
                        out int[] nextFaces,
                        out int nextFaceCount,
                        out int nextSplitFaceCount,
                        out int nextCandidateFaceCount,
                        out int nextSplitFaceCap))
                {
                    break;
                }

                GradingResult nextResult = BuildRefinedFallbackResult(
                    vertexCount,
                    faceCount,
                    nextVertices,
                    nextVertexCount,
                    nextFaces,
                    nextFaceCount,
                    pads,
                    lockCurves,
                    modelTolerance,
                    "grade_pad.topology.local_refinement_fallback",
                    $"Grade Pad local refinement fallback pass {refinementPass:N0} used for protected-pad topology because the previous local pass still had {localMaxDelta:F2} deg max slope deviation.",
                    "Grade Pad protected apron handled by local refinement fallback.",
                    "Grade Pad interacting pad ownership resolved after local refinement fallback.",
                    $"Grade Pad local refinement fallback pass {refinementPass:N0} split {nextSplitFaceCount:N0} upstream face(s) touched by pad influence ({nextCandidateFaceCount:N0} candidate face(s), cap {nextSplitFaceCap:N0}).",
                    constraintFirstFailureDiagnostics: constraintFirstFailureDiagnostics);

                if (!TryGetMaxSlopeDeviation(nextResult, out double nextMaxDelta))
                {
                    localResult = nextResult;
                    break;
                }

                if (nextMaxDelta >= localMaxDelta - 1.0)
                    break;

                refinedOriginalVertices = nextVertices;
                refinedVertexCount = nextVertexCount;
                refinedFaces = nextFaces;
                refinedFaceCount = nextFaceCount;
                localResult = nextResult;
                localMaxDelta = nextMaxDelta;
            }

            GradingResult? wholeMeshFallback = TryBuildDeferredWholeMeshFallback(
                $"Grade Pad whole-mesh retriangulation fallback used because local refinement produced excessive slope deviation ({localMaxDelta:F2} deg) after constraint-first could not preserve constraints. Reason: {fallbackReason}");
            if (wholeMeshFallback != null &&
                ShouldUseProtectedPadFallbackResult(localResult, wholeMeshFallback, out _))
            {
                return wholeMeshFallback;
            }
        }

        return localResult;

        GradingResult? TryBuildDeferredWholeMeshFallback(string headlineMessage)
        {
            if (!TryBuildWholeMeshRetriangulatedFallbackTopology(
                    vertices,
                    vertexCount,
                    faces,
                    faceCount,
                    pads,
                    modelTolerance,
                    out double[] wholeMeshVertices,
                    out int wholeMeshVertexCount,
                    out int[] wholeMeshFaces,
                    out int wholeMeshFaceCount,
                    out string? wholeMeshWarning))
            {
                return null;
            }

            return BuildRefinedFallbackResult(
                vertexCount,
                faceCount,
                wholeMeshVertices,
                wholeMeshVertexCount,
                wholeMeshFaces,
                wholeMeshFaceCount,
                pads,
                lockCurves,
                modelTolerance,
                "grade_pad.topology.whole_mesh_retriangulation_fallback",
                headlineMessage,
                "Grade Pad protected apron handled by whole-mesh retriangulation fallback.",
                "Grade Pad interacting pad ownership resolved after whole-mesh retriangulation fallback.",
                wholeMeshWarning,
                constraintFirstFailureDiagnostics);
        }
    }

    private static GradingResult BuildRefinedFallbackResult(
        int inputVertexCount,
        int inputFaceCount,
        double[] topologyVertices,
        int topologyVertexCount,
        int[] topologyFaces,
        int topologyFaceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double modelTolerance,
        string diagnosticCode,
        string headlineMessage,
        string apronMessage,
        string ownershipMessage,
        string? topologyWarning,
        IReadOnlyList<GradingDiagnostic>? constraintFirstFailureDiagnostics)
    {
        double[] gradedVertices = ApplyGradingZ(
            topologyVertices,
            topologyVertexCount,
            topologyFaces,
            topologyFaceCount,
            pads,
            lockCurves);
        var diagnostics = new List<string>
        {
            headlineMessage,
            apronMessage,
            ownershipMessage,
            GradingTopologyDiagnostics.BuildMeshSummaryMessage(
                "Grade Pad",
                inputVertexCount,
                inputFaceCount,
                topologyVertexCount,
                topologyFaceCount,
                topologyFaces)
        };
        if (!string.IsNullOrWhiteSpace(topologyWarning))
            diagnostics.Add(topologyWarning!);

        var structuredDiagnostics = new List<GradingDiagnostic>
        {
            GradingDiagnostic.Warning(
                diagnosticCode,
                diagnostics[0],
                operation: "grade_pad")
        };
        if (constraintFirstFailureDiagnostics != null)
            structuredDiagnostics.AddRange(constraintFirstFailureDiagnostics);
        AppendPadOutputSlopeDiagnostics(
            topologyVertices,
            topologyVertexCount,
            topologyFaces,
            topologyFaceCount,
            gradedVertices,
            pads,
            lockCurves,
            modelTolerance,
            diagnostics,
            structuredDiagnostics);

        return GradingResultBuilder.BuildFromXyz(
            topologyVertices,
            gradedVertices,
            topologyVertexCount,
            topologyFaces,
            topologyFaceCount,
            BuildPadBoundaryPolylines(pads),
            diagnostics,
            BuildPadPatchSummaries(pads),
            structuredDiagnostics);
    }

    private static bool TryBuildWholeMeshRetriangulatedFallbackTopology(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        double modelTolerance,
        out double[] outputVertices,
        out int outputVertexCount,
        out int[] outputFaces,
        out int outputFaceCount,
        out string? warning)
    {
        outputVertices = Array.Empty<double>();
        outputVertexCount = 0;
        outputFaces = Array.Empty<int>();
        outputFaceCount = 0;
        warning = null;

        if (vertexCount < 3 || vertices.Length < vertexCount * 3)
            return false;

        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        var faceGrid = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        var xy = new List<double>(vertexCount * 2);
        var pointIndexByKey = new Dictionary<FallbackPointKey, int>(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            AddFallbackPoint(
                xy,
                pointIndexByKey,
                vertices[i * 3],
                vertices[(i * 3) + 1],
                tolerance);
        }

        var segments = new List<(int a, int b)>();
        for (int padIndex = 0; padIndex < pads.Length; padIndex++)
        {
            PadBoundary pad = pads[padIndex];
            if (pad.VertexCount < 3 || pad.XyVertices.Length < pad.VertexCount * 2)
                continue;

            var padVertexIndices = new int[pad.VertexCount];
            double centerX = 0.0;
            double centerY = 0.0;
            for (int vertexIndex = 0; vertexIndex < pad.VertexCount; vertexIndex++)
            {
                double x = pad.XyVertices[vertexIndex * 2];
                double y = pad.XyVertices[(vertexIndex * 2) + 1];
                centerX += x;
                centerY += y;
                padVertexIndices[vertexIndex] = AddFallbackPoint(
                    xy,
                    pointIndexByKey,
                    x,
                    y,
                    tolerance);
            }

            for (int vertexIndex = 0; vertexIndex < pad.VertexCount; vertexIndex++)
            {
                int nextIndex = (vertexIndex + 1) % pad.VertexCount;
                int a = padVertexIndices[vertexIndex];
                int b = padVertexIndices[nextIndex];
                if (a != b)
                    segments.Add((a, b));
            }

            AddFallbackPoint(
                xy,
                pointIndexByKey,
                centerX / pad.VertexCount,
                centerY / pad.VertexCount,
                tolerance);
        }

        outputVertexCount = xy.Count / 2;
        TriangulationOutcome outcome = TriangulationHelper.Triangulate(
            xy,
            outputVertexCount,
            segments,
            maxArea: 0.0,
            minAngle: 0.0,
            convex: true,
            segmentSplitting: 0);
        if (outcome.Mesh == null)
        {
            warning = outcome.WarningMessage;
            return false;
        }

        TriangleNetExtractor.Result extracted = TriangleNetExtractor.Extract(outcome.Mesh);
        if (extracted.VertexCount < 3 || extracted.FaceCount <= 0)
            return false;

        outputVertexCount = extracted.VertexCount;
        outputVertices = new double[outputVertexCount * 3];
        for (int i = 0; i < outputVertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[(i * 2) + 1];
            int sourceId = extracted.SourceIds[i];
            outputVertices[i * 3] = x;
            outputVertices[(i * 3) + 1] = y;
            outputVertices[(i * 3) + 2] = sourceId >= 0 && sourceId < vertexCount
                ? vertices[(sourceId * 3) + 2]
                : faceGrid.InterpolateZ(x, y);
        }

        outputFaces = extracted.Faces;
        outputFaceCount = extracted.FaceCount;

        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(outputFaces, outputFaceCount);
        if (!topology.HasSingleClosedBoundaryLoop)
        {
            warning =
                $"Grade Pad whole-mesh retriangulation fallback rejected unhealthy topology: boundary edges={topology.BoundaryEdgeCount:N0}, components={topology.BoundaryComponentCount:N0}, open chains={topology.HasOpenBoundaryChains}, nonmanifold={topology.NonManifoldEdgeCount:N0}.";
            return false;
        }

        warning = outcome.WarningMessage;
        return true;
    }

    private readonly record struct FallbackPointKey(long X, long Y);

    private static int AddFallbackPoint(
        List<double> xy,
        Dictionary<FallbackPointKey, int> indexByKey,
        double x,
        double y,
        double tolerance)
    {
        var key = new FallbackPointKey(
            QuantizeFallbackCoordinate(x, tolerance),
            QuantizeFallbackCoordinate(y, tolerance));
        if (indexByKey.TryGetValue(key, out int existingIndex))
            return existingIndex;

        int index = xy.Count / 2;
        xy.Add(x);
        xy.Add(y);
        indexByKey.Add(key, index);
        return index;
    }

    private static long QuantizeFallbackCoordinate(double value, double tolerance)
    {
        double scale = Math.Max(Math.Abs(tolerance), 1e-9);
        double scaled = value / scale;
        if (!double.IsFinite(scaled))
            return value < 0.0 ? long.MinValue : long.MaxValue;
        if (scaled >= long.MaxValue)
            return long.MaxValue;
        if (scaled <= long.MinValue)
            return long.MinValue;

        return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    internal static bool TryBuildLocallyRefinedFallbackTopology(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double modelTolerance,
        out double[] refinedVertices,
        out int refinedVertexCount,
        out int[] refinedFaces,
        out int refinedFaceCount,
        out int splitFaceCount,
        out int candidateFaceCount,
        out int splitFaceCap)
    {
        refinedVertices = Array.Empty<double>();
        refinedFaces = Array.Empty<int>();
        refinedVertexCount = 0;
        refinedFaceCount = 0;
        splitFaceCount = 0;
        candidateFaceCount = 0;
        splitFaceCap = 0;

        if (vertexCount <= 0 || faceCount <= 0 || pads.Length == 0)
            return false;

        var facesToSplit = new Dictionary<int, int>();
        if (vertexCount > 64)
        {
            splitFaceCap = ComputeLocalRefinementSplitFaceCap(faceCount, pads.Length);
            int[] selectedFaces = SelectLocalRefinementFaces(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pads,
                lockCurves ?? Array.Empty<LockCurve>(),
                modelTolerance,
                splitFaceCap,
                out candidateFaceCount);

            facesToSplit = new Dictionary<int, int>(selectedFaces.Length);
            foreach (int faceIndex in selectedFaces)
                facesToSplit.TryAdd(faceIndex, 0);
        }

        if (facesToSplit.Count == 0)
        {
            for (int padIndex = 0; padIndex < pads.Length; padIndex++)
            {
                int faceIndex = FindFallbackSplitFace(vertices, faces, faceCount, pads[padIndex]);
                if (faceIndex >= 0)
                    facesToSplit.TryAdd(faceIndex, padIndex);
            }
        }

        if (facesToSplit.Count == 0)
        {
            int firstValidFace = FindFirstValidFallbackFace(faces, faceCount, vertexCount);
            if (firstValidFace >= 0)
                facesToSplit.Add(firstValidFace, 0);
        }

        if (facesToSplit.Count > 0)
        {
            if (candidateFaceCount == 0)
                candidateFaceCount = facesToSplit.Count;
            if (splitFaceCap == 0)
                splitFaceCap = facesToSplit.Count;
        }

        refinedVertexCount = vertexCount + facesToSplit.Count;
        refinedVertices = new double[refinedVertexCount * 3];
        Array.Copy(vertices, refinedVertices, vertexCount * 3);

        var splitVertexByFace = new Dictionary<int, int>(facesToSplit.Count);
        foreach (int faceIndex in facesToSplit.Keys)
        {
            int a = faces[faceIndex * 3];
            int b = faces[(faceIndex * 3) + 1];
            int c = faces[(faceIndex * 3) + 2];
            if (!IsValidFaceIndex(a, vertexCount) ||
                !IsValidFaceIndex(b, vertexCount) ||
                !IsValidFaceIndex(c, vertexCount))
            {
                continue;
            }

            int newIndex = vertexCount + splitVertexByFace.Count;
            splitVertexByFace.Add(faceIndex, newIndex);
            refinedVertices[newIndex * 3] = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            refinedVertices[(newIndex * 3) + 1] = (vertices[(a * 3) + 1] + vertices[(b * 3) + 1] + vertices[(c * 3) + 1]) / 3.0;
            refinedVertices[(newIndex * 3) + 2] = (vertices[(a * 3) + 2] + vertices[(b * 3) + 2] + vertices[(c * 3) + 2]) / 3.0;
        }

        if (splitVertexByFace.Count == 0)
        {
            int firstValidFace = FindFirstValidFallbackFace(faces, faceCount, vertexCount);
            if (firstValidFace < 0)
                return false;

            int a = faces[firstValidFace * 3];
            int b = faces[(firstValidFace * 3) + 1];
            int c = faces[(firstValidFace * 3) + 2];
            int newIndex = vertexCount;
            splitVertexByFace.Add(firstValidFace, newIndex);
            refinedVertices[newIndex * 3] = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            refinedVertices[(newIndex * 3) + 1] = (vertices[(a * 3) + 1] + vertices[(b * 3) + 1] + vertices[(c * 3) + 1]) / 3.0;
            refinedVertices[(newIndex * 3) + 2] = (vertices[(a * 3) + 2] + vertices[(b * 3) + 2] + vertices[(c * 3) + 2]) / 3.0;
        }

        if (splitVertexByFace.Count == 0)
            return false;

        refinedVertexCount = vertexCount + splitVertexByFace.Count;
        if (refinedVertices.Length != refinedVertexCount * 3)
            Array.Resize(ref refinedVertices, refinedVertexCount * 3);

        refinedFaceCount = faceCount + (splitVertexByFace.Count * 2);
        refinedFaces = new int[refinedFaceCount * 3];
        int writeFace = 0;
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[(faceIndex * 3) + 1];
            int c = faces[(faceIndex * 3) + 2];

            if (splitVertexByFace.TryGetValue(faceIndex, out int center))
            {
                WriteFace(refinedFaces, ref writeFace, a, b, center);
                WriteFace(refinedFaces, ref writeFace, b, c, center);
                WriteFace(refinedFaces, ref writeFace, c, a, center);
            }
            else
            {
                WriteFace(refinedFaces, ref writeFace, a, b, c);
            }
        }

        refinedFaceCount = writeFace;
        if (refinedFaces.Length != refinedFaceCount * 3)
            Array.Resize(ref refinedFaces, refinedFaceCount * 3);

        splitFaceCount = splitVertexByFace.Count;
        return refinedVertexCount > vertexCount && refinedFaceCount > faceCount;
    }

    private readonly record struct LocalRefinementFaceCandidate(int FaceIndex, double Score);

    private static int ComputeLocalRefinementSplitFaceCap(int faceCount, int padCount)
    {
        if (faceCount <= 0 || padCount <= 0)
            return 0;

        int perPadBudget = Math.Max(256, padCount * 1024);
        return Math.Min(faceCount, Math.Min(8192, perPadBudget));
    }

    private static int[] SelectLocalRefinementFaces(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[] lockCurves,
        double modelTolerance,
        int splitFaceCap,
        out int candidateFaceCount)
    {
        candidateFaceCount = 0;
        if (splitFaceCap <= 0)
            return Array.Empty<int>();

        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        var terrainFaceGrid = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        PreparedBarriers barriers = lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;
        bool hasBoundaryLoop = TryBuildBoundaryLoop(vertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        var preparedPads = new List<PreparedPadSections>(pads.Length);
        var preparedBounds = new List<Bounds2D>(pads.Length);
        foreach (PadBoundary pad in pads)
        {
            PreparedPadSections prepared = BuildPreparedPadSections(
                pad,
                terrainFaceGrid,
                barriers,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance,
                keepShoulderOnBatterPlane: false,
                defaultCornerFanSegments: 6);
            var bounds = new Bounds2D(
                prepared.InfluenceMinX,
                prepared.InfluenceMaxX,
                prepared.InfluenceMinY,
                prepared.InfluenceMaxY);
            if (!IsFiniteBounds(bounds))
                continue;

            preparedPads.Add(prepared);
            preparedBounds.Add(bounds);
        }

        if (preparedPads.Count == 0)
            return Array.Empty<int>();

        SpatialHashGrid2D padIndex = SpatialHashGrid2D.Build(preparedBounds.ToArray());
        var padCandidates = new List<int>(Math.Min(8, preparedPads.Count));
        var padScratch = new SpatialHashGrid2D.QueryScratch(preparedPads.Count);
        var faceCandidates = new List<LocalRefinementFaceCandidate>();

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[(faceIndex * 3) + 1];
            int c = faces[(faceIndex * 3) + 2];
            if (!IsValidFaceIndex(a, vertexCount) ||
                !IsValidFaceIndex(b, vertexCount) ||
                !IsValidFaceIndex(c, vertexCount) ||
                a == b ||
                b == c ||
                c == a)
            {
                continue;
            }

            double ax = vertices[a * 3];
            double ay = vertices[(a * 3) + 1];
            double bx = vertices[b * 3];
            double by = vertices[(b * 3) + 1];
            double cx = vertices[c * 3];
            double cy = vertices[(c * 3) + 1];
            var faceBounds = new Bounds2D(
                Math.Min(ax, Math.Min(bx, cx)) - tolerance,
                Math.Max(ax, Math.Max(bx, cx)) + tolerance,
                Math.Min(ay, Math.Min(by, cy)) - tolerance,
                Math.Max(ay, Math.Max(by, cy)) + tolerance);

            padIndex.GatherCandidates(faceBounds, padCandidates, padScratch);
            if (padCandidates.Count == 0)
                continue;

            double bestScore = double.MaxValue;
            bool touchesInfluence = false;
            foreach (int padIndexHit in padCandidates)
            {
                if (!preparedBounds[padIndexHit].Intersects(faceBounds))
                    continue;

                if (!FaceTouchesPreparedPadInfluence(
                        ax,
                        ay,
                        bx,
                        by,
                        cx,
                        cy,
                        preparedPads[padIndexHit],
                        tolerance,
                        out double score))
                {
                    continue;
                }

                touchesInfluence = true;
                bestScore = Math.Min(bestScore, score);
            }

            if (!touchesInfluence)
                continue;

            faceCandidates.Add(new LocalRefinementFaceCandidate(faceIndex, bestScore));
        }

        candidateFaceCount = faceCandidates.Count;
        if (faceCandidates.Count == 0)
            return Array.Empty<int>();

        return faceCandidates
            .OrderBy(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.FaceIndex)
            .Take(splitFaceCap)
            .Select(static candidate => candidate.FaceIndex)
            .ToArray();
    }

    private static bool FaceTouchesPreparedPadInfluence(
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy,
        PreparedPadSections prepared,
        double tolerance,
        out double score)
    {
        score = double.MaxValue;
        ReadOnlySpan<(double X, double Y)> samples =
        [
            (ax, ay),
            (bx, by),
            (cx, cy),
            ((ax + bx + cx) / 3.0, (ay + by + cy) / 3.0),
            ((ax + bx) * 0.5, (ay + by) * 0.5),
            ((bx + cx) * 0.5, (by + cy) * 0.5),
            ((cx + ax) * 0.5, (cy + ay) * 0.5)
        ];

        foreach ((double x, double y) in samples)
        {
            if (!PointTouchesPreparedPadInfluence(x, y, prepared, tolerance, out double pointScore))
                continue;

            score = Math.Min(score, pointScore);
            return true;
        }

        if (LoopIntersectsTriangle(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, ax, ay, bx, by, cx, cy) ||
            LoopIntersectsTriangle(prepared.ShoulderXy, prepared.BoundaryVertexCount, ax, ay, bx, by, cx, cy) ||
            LoopHasVertexInsideTriangle(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, ax, ay, bx, by, cx, cy) ||
            LoopHasVertexInsideTriangle(prepared.ShoulderXy, prepared.BoundaryVertexCount, ax, ay, bx, by, cx, cy))
        {
            double centroidX = (ax + bx + cx) / 3.0;
            double centroidY = (ay + by + cy) / 3.0;
            score = ComputePreparedPadInfluenceScore(centroidX, centroidY, prepared);
            return true;
        }

        return false;
    }

    private static bool PointTouchesPreparedPadInfluence(
        double x,
        double y,
        PreparedPadSections prepared,
        double tolerance,
        out double score)
    {
        score = double.MaxValue;
        double padding = Math.Max(tolerance * 2.0, 1e-6);
        if (x < prepared.InfluenceMinX - padding ||
            x > prepared.InfluenceMaxX + padding ||
            y < prepared.InfluenceMinY - padding ||
            y > prepared.InfluenceMaxY + padding)
        {
            return false;
        }

        if (PointInPolygon(x, y, prepared.Pad.XyVertices, prepared.Pad.VertexCount))
        {
            score = 0.0;
            return true;
        }

        if (!TryFindClosestLoopLocation(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, x, y, out ClosestLoopLocation closest) ||
            !TryInterpolatePadSection(
                prepared,
                closest,
                out double boundaryX,
                out double boundaryY,
                out _,
                out double shoulderX,
                out double shoulderY,
                out _))
        {
            return false;
        }

        double reach = Math.Sqrt(((shoulderX - boundaryX) * (shoulderX - boundaryX)) + ((shoulderY - boundaryY) * (shoulderY - boundaryY)));
        if (closest.Distance > reach + padding)
            return false;

        score = closest.Distance;
        return true;
    }

    private static double ComputePreparedPadInfluenceScore(double x, double y, PreparedPadSections prepared)
    {
        if (!TryFindClosestLoopLocation(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, x, y, out ClosestLoopLocation closest))
            return double.MaxValue;

        return closest.Distance;
    }

    private static bool LoopIntersectsTriangle(
        double[] loopXy,
        int vertexCount,
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy)
    {
        if (vertexCount < 2 || loopXy.Length < vertexCount * 2)
            return false;

        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double sx0 = loopXy[i * 2];
            double sy0 = loopXy[(i * 2) + 1];
            double sx1 = loopXy[next * 2];
            double sy1 = loopXy[(next * 2) + 1];

            if (SegmentsIntersect(sx0, sy0, sx1, sy1, ax, ay, bx, by) ||
                SegmentsIntersect(sx0, sy0, sx1, sy1, bx, by, cx, cy) ||
                SegmentsIntersect(sx0, sy0, sx1, sy1, cx, cy, ax, ay))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LoopHasVertexInsideTriangle(
        double[] loopXy,
        int vertexCount,
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy)
    {
        if (vertexCount < 1 || loopXy.Length < vertexCount * 2)
            return false;

        for (int i = 0; i < vertexCount; i++)
        {
            if (PointInTriangle(loopXy[i * 2], loopXy[(i * 2) + 1], ax, ay, bx, by, cx, cy))
                return true;
        }

        return false;
    }

    private static bool PointInTriangle(
        double px,
        double py,
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy)
    {
        const double tolerance = 1e-12;
        double o1 = Orientation(ax, ay, bx, by, px, py);
        double o2 = Orientation(bx, by, cx, cy, px, py);
        double o3 = Orientation(cx, cy, ax, ay, px, py);
        bool hasNegative = o1 < -tolerance || o2 < -tolerance || o3 < -tolerance;
        bool hasPositive = o1 > tolerance || o2 > tolerance || o3 > tolerance;
        return !(hasNegative && hasPositive);
    }

    private static bool IsFiniteBounds(Bounds2D bounds)
    {
        return double.IsFinite(bounds.MinX) &&
               double.IsFinite(bounds.MaxX) &&
               double.IsFinite(bounds.MinY) &&
               double.IsFinite(bounds.MaxY) &&
               bounds.MinX <= bounds.MaxX &&
               bounds.MinY <= bounds.MaxY;
    }

    private static int FindFallbackSplitFace(
        double[] vertices,
        int[] faces,
        int faceCount,
        PadBoundary pad)
    {
        double padCenterX = 0.0;
        double padCenterY = 0.0;
        for (int i = 0; i < pad.VertexCount; i++)
        {
            padCenterX += pad.XyVertices[i * 2];
            padCenterY += pad.XyVertices[(i * 2) + 1];
        }

        padCenterX /= Math.Max(1, pad.VertexCount);
        padCenterY /= Math.Max(1, pad.VertexCount);

        int bestFace = -1;
        double bestDistanceSq = double.MaxValue;
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[(faceIndex * 3) + 1];
            int c = faces[(faceIndex * 3) + 2];
            if (!IsValidFaceIndex(a, vertices.Length / 3) ||
                !IsValidFaceIndex(b, vertices.Length / 3) ||
                !IsValidFaceIndex(c, vertices.Length / 3))
            {
                continue;
            }

            double cx = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            double cy = (vertices[(a * 3) + 1] + vertices[(b * 3) + 1] + vertices[(c * 3) + 1]) / 3.0;
            if (PointInPolygon(cx, cy, pad.XyVertices, pad.VertexCount))
                return faceIndex;

            double dx = cx - padCenterX;
            double dy = cy - padCenterY;
            double distanceSq = (dx * dx) + (dy * dy);
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                bestFace = faceIndex;
            }
        }

        return bestFace;
    }

    private static bool IsValidFaceIndex(int index, int vertexCount)
    {
        return index >= 0 && index < vertexCount;
    }

    private static int FindFirstValidFallbackFace(int[] faces, int faceCount, int vertexCount)
    {
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[(faceIndex * 3) + 1];
            int c = faces[(faceIndex * 3) + 2];
            if (IsValidFaceIndex(a, vertexCount) &&
                IsValidFaceIndex(b, vertexCount) &&
                IsValidFaceIndex(c, vertexCount) &&
                a != b &&
                b != c &&
                c != a)
            {
                return faceIndex;
            }
        }

        return -1;
    }

    private static void WriteFace(int[] faces, ref int faceIndex, int a, int b, int c)
    {
        faces[faceIndex * 3] = a;
        faces[(faceIndex * 3) + 1] = b;
        faces[(faceIndex * 3) + 2] = c;
        faceIndex++;
    }
}
