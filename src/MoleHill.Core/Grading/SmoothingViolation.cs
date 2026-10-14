namespace MoleHill.Core.Grading;

/// <summary>
/// A smoothed point that broke one of smoothing's rules: it moved although it is held (the mesh border,
/// outside every Smooth region, or on a fully held breakline), or it left the range of heights it could
/// have mixed with — a new peak or pit. See <see cref="MeshSmoother.FindViolations"/>.
/// </summary>
public readonly record struct SmoothingViolation(
    int VertexIndex,
    double InputZ,
    double OutputZ,
    double LowZ,
    double HighZ,
    bool IsHeld)
{
    /// <summary>How far the point lies outside what it was allowed.</summary>
    public double Overshoot => IsHeld
        ? Math.Abs(OutputZ - InputZ)
        : OutputZ > HighZ ? OutputZ - HighZ : LowZ - OutputZ;
}
