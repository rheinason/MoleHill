using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh by assigning a planar finished surface inside
/// boundary curves, with controlled slope transitions.
/// Re-triangulates the entire mesh with pad boundaries as constrained edges.
/// </summary>
public static partial class PadGrader
{
    public const double DefaultStitchApronDistance = 0.0;

    /// <summary>
    /// Apply pad grading to a terrain mesh.
    /// Each pad carries its own slope angle and max distance.
    /// Later pads in the array override earlier ones in overlapping zones.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        out string? errorMessage,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        return Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            out errorMessage,
            out _,
            modelTolerance,
            terrainDetailSize: 0.0);
    }

    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        out string? errorMessage,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        double modelTolerance = GradingTolerances.DefaultModelTolerance,
        double terrainDetailSize = 0.0)
    {
        errorMessage = null;
        failureOutputPolylines = Array.Empty<OutputPolyline>();
        lockCurves ??= Array.Empty<LockCurve>();

        if (!GradingInputValidator.ValidateTerrainMesh(vertices, vertexCount, faces, faceCount, out errorMessage))
            return null;

        if (!GradingInputValidator.ValidateLockCurves(lockCurves, out errorMessage))
            return null;

        if (!ValidatePads(pads, out errorMessage))
            return null;

        pads = OrderPadsForOwnership(pads);

        GradingResult? rebuilt = GradeWithConstraintFirstTopology(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            modelTolerance,
            terrainDetailSize,
            out failureOutputPolylines,
            out IReadOnlyList<GradingDiagnostic> constraintFirstFailureDiagnostics,
            out errorMessage);

        if (rebuilt != null &&
            ShouldPreferProtectedPadLocalRefinement(
                pads,
                modelTolerance,
                vertexCount,
                faceCount,
                rebuilt,
                out string protectedPadRefinementReason))
        {
            GradingResult? localRefinement = GradeWithRefinedZOnlyFallback(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pads,
                lockCurves,
                modelTolerance,
                protectedPadRefinementReason,
                constraintFirstFailureDiagnostics);
            if (localRefinement != null)
                return localRefinement;
        }

        if (rebuilt == null)
        {
            string? constraintFirstError = errorMessage;
            GradingResult? localRefinementFallback = GradeWithRefinedZOnlyFallback(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pads,
                lockCurves,
                modelTolerance,
                constraintFirstError ?? "unknown",
                constraintFirstFailureDiagnostics);
            if (localRefinementFallback != null)
            {
                errorMessage = null;
                return localRefinementFallback;
            }

            errorMessage = constraintFirstError;
        }

        if (rebuilt == null && string.IsNullOrWhiteSpace(errorMessage))
            errorMessage = "Grade Pad constraint-first rebuild failed.";

        if (rebuilt != null)
        {
            rebuilt = AddMultiPadSlopeDeviationFallbackDiagnosticIfNeeded(pads, modelTolerance, rebuilt);
            errorMessage = null;
        }

        return rebuilt;
    }

    private static bool ShouldPreferProtectedPadLocalRefinement(
        PadBoundary[] pads,
        double modelTolerance,
        int inputVertexCount,
        int inputFaceCount,
        GradingResult result,
        out string reason)
    {
        reason = string.Empty;
        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        bool hasProtectedPad = pads.Any(pad => pad.StitchApronDistance > tolerance * 4.0);
        if (!hasProtectedPad)
            return false;

        if (result.VertexCount <= inputVertexCount || result.FaceCount <= inputFaceCount)
        {
            reason = "constraint-first protected-pad topology did not add enough transition topology";
            return true;
        }

        if (pads.Length == 1 &&
            result.StructuredDiagnostics.Any(static diagnostic =>
                string.Equals(diagnostic.Code, "grade_pad.slope.deviation", StringComparison.Ordinal)))
        {
            reason = "constraint-first protected-pad topology produced excessive slope deviation";
            return true;
        }

        return false;
    }

    private static GradingResult AddMultiPadSlopeDeviationFallbackDiagnosticIfNeeded(
        PadBoundary[] pads,
        double modelTolerance,
        GradingResult result)
    {
        if (pads.Length <= 1)
            return result;

        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        if (!pads.Any(pad => pad.StitchApronDistance > tolerance * 4.0))
            return result;

        if (!result.StructuredDiagnostics.Any(static diagnostic =>
                string.Equals(diagnostic.Code, "grade_pad.slope.deviation", StringComparison.Ordinal)))
        {
            return result;
        }

        const string diagnosticCode = "grade_pad.fallback.multi_pad_slope_deviation_skipped";
        if (result.StructuredDiagnostics.Any(static diagnostic =>
                string.Equals(diagnostic.Code, diagnosticCode, StringComparison.Ordinal)))
        {
            return result;
        }

        return AddResultDiagnostic(
            result,
            GradingDiagnostic.Information(
                diagnosticCode,
                "Grade Pad local-refinement slope fallback skipped for coupled protected pads; multi-pad protected topology remains on the constraint-first result.",
                operation: "grade_pad"));
    }

    private static GradingResult AddResultDiagnostic(GradingResult result, GradingDiagnostic diagnostic)
    {
        string[] diagnostics = result.Diagnostics.Concat(new[] { diagnostic.Message }).ToArray();
        GradingDiagnostic[] structuredDiagnostics =
            result.StructuredDiagnostics.Concat(new[] { diagnostic }).ToArray();

        return new GradingResult(
            result.Vertices,
            result.VertexCount,
            result.Faces,
            result.FaceCount,
            result.CutVolume,
            result.FillVolume,
            result.DaylightVertices,
            result.DaylightVertexCount,
            result.OutputPolylines,
            diagnostics,
            result.PatchSummaries,
            structuredDiagnostics);
    }


    private sealed class PatchMeshResult
    {
        public required double[] Vertices { get; init; }
        public required int VertexCount { get; init; }
        public required int[] Faces { get; init; }
        public required int FaceCount { get; init; }
        public double[]? StitchLoopXy { get; init; }
        public int CornerConstraintCount { get; init; }
        public bool CornerConstraintsRejected { get; set; }
    }

    private readonly record struct OrderedPad(PadBoundary Pad, int OriginalIndex, double Priority);

    private static PadBoundary[] OrderPadsForOwnership(PadBoundary[] pads)
    {
        if (pads.Length <= 1)
            return pads;

        return pads
            .Select((pad, index) => new OrderedPad(pad, index, ComputePadOwnershipPriority(pad)))
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.OriginalIndex)
            .Select(entry => entry.Pad)
            .ToArray();
    }

    private static double ComputePadOwnershipPriority(PadBoundary pad)
    {
        double sumX = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < pad.VertexCount; i++)
        {
            sumX += pad.XyVertices[i * 2];
            sumY += pad.XyVertices[i * 2 + 1];
        }

        double centroidX = sumX / pad.VertexCount;
        double centroidY = sumY / pad.VertexCount;
        return pad.EvaluateZ(centroidX, centroidY);
    }


}
