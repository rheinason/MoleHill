using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Rhino.Services;

/// <summary>
/// "Grade Through Breaklines" on the grading cards. Breaklines stay hard by default: a card may not cross
/// a breakline or graded edge from a card above it. With the option on, the card grades across them, and
/// afterwards the parts of those lines it regraded are dropped from what later stages keep, so a Remesh or
/// wall below does not pull the old ground back into the new grade.
/// </summary>
internal sealed partial class TerrainBuildService
{
    /// <summary>The lines a grading card must respect: every upstream breakline, or none when it grades through them.</summary>
    private static IReadOnlyList<ConstraintPolyline> UpstreamBreaklines(TerrainBuildResult build, bool gradeThrough) =>
        gradeThrough ? Array.Empty<ConstraintPolyline>() : build.PersistentHardConstraints;

    /// <summary>
    /// Cuts the persisted breaklines and contours down to where they still lie on the graded terrain. Call it
    /// after a successful grade and before the card adds its own lines.
    /// </summary>
    private static void DropRegradedBreaklines(
        TerrainBuildResult build,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double modelTolerance,
        global::Rhino.UnitSystem unitSystem,
        string label)
    {
        if (build.PersistentHardConstraints.Count == 0 && build.PersistentElevationConstraints.Count == 0)
            return;

        var graded = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        double heightTolerance = Math.Max(modelTolerance * 10.0, 1e-9);
        List<ConstraintPolyline> hard = RegradedConstraintTrimmer.Trim(
            build.PersistentHardConstraints, graded, heightTolerance, out double hardRemoved, out int hardTrimmed);
        List<ConstraintPolyline> elevation = RegradedConstraintTrimmer.Trim(
            build.PersistentElevationConstraints, graded, heightTolerance, out double elevationRemoved, out int elevationTrimmed);
        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(hard);
        build.PersistentElevationConstraints.Clear();
        build.PersistentElevationConstraints.AddRange(elevation);
        if (hardTrimmed + elevationTrimmed > 0)
        {
            build.Diagnostics.Add(
                $"{label} graded through {hardTrimmed:N0} breakline(s) and {elevationTrimmed:N0} contour(s); " +
                $"{hardRemoved + elevationRemoved:N1} {ModelUnits.Abbreviation(unitSystem)} of them was regraded and dropped.");
        }
    }
}
