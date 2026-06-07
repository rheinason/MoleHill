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
    /// <summary>
    /// Build GradingResult with volumes and daylight line.
    /// </summary>
}
