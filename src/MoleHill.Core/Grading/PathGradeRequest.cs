using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Everything one Grade Path (or Grade Line, a path at width zero) run needs. Replaces the positional
/// overloads of <see cref="PathGrader.Grade(PathGradeRequest)"/>.
/// </summary>
public sealed class PathGradeRequest
{
    /// <summary>The terrain to grade.</summary>
    public required IndexedTriMesh Terrain { get; init; }

    public required PathGrader.PathDefinition[] Paths { get; init; }

    /// <summary>Persistent breaklines (retaining walls) the grade must not cross and must keep as exact edges.</summary>
    public IReadOnlyList<ConstraintPolyline> HardConstraints { get; init; } = Array.Empty<ConstraintPolyline>();

    public double ModelTolerance { get; init; } = GradingTolerances.DefaultModelTolerance;

    /// <summary>
    /// Try the split-keep tier before the explicit corridor. Final Rhino builds set it for large meshes with
    /// persistent hard constraints, where the explicit carve commonly defers anyway.
    /// </summary>
    public bool PreferSplitKeep { get; init; }
}
