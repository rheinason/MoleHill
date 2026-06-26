namespace MoleHill.Core.Scattering;

/// <summary>
/// Inputs to <see cref="ScatterSampler.Sample"/>: the boundary region(s) to fill (each a flat closed
/// XY loop <c>[x0,y0,x1,y1,…]</c>, ≥3 vertices), the arrangement pattern, the density specification,
/// and a seed. Multiple boundaries are treated as one combined region (a point is kept if it lies in
/// any loop; the total area is the sum of the loops' areas). Results are deterministic for a given seed.
/// </summary>
public sealed class ScatterRequest
{
    /// <summary>Region mode: closed XY loops <c>[x0,y0,x1,y1,…]</c> (≥3 vertices) to fill.</summary>
    public IReadOnlyList<double[]> Boundaries { get; init; } = Array.Empty<double[]>();

    /// <summary>Curve mode: open XY polylines <c>[x0,y0,x1,y1,…]</c> (≥2 vertices) to distribute along.</summary>
    public IReadOnlyList<double[]> Paths { get; init; } = Array.Empty<double[]>();

    /// <summary>Whether to fill a region or distribute along curves.</summary>
    public ScatterSourceMode Source { get; init; } = ScatterSourceMode.Region;

    public ScatterPattern Pattern { get; init; } = ScatterPattern.Random;

    public ScatterDensityMode DensityMode { get; init; } = ScatterDensityMode.Count;

    /// <summary>Target instance count (<see cref="ScatterDensityMode.Count"/>).</summary>
    public double Count { get; init; }

    /// <summary>Instances per unit area (<see cref="ScatterDensityMode.PerArea"/>).</summary>
    public double PerAreaDensity { get; init; }

    /// <summary>Minimum centre-to-centre spacing (<see cref="ScatterDensityMode.Spacing"/>).</summary>
    public double Spacing { get; init; }

    /// <summary>Curve mode only: random XY offset radius (model units) applied to each on-curve point.</summary>
    public double JitterXy { get; init; }

    /// <summary>Curve + <see cref="ScatterDensityMode.EdgeToEdge"/>: gap (model units) left between item
    /// footprints. The per-item footprint extent is supplied by the caller's extent callback.</summary>
    public double EdgeGap { get; init; }

    public int Seed { get; init; }

    /// <summary>Hard upper bound on generated points; guards against runaway density. 0 = use the default.</summary>
    public int MaxSamples { get; init; }

    /// <summary>Cooperative cancellation polled during sampling so superseded builds abort promptly.</summary>
    public Func<bool>? ShouldCancel { get; init; }
}
