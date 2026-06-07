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
        IReadOnlyList<GradingDiagnostic>? constraintFirstFailureDiagnostics = null)
    {
        if (pads.Length > 1 &&
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
            double[] preferredWholeMeshGradedVertices = ApplyGradingZ(
                preferredWholeMeshVertices,
                preferredWholeMeshVertexCount,
                preferredWholeMeshFaces,
                preferredWholeMeshFaceCount,
                pads,
                lockCurves);

            var preferredWholeMeshDiagnostics = new List<string>
            {
                $"Grade Pad constraint-first rebuild failed; constrained whole-mesh retriangulation fallback used for protected-pad topology. Failure: {fallbackReason}",
                "Grade Pad protected apron handled by constrained whole-mesh retriangulation fallback.",
                "Grade Pad interacting pad ownership resolved after constrained whole-mesh retriangulation fallback.",
                GradingTopologyDiagnostics.BuildMeshSummaryMessage(
                    "Grade Pad",
                    vertexCount,
                    faceCount,
                    preferredWholeMeshVertexCount,
                    preferredWholeMeshFaceCount,
                    preferredWholeMeshFaces)
            };
            if (!string.IsNullOrWhiteSpace(preferredWholeMeshWarning))
                preferredWholeMeshDiagnostics.Add(preferredWholeMeshWarning!);

            var preferredWholeMeshStructuredDiagnostics = new List<GradingDiagnostic>
            {
                GradingDiagnostic.Warning(
                    "grade_pad.topology.constrained_whole_mesh_retriangulation_fallback",
                    preferredWholeMeshDiagnostics[0],
                    operation: "grade_pad")
            };
            if (constraintFirstFailureDiagnostics != null)
                preferredWholeMeshStructuredDiagnostics.AddRange(constraintFirstFailureDiagnostics);
            AppendPadOutputSlopeDiagnostics(
                preferredWholeMeshVertices,
                preferredWholeMeshVertexCount,
                preferredWholeMeshFaces,
                preferredWholeMeshFaceCount,
                preferredWholeMeshGradedVertices,
                pads,
                lockCurves,
                modelTolerance,
                preferredWholeMeshDiagnostics,
                preferredWholeMeshStructuredDiagnostics);

            return GradingResultBuilder.BuildFromXyz(
                preferredWholeMeshVertices,
                preferredWholeMeshGradedVertices,
                preferredWholeMeshVertexCount,
                preferredWholeMeshFaces,
                preferredWholeMeshFaceCount,
                BuildPadBoundaryPolylines(pads),
                preferredWholeMeshDiagnostics,
                BuildPadPatchSummaries(pads),
                preferredWholeMeshStructuredDiagnostics);
        }

        if (!TryBuildLocallyRefinedFallbackTopology(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pads,
                out double[] refinedOriginalVertices,
                out int refinedVertexCount,
                out int[] refinedFaces,
                out int refinedFaceCount))
        {
            return null;
        }

        MeshTopologyValidator.BoundaryGraphAnalysis localTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(refinedFaces, refinedFaceCount);
        if (!localTopology.HasSingleClosedBoundaryLoop &&
            TryBuildWholeMeshRetriangulatedFallbackTopology(
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
            double[] wholeMeshGradedVertices = ApplyGradingZ(
                wholeMeshVertices,
                wholeMeshVertexCount,
                wholeMeshFaces,
                wholeMeshFaceCount,
                pads,
                lockCurves);

            var wholeMeshDiagnostics = new List<string>
            {
                $"Grade Pad constraint-first rebuild failed; whole-mesh retriangulation fallback used because local refinement kept unhealthy upstream boundaries. Failure: {fallbackReason}",
                "Grade Pad protected apron handled by whole-mesh retriangulation fallback.",
                "Grade Pad interacting pad ownership resolved after whole-mesh retriangulation fallback.",
                GradingTopologyDiagnostics.BuildMeshSummaryMessage(
                    "Grade Pad",
                    vertexCount,
                    faceCount,
                    wholeMeshVertexCount,
                    wholeMeshFaceCount,
                    wholeMeshFaces)
            };
            if (!string.IsNullOrWhiteSpace(wholeMeshWarning))
                wholeMeshDiagnostics.Add(wholeMeshWarning!);

            var wholeMeshStructuredDiagnostics = new List<GradingDiagnostic>
            {
                GradingDiagnostic.Warning(
                    "grade_pad.topology.whole_mesh_retriangulation_fallback",
                    wholeMeshDiagnostics[0],
                    operation: "grade_pad")
            };
            if (constraintFirstFailureDiagnostics != null)
                wholeMeshStructuredDiagnostics.AddRange(constraintFirstFailureDiagnostics);
            AppendPadOutputSlopeDiagnostics(
                wholeMeshVertices,
                wholeMeshVertexCount,
                wholeMeshFaces,
                wholeMeshFaceCount,
                wholeMeshGradedVertices,
                pads,
                lockCurves,
                modelTolerance,
                wholeMeshDiagnostics,
                wholeMeshStructuredDiagnostics);

            return GradingResultBuilder.BuildFromXyz(
                wholeMeshVertices,
                wholeMeshGradedVertices,
                wholeMeshVertexCount,
                wholeMeshFaces,
                wholeMeshFaceCount,
                BuildPadBoundaryPolylines(pads),
                wholeMeshDiagnostics,
                BuildPadPatchSummaries(pads),
                wholeMeshStructuredDiagnostics);
        }

        double[] gradedVertices = ApplyGradingZ(
            refinedOriginalVertices,
            refinedVertexCount,
            refinedFaces,
            refinedFaceCount,
            pads,
            lockCurves);

        var diagnostics = new List<string>
        {
            $"Grade Pad constraint-first rebuild failed; local refinement fallback used for protected-pad topology. Failure: {fallbackReason}",
            "Grade Pad protected apron handled by local refinement fallback.",
            "Grade Pad interacting pad ownership resolved after local refinement fallback.",
            GradingTopologyDiagnostics.BuildMeshSummaryMessage(
                "Grade Pad",
                vertexCount,
                faceCount,
                refinedVertexCount,
                refinedFaceCount,
                refinedFaces)
        };
        var structuredDiagnostics = new List<GradingDiagnostic>
        {
            GradingDiagnostic.Warning(
                "grade_pad.topology.local_refinement_fallback",
                diagnostics[0],
                operation: "grade_pad")
        };
        if (constraintFirstFailureDiagnostics != null)
            structuredDiagnostics.AddRange(constraintFirstFailureDiagnostics);
        AppendPadOutputSlopeDiagnostics(
            refinedOriginalVertices,
            refinedVertexCount,
            refinedFaces,
            refinedFaceCount,
            gradedVertices,
            pads,
            lockCurves,
            modelTolerance,
            diagnostics,
            structuredDiagnostics);

        return GradingResultBuilder.BuildFromXyz(
            refinedOriginalVertices,
            gradedVertices,
            refinedVertexCount,
            refinedFaces,
            refinedFaceCount,
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

        var faceGrid = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
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

    private static bool TryBuildLocallyRefinedFallbackTopology(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        out double[] refinedVertices,
        out int refinedVertexCount,
        out int[] refinedFaces,
        out int refinedFaceCount)
    {
        refinedVertices = Array.Empty<double>();
        refinedFaces = Array.Empty<int>();
        refinedVertexCount = 0;
        refinedFaceCount = 0;

        if (vertexCount <= 0 || faceCount <= 0 || pads.Length == 0)
            return false;

        var facesToSplit = new Dictionary<int, int>();
        for (int padIndex = 0; padIndex < pads.Length; padIndex++)
        {
            int faceIndex = FindFallbackSplitFace(vertices, faces, faceCount, pads[padIndex]);
            if (faceIndex >= 0 && !facesToSplit.ContainsKey(faceIndex))
                facesToSplit.Add(faceIndex, padIndex);
        }

        if (facesToSplit.Count == 0)
        {
            int firstValidFace = FindFirstValidFallbackFace(faces, faceCount, vertexCount);
            if (firstValidFace >= 0)
                facesToSplit.Add(firstValidFace, 0);
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

        return refinedVertexCount > vertexCount && refinedFaceCount > faceCount;
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
