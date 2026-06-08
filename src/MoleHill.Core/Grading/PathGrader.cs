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

}
