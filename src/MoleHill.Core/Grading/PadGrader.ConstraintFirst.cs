using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private static GradingResult? GradeWithConstraintFirstTopology(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double modelTolerance,
        double terrainDetailSize,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        out IReadOnlyList<GradingDiagnostic> failureStructuredDiagnostics,
        out string? errorMessage)
    {
        failureOutputPolylines = Array.Empty<OutputPolyline>();
        failureStructuredDiagnostics = Array.Empty<GradingDiagnostic>();
        errorMessage = null;

        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        bool useTransitionStationConstraints = ShouldUseTransitionStationConstraints(pads, lockCurves, tolerance);

        ConstraintSet constraintSet = CreateConstraints(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            tolerance,
            includeTransitionStationConstraints: useTransitionStationConstraints);

        if (constraintSet.Constraints.Length == 0)
        {
            errorMessage = constraintSet.Diagnostics.FirstOrDefault() ?? "Grade Pad produced no usable constraints.";
            failureOutputPolylines = BuildPadBoundaryPolylines(pads);
            return null;
        }

        double requestedEdgeLength = constraintSet.SuggestedEdgeLength;
        if (terrainDetailSize > tolerance)
            requestedEdgeLength = requestedEdgeLength > 0.0 ? Math.Max(requestedEdgeLength, terrainDetailSize) : terrainDetailSize;

        OutputPolyline[] outputPolylines = BuildPadBoundaryPolylines(pads).ToArray();
        IReadOnlyList<GradingPatch> patchSummaries = BuildPadPatchSummaries(pads);

        var diagnostics = new List<string>(constraintSet.Diagnostics)
        {
            "Grade Pad topology mode: constraint-first rebuild with pad boundary, daylight, apron, and lock constraints."
        };
        if (pads.Any(pad => pad.StitchApronDistance > tolerance * 4.0))
            diagnostics.Add("Grade Pad protected apron encoded as hard topology constraints.");
        if (pads.Length > 1)
            diagnostics.Add("Grade Pad interacting pad ownership resolved by ordered pad surfaces during Z evaluation.");

        var structuredDiagnostics = new List<GradingDiagnostic>(constraintSet.StructuredDiagnostics)
        {
            GradingDiagnostic.Information(
                "grade_pad.topology.mode",
                "Grade Pad topology mode: constraint-first rebuild with pad boundary, daylight, apron, and lock constraints.",
                operation: "grade_pad")
        };
        if (pads.Any(pad => pad.StitchApronDistance > tolerance * 4.0))
        {
            structuredDiagnostics.Add(GradingDiagnostic.Information(
                "grade_pad.apron.constraints",
                "Grade Pad protected apron encoded as hard topology constraints.",
                operation: "grade_pad"));
        }
        if (pads.Length > 1)
        {
            structuredDiagnostics.Add(GradingDiagnostic.Information(
                "grade_pad.ownership.interacting_pads",
                "Grade Pad interacting pad ownership resolved by ordered pad surfaces during Z evaluation.",
                operation: "grade_pad"));
        }

        GradingResult? result = ConstraintFirstGradingEngine.TryBuild(
            "Grade Pad",
            vertices,
            vertexCount,
            faces,
            faceCount,
            constraintSet.Constraints,
            requestedEdgeLength,
            tolerance,
            (topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount) =>
                useTransitionStationConstraints
                    ? ApplyGradingZWithDefaultCornerFans(
                        topologyVertices,
                        topologyVertexCount,
                        topologyFaces,
                        topologyFaceCount,
                        pads,
                        lockCurves,
                        defaultCornerFanSegments: 6)
                    : ApplyGradingZ(
                        topologyVertices,
                        topologyVertexCount,
                        topologyFaces,
                        topologyFaceCount,
                        pads,
                        lockCurves,
                        useNearestShoulderCandidate: constraintSet.GuidePolylines.Length >= pads.Length),
            outputPolylines,
            patchSummaries,
            diagnostics,
            structuredDiagnostics,
            (topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount, gradedVertices, outputDiagnostics, outputStructuredDiagnostics) =>
                AppendPadOutputSlopeDiagnostics(
                    topologyVertices,
                    topologyVertexCount,
                    topologyFaces,
                    topologyFaceCount,
                    gradedVertices,
                    pads,
                    lockCurves,
                    tolerance,
                    outputDiagnostics,
                    outputStructuredDiagnostics),
            out failureStructuredDiagnostics,
            out errorMessage,
            guidePolylines: constraintSet.GuidePolylines);

        if (result == null)
            failureOutputPolylines = outputPolylines;

        return result;
    }

    private static bool ShouldUseTransitionStationConstraints(
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double tolerance)
    {
        if (lockCurves != null && lockCurves.Length > 0)
            return false;
        if (pads.Length <= 1)
            return true;

        bool hasProtectedPad = pads.Any(pad => pad.StitchApronDistance > tolerance * 4.0);
        if (!hasProtectedPad)
            return false;

        return pads.All(static pad => pad.SlopeAngleDeg >= 40.0);
    }

    private static void AppendPadOutputSlopeDiagnostics(
        double[] originalVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] gradedVertices,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double tolerance,
        List<string> diagnostics,
        List<GradingDiagnostic> structuredDiagnostics)
    {
        if (pads.Length == 0 || vertexCount <= 0 || faceCount <= 0)
            return;

        var terrainFaceGrid = new TerrainFaceGrid(originalVertices, vertexCount, faces, faceCount);
        PreparedBarriers barriers = GradingBarriers.BuildFromLockCurves(lockCurves ?? Array.Empty<LockCurve>());
        var patch = new PatchMeshResult
        {
            Vertices = gradedVertices,
            VertexCount = vertexCount,
            Faces = faces,
            FaceCount = faceCount
        };

        for (int padIndex = 0; padIndex < pads.Length; padIndex++)
        {
            PreparedPadSections prepared = BuildPreparedPadSections(
                pads[padIndex],
                terrainFaceGrid,
                barriers,
                hasBoundaryLoop: false,
                boundaryLoop: Array.Empty<double>(),
                boundaryVertexCount: 0,
                tolerance,
                keepShoulderOnBatterPlane: false,
                defaultCornerFanSegments: 6);

            foreach (string message in BuildPadSlopeDiagnostics(padIndex, prepared, patch, tolerance))
            {
                diagnostics.Add(message);
                structuredDiagnostics.Add(new GradingDiagnostic(
                    ClassifyPadDiagnosticSeverity(message),
                    ClassifyPadDiagnosticCode(message),
                    message,
                    Operation: "grade_pad",
                    TargetIndex: padIndex));
            }
        }
    }
}
