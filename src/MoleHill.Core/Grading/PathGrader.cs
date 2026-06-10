using MoleHill.Core.Engine;
using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh along path curves (roads, sidewalks, etc.).
/// Adds road edges (path offset by half-width) as constrained edges,
/// re-triangulates, then grades Z: inside road = path Z, outside = slope transition.
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
        hardConstraints ??= Array.Empty<SurfaceRemesher.ConstraintPolyline>();

        if (!GradingInputValidator.ValidateTerrainMesh(vertices, vertexCount, faces, faceCount, out errorMessage))
            return null;

        if (!GradingInputValidator.ValidateConstraintPolylines(hardConstraints, "Hard", out errorMessage))
            return null;

        if (!GradingInputValidator.ValidatePathDefinitions(paths, out errorMessage))
            return null;

        // Primary path: explicit corridor construction (ruled road surface + side batters welded into
        // terrain). Falls through to the legacy constraint-first path for interacting corridors, hard
        // constraints, or any case it cannot make watertight and manifold.
        GradingResult? explicitResult = GradeWithExplicitCorridor(
            vertices, vertexCount, faces, faceCount, paths, hardConstraints, modelTolerance, out string? explicitFailureReason);
        if (explicitResult != null)
        {
            errorMessage = null;
            return explicitResult;
        }

        // Record WHY the preferred explicit corridor path deferred so a fallback success does not
        // silently mask an explicit-path regression.
        GradingDiagnostic? explicitFallbackDiagnostic = string.IsNullOrWhiteSpace(explicitFailureReason)
            ? null
            : GradingDiagnostic.Information(
                "grade_path.explicit.fallback",
                $"Explicit corridor construction deferred to the constraint-first path: {explicitFailureReason}",
                operation: "grade_path");

        // Grade Path must own and rebuild topology. Do not silently fall back to Z-only grading.
        var result = GradeWithEdges(vertices, vertexCount, faces, faceCount, paths, hardConstraints, modelTolerance, out string? topologyError, out _);
        if (result != null)
        {
            errorMessage = null;
            return explicitFallbackDiagnostic != null
                ? WithExtraDiagnostic(result, explicitFallbackDiagnostic.Value)
                : result;
        }

        errorMessage = string.IsNullOrWhiteSpace(topologyError)
            ? "Grade Path topology rebuild failed."
            : $"Grade Path topology rebuild failed: {topologyError}";
        return null;
    }

    /// <summary>Returns a copy of <paramref name="result"/> with one extra diagnostic appended.</summary>
    private static GradingResult WithExtraDiagnostic(GradingResult result, GradingDiagnostic diagnostic)
    {
        string[] diagnostics = result.Diagnostics.Concat(new[] { diagnostic.Message }).ToArray();
        GradingDiagnostic[] structured = result.StructuredDiagnostics.Concat(new[] { diagnostic }).ToArray();

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
            structured);
    }
}
