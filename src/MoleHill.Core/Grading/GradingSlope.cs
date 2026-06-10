namespace MoleHill.Core.Grading;

/// <summary>
/// Shared helper for resolving a batter slope ratio from separate cut and fill slope angles.
/// </summary>
internal static class GradingSlope
{
    /// <summary>
    /// Returns the slope ratio (rise/run = tan(angle)) for the cut or fill side. The branch is chosen
    /// by <paramref name="branchSign"/> = sign(terrainZ - gradeZ): &gt; 0 means terrain sits above the
    /// graded surface (a cut) and uses <paramref name="cutAngleDeg"/>; &lt; 0 means terrain is below
    /// (a fill) and uses <paramref name="fillAngleDeg"/>. Zero is treated as cut (no batter there).
    /// </summary>
    public static double RatioFor(double cutAngleDeg, double fillAngleDeg, double branchSign)
    {
        double angle = branchSign < 0.0 ? fillAngleDeg : cutAngleDeg;
        return System.Math.Tan(System.Math.Clamp(angle, 0.1, 89.9) * System.Math.PI / 180.0);
    }
}
