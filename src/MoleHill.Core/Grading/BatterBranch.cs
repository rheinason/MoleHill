namespace MoleHill.Core.Grading;

/// <summary>
/// Which way a batter runs from a design edge: up into cut (+1, the ground above the design) or down into
/// fill (−1). The one rule every tier uses: <b>the side the ground starts on</b> — the first ground height,
/// in order of distance from the edge, that differs from the design by more than the tolerance.
///
/// <para>A batter can only daylight on that side. Ground 0.13 m below a road edge that rises 0.5 m above
/// the road further out is a fill section: a fill batter meets the ground almost at once, while a cut batter
/// starting above the ground climbs away from it for ever. The section tier once took the side of the
/// largest difference instead, and on exactly that verge-then-terrace section it climbed 45° into a kerb
/// and stood 5 m proud of the ground. The explicit and Pad tiers always looked at the edge; this keeps the
/// three from drifting apart again.</para>
/// </summary>
internal static class BatterBranch
{
    /// <summary>
    /// +1 (cut), −1 (fill), or 0 when every difference is within <paramref name="atGradeTolerance"/> of the
    /// design. <paramref name="groundMinusDesign"/> is ordered from the edge outwards.
    /// </summary>
    internal static double FromGroundDifferences(ReadOnlySpan<double> groundMinusDesign, double atGradeTolerance)
    {
        foreach (double difference in groundMinusDesign)
        {
            if (difference > atGradeTolerance)
                return 1.0;
            if (difference < -atGradeTolerance)
                return -1.0;
        }

        return 0.0;
    }

    /// <summary>The branch from the ground at the edge alone; 0 when it is within tolerance of the design.</summary>
    internal static double AtEdge(double groundZ, double designZ, double atGradeTolerance) =>
        FromGroundDifferences(stackalloc double[] { groundZ - designZ }, atGradeTolerance);
}
