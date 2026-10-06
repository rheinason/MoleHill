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
        return Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            out errorMessage,
            out failureOutputPolylines,
            out _,
            modelTolerance,
            terrainDetailSize);
    }

    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        out string? errorMessage,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        out IReadOnlyList<GradingDiagnostic> failureStructuredDiagnostics,
        double modelTolerance = GradingTolerances.DefaultModelTolerance,
        double terrainDetailSize = 0.0,
        IReadOnlyList<ConstraintPolyline>? hardConstraints = null)
    {
        errorMessage = null;
        failureOutputPolylines = Array.Empty<OutputPolyline>();
        failureStructuredDiagnostics = Array.Empty<GradingDiagnostic>();
        lockCurves ??= Array.Empty<LockCurve>();

        if (!GradingInputValidator.ValidateTerrainMesh(vertices, vertexCount, faces, faceCount, out errorMessage))
        {
            failureStructuredDiagnostics = BuildFailureDiagnostic(
                "grade_pad.input.invalid_terrain",
                errorMessage,
                GradingDiagnosticSeverity.Warning);
            return null;
        }

        if (!GradingInputValidator.ValidateLockCurves(lockCurves, out errorMessage))
        {
            failureStructuredDiagnostics = BuildFailureDiagnostic(
                "grade_pad.input.invalid_lock_curve",
                errorMessage,
                GradingDiagnosticSeverity.Warning);
            return null;
        }

        // Retaining walls and other persistent breaklines must survive a pad grade. The conforming
        // tiers can fall back to a whole-terrain CDT re-conform, which flips away the near-vertical
        // wall-face edges unless they are re-inserted as exact constraints - a pad on the far side of
        // the terrain then orphans a wall's foot vertices. Path grading has always threaded these; pad
        // grading did not, which is what the wall-foot regression was.
        hardConstraints ??= Array.Empty<ConstraintPolyline>();
        if (!GradingInputValidator.ValidateConstraintPolylines(hardConstraints, "Hard", out errorMessage))
        {
            failureStructuredDiagnostics = BuildFailureDiagnostic(
                "grade_pad.input.invalid_hard_constraint",
                errorMessage,
                GradingDiagnosticSeverity.Warning);
            return null;
        }

        if (!ValidatePads(pads, out errorMessage))
        {
            failureStructuredDiagnostics = BuildFailureDiagnostic(
                "grade_pad.input.invalid_pad",
                errorMessage,
                GradingDiagnosticSeverity.Warning);
            return null;
        }

        pads = OrderPadsForOwnership(pads);

        // Primary path: explicit batter construction. Deterministic geometry, slope exact by
        // construction, terrain outside the daylight loops left intact. Defers only when it cannot
        // produce a watertight, manifold result.
        GradingResult? explicitResult = GradeWithExplicitBatter(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            hardConstraints,
            modelTolerance,
            terrainDetailSize,
            out string? explicitFailureReason);
        if (explicitResult != null)
        {
            errorMessage = null;
            return explicitResult;
        }

        // The explicit engine is the preferred path; record WHY it deferred so a fallback success
        // does not silently mask an explicit-path regression. Attached to the returned result below.
        GradingDiagnostic? explicitFallbackDiagnostic = string.IsNullOrWhiteSpace(explicitFailureReason)
            ? null
            : GradingDiagnostic.Information(
                "grade_pad.explicit.fallback",
                $"Explicit batter construction deferred to the split-keep path: {explicitFailureReason}",
                operation: "grade_pad");

        // Middle tier: conform the terrain to the daylight loops and keep the whole mesh. Watertight
        // by construction (no carve/fill/weld seam); batter slopes follow conformed terrain density so
        // they can be slightly faceted, but there are no holes or spikes. Defers cleanly to
        // region-remesh when the area splitter cannot conform the scene manifold.
        GradingResult? splitKeep = GradeWithSplitKeep(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            hardConstraints,
            modelTolerance,
            terrainDetailSize,
            out string? splitKeepFailureReason);
        if (splitKeep != null)
        {
            if (explicitFallbackDiagnostic != null)
                splitKeep = AddResultDiagnostic(splitKeep, explicitFallbackDiagnostic.Value);
            errorMessage = null;
            return splitKeep;
        }

        GradingDiagnostic? splitKeepFallbackDiagnostic = string.IsNullOrWhiteSpace(splitKeepFailureReason)
            ? null
            : GradingDiagnostic.Information(
                "grade_pad.split_keep.fallback",
                $"Terrain conform (split-keep) deferred to the region-remesh path: {splitKeepFailureReason}",
                operation: "grade_pad");

        // Robust fallback: replace the affected region with a clean dense remesh graded by distance
        // field. Watertight by construction (rim is original terrain vertices, interior is one fresh
        // triangulation), so it catches dense/degenerate scenes the conforming tiers defer — a
        // spike-free, hole-free result.
        GradingResult? regionRemesh = GradeWithRegionRemesh(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            modelTolerance,
            terrainDetailSize,
            out string? regionRemeshFailureReason);
        if (regionRemesh != null)
        {
            if (explicitFallbackDiagnostic != null)
                regionRemesh = AddResultDiagnostic(regionRemesh, explicitFallbackDiagnostic.Value);
            if (splitKeepFallbackDiagnostic != null)
                regionRemesh = AddResultDiagnostic(regionRemesh, splitKeepFallbackDiagnostic.Value);
            errorMessage = null;
            return regionRemesh;
        }

        // All construction tiers (explicit batter, split-keep conform, region remesh) deferred. The
        // The removed whole-mesh rebuild used to catch this, but it produced spikes on exactly the
        // degenerate scenes that reach here. Fail cleanly with
        // the recorded reason rather than emit a non-watertight result.
        errorMessage = string.IsNullOrWhiteSpace(regionRemeshFailureReason)
            ? "Grade Pad could not produce a watertight result for this scene."
            : $"Grade Pad could not produce a watertight result: {regionRemeshFailureReason}";
        failureStructuredDiagnostics = BuildFailureDiagnostic(
            "grade_pad.all_tiers_deferred",
            errorMessage,
            GradingDiagnosticSeverity.Warning);
        return null;
    }

    private static IReadOnlyList<GradingDiagnostic> BuildFailureDiagnostic(
        string code,
        string? message,
        GradingDiagnosticSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Array.Empty<GradingDiagnostic>();

        return new[]
        {
            new GradingDiagnostic(
                severity,
                code,
                message,
                Operation: "grade_pad")
        };
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
