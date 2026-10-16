namespace MoleHill.Core.Displacement;

/// <summary>
/// Procedural displacement: fractal gradient noise laid out in world XY. <see cref="Height"/> is the full
/// range the offset can span, lowest to highest, so every offset lies within ±Height/2 of the incoming
/// ground. Typical relief is about half that range; the extremes are rare.
/// </summary>
public sealed class NoiseDisplacementSource : IDisplacementSource
{
    public NoiseDisplacementSource(
        double size,
        double height,
        int octaves,
        double roughness,
        int seed,
        DisplacementPlacement placement)
    {
        if (!(size > 0.0) || !double.IsFinite(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "The noise size must be positive.");

        Size = size;
        Height = double.IsFinite(height) ? height : 0.0;
        Octaves = Math.Clamp(octaves, 1, GradientNoise.MaxOctaves);
        Roughness = Math.Clamp(roughness, 0.0, 1.0);
        Seed = seed;
        Placement = placement;
    }

    /// <summary>The plan width of the coarsest octave's cells: the size of one bump.</summary>
    public double Size { get; }

    /// <summary>Peak-to-peak range of the offset.</summary>
    public double Height { get; }

    public int Octaves { get; }

    /// <summary>Weight of each octave relative to the one before, 0 to 1.</summary>
    public double Roughness { get; }

    public int Seed { get; }

    public DisplacementPlacement Placement { get; }

    /// <summary>The size of the finest octave's cells.</summary>
    public double FinestSize => Size / Math.Pow(2.0, Octaves - 1);

    public double DetailSize => Size;

    public double Offset(double x, double y)
    {
        (double u, double v) = Placement.ToLocal(x, y);
        return 0.5 * Height * GradientNoise.Fractal(u / Size, v / Size, Octaves, Roughness, Seed);
    }
}
