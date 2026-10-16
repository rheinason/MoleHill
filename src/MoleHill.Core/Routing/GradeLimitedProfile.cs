namespace MoleHill.Core.Routing;

/// <summary>
/// Fits a longitudinal profile to sampled ground so that no part of it is steeper than a limit, with
/// both ends held at given elevations. The profile is the ground wherever the ground already obeys the
/// limit, and departs from it only where it must.
/// </summary>
/// <remarks>
/// The construction is the L-infinity-optimal one. Over stations s with ground g and limit m:
/// <list type="bullet">
/// <item><c>U(s) = max_j (g_j - m |s - s_j|)</c> is the lowest profile at grade &lt;= m that stays on or
/// above the ground (fill everywhere);</item>
/// <item><c>L(s) = min_j (g_j + m |s - s_j|)</c> is the highest that stays on or below it (cut
/// everywhere);</item>
/// <item>their mean is within m too, and splits every departure evenly between cut and fill — the
/// smallest worst-case depth any grade-limited profile can achieve.</item>
/// </list>
/// Each envelope is two linear passes (a 1-D distance transform), so a fit is O(n). The held ends are
/// imposed by clamping into the cone both ends allow, which is a max/min of grade-m lines and so keeps
/// the profile within the limit. Any subset of the fitted points, joined by straight chords, is still
/// within the limit — a chord's grade is an average of the grades it spans — which is what lets the
/// caller simplify the result without re-checking it.
/// </remarks>
public static class GradeLimitedProfile
{
    /// <summary>
    /// Fits the profile. Returns false, with the grade the ends alone would need, when the held
    /// elevations are further apart than the limit allows over the given length.
    /// </summary>
    /// <param name="stations">Ascending plan stations.</param>
    /// <param name="ground">Ground elevation at each station.</param>
    /// <param name="maxGrade">The limit as rise over run (0.08 is 8%).</param>
    /// <param name="startZ">Elevation held at the first station.</param>
    /// <param name="endZ">Elevation held at the last station.</param>
    /// <param name="profile">The fitted elevation at each station.</param>
    /// <param name="requiredGrade">The grade between the held ends, |endZ - startZ| / length.</param>
    public static bool TryFit(
        IReadOnlyList<double> stations,
        IReadOnlyList<double> ground,
        double maxGrade,
        double startZ,
        double endZ,
        out double[] profile,
        out double requiredGrade)
    {
        int n = stations.Count;
        if (n != ground.Count)
            throw new ArgumentException("Stations and ground must have the same length.", nameof(ground));
        if (!(maxGrade >= 0.0))
            throw new ArgumentOutOfRangeException(nameof(maxGrade));

        profile = new double[n];
        requiredGrade = 0.0;
        if (n == 0)
            return true;
        if (n == 1)
        {
            profile[0] = startZ;
            return true;
        }

        double first = stations[0];
        double last = stations[n - 1];
        double length = last - first;
        requiredGrade = length > 0.0 ? Math.Abs(endZ - startZ) / length : (endZ == startZ ? 0.0 : double.PositiveInfinity);
        // A relative slack, so ends that meet the limit exactly — the common case on a fitted leg — pass.
        if (requiredGrade > maxGrade * (1.0 + 1e-9) + 1e-12)
            return false;

        var upper = new double[n];
        var lower = new double[n];
        upper[0] = ground[0];
        lower[0] = ground[0];
        for (int i = 1; i < n; i++)
        {
            double step = maxGrade * (stations[i] - stations[i - 1]);
            upper[i] = Math.Max(ground[i], upper[i - 1] - step);
            lower[i] = Math.Min(ground[i], lower[i - 1] + step);
        }

        for (int i = n - 2; i >= 0; i--)
        {
            double step = maxGrade * (stations[i + 1] - stations[i]);
            upper[i] = Math.Max(upper[i], upper[i + 1] - step);
            lower[i] = Math.Min(lower[i], lower[i + 1] + step);
        }

        for (int i = 0; i < n; i++)
        {
            double fromStart = stations[i] - first;
            double toEnd = last - stations[i];
            double low = Math.Max(startZ - (maxGrade * fromStart), endZ - (maxGrade * toEnd));
            double high = Math.Min(startZ + (maxGrade * fromStart), endZ + (maxGrade * toEnd));
            double mid = 0.5 * (upper[i] + lower[i]);
            profile[i] = high < low ? 0.5 * (low + high) : Math.Clamp(mid, low, high);
        }

        profile[0] = startZ;
        profile[n - 1] = endZ;
        return true;
    }
}
