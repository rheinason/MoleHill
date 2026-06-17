namespace MoleHill.Core.Scattering;

/// <summary>
/// Inputs to <see cref="ScatterSampler.Sample"/>: the boundary region(s) to fill (each a flat closed
/// XY loop <c>[x0,y0,x1,y1,…]</c>, ≥3 vertices), the arrangement pattern, the density specification,
/// and a seed. Multiple boundaries are treated as one combined region (a point is kept if it lies in
/// any loop; the total area is the sum of the loops' areas). Results are deterministic for a given seed.
/// </summary>
public sealed class ScatterRequest
{
    public IReadOnlyList<double[]> Boundaries { get; init; } = Array.Empty<double[]>();

    public ScatterPattern Pattern { get; init; } = ScatterPattern.Random;

    public ScatterDensityMode DensityMode { get; init; } = ScatterDensityMode.Count;

    /// <summary>Target instance count (<see cref="ScatterDensityMode.Count"/>).</summary>
    public double Count { get; init; }

    /// <summary>Instances per unit area (<see cref="ScatterDensityMode.PerArea"/>).</summary>
    public double PerAreaDensity { get; init; }

    /// <summary>Minimum centre-to-centre spacing (<see cref="ScatterDensityMode.Spacing"/>).</summary>
    public double Spacing { get; init; }

    public int Seed { get; init; }
}
