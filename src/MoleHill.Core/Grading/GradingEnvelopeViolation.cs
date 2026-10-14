namespace MoleHill.Core.Grading;

/// <summary>
/// A graded point outside its envelope: the band between the original ground and the design height at
/// the nearest path centreline or pad boundary. A batter runs from the design edge to the ground, so every
/// point it moves stays between the two; one above both or below both is a grading fault — a batter that
/// never met the ground, or a spike. See <see cref="PathGrader.FindEnvelopeViolations"/>.
/// </summary>
public readonly record struct GradingEnvelopeViolation(
    int VertexIndex,
    double X,
    double Y,
    double Z,
    double GroundZ,
    double LowZ,
    double HighZ)
{
    /// <summary>How far the point lies outside the band.</summary>
    public double Overshoot => Z > HighZ ? Z - HighZ : LowZ - Z;
}
