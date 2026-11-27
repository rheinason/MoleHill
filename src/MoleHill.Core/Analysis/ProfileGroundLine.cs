// The ground under a long section, as station-elevation runs broken where the curve leaves the ground.
namespace MoleHill.Core.Analysis;

/// <summary>
/// The ground line of a long section: runs of (station, elevation) pairs, flat <c>[s0, z0, s1, z1, …]</c>,
/// in increasing station, one run per stretch where the curve passes over the ground.
/// </summary>
public sealed class ProfileGroundLine
{
    public ProfileGroundLine(IReadOnlyList<double[]> runs)
    {
        Runs = runs;
    }

    public static ProfileGroundLine Empty { get; } = new(Array.Empty<double[]>());

    public IReadOnlyList<double[]> Runs { get; }

    public bool IsEmpty => Runs.Count == 0;

    /// <summary>The ground's elevation at a station, or null where the curve is off the ground.</summary>
    public double? ElevationAt(double station)
    {
        foreach (double[] run in Runs)
        {
            int count = run.Length / 2;
            if (count == 0 || station < run[0] || station > run[(count - 1) * 2])
                continue;
            for (int i = 1; i < count; i++)
            {
                double s0 = run[(i - 1) * 2], s1 = run[i * 2];
                if (station > s1)
                    continue;
                double t = s1 > s0 ? (station - s0) / (s1 - s0) : 0.0;
                return run[(i - 1) * 2 + 1] + (run[i * 2 + 1] - run[(i - 1) * 2 + 1]) * t;
            }

            return run[(count - 1) * 2 + 1];
        }

        return null;
    }

    /// <summary>Lowest and highest ground elevation, or null when there is no ground.</summary>
    public (double Low, double High)? Extent()
    {
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        foreach (double[] run in Runs)
        {
            for (int i = 1; i < run.Length; i += 2)
            {
                low = Math.Min(low, run[i]);
                high = Math.Max(high, run[i]);
            }
        }

        return double.IsFinite(low) ? (low, high) : null;
    }
}
