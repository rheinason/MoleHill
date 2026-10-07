using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>Everything one Grade Pad run needs. Replaces the positional overloads of <see cref="PadGrader.Grade(PadGradeRequest)"/>.</summary>
public sealed class PadGradeRequest
{
    /// <summary>The terrain to grade.</summary>
    public required IndexedTriMesh Terrain { get; init; }

    /// <summary>Pads in input order; later pads win where they overlap.</summary>
    public required PadGrader.PadBoundary[] Pads { get; init; }

    public PadGrader.LockCurve[] LockCurves { get; init; } = Array.Empty<PadGrader.LockCurve>();

    /// <summary>Persistent breaklines (retaining walls) the grade must keep as exact edges.</summary>
    public IReadOnlyList<ConstraintPolyline> HardConstraints { get; init; } = Array.Empty<ConstraintPolyline>();

    public double ModelTolerance { get; init; } = GradingTolerances.DefaultModelTolerance;

    /// <summary>Typical terrain edge length, used to size batter density; 0 derives it from the mesh.</summary>
    public double TerrainDetailSize { get; init; }
}
