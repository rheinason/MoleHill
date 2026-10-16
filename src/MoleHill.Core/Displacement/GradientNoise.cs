namespace MoleHill.Core.Displacement;

/// <summary>
/// Deterministic 2D gradient (Perlin) noise and its fractal sum. The lattice hash is integer arithmetic
/// and the gradients are a fixed table, so a value depends on nothing but the inputs: no
/// <see cref="System.Random"/>, no transcendental function, identical on every machine and runtime. That
/// is what lets a saved terrain rebuild to the same ground anywhere.
/// </summary>
public static class GradientNoise
{
    private const double Diagonal = 0.70710678118654752440;

    /// <summary>√2: a single octave of 2D Perlin noise with unit gradients stays within ±√2/2.</summary>
    private const double Normalize = 1.41421356237309504880;

    private static readonly double[] GradientX = { 1.0, -1.0, 0.0, 0.0, Diagonal, -Diagonal, Diagonal, -Diagonal };
    private static readonly double[] GradientY = { 0.0, 0.0, 1.0, -1.0, Diagonal, Diagonal, -Diagonal, -Diagonal };

    /// <summary>One octave of gradient noise at (x, y) in lattice units, within [-1, 1]. Zero on every lattice point.</summary>
    public static double Sample(double x, double y, int seed)
    {
        double fx = Math.Floor(x);
        double fy = Math.Floor(y);
        long ix = (long)fx;
        long iy = (long)fy;
        double tx = x - fx;
        double ty = y - fy;

        double n00 = Corner(ix, iy, tx, ty, seed);
        double n10 = Corner(ix + 1, iy, tx - 1.0, ty, seed);
        double n01 = Corner(ix, iy + 1, tx, ty - 1.0, seed);
        double n11 = Corner(ix + 1, iy + 1, tx - 1.0, ty - 1.0, seed);

        double u = Fade(tx);
        double v = Fade(ty);
        double bottom = n00 + (u * (n10 - n00));
        double top = n01 + (u * (n11 - n01));
        return Math.Clamp((bottom + (v * (top - bottom))) * Normalize, -1.0, 1.0);
    }

    /// <summary>
    /// Fractal sum of <paramref name="octaves"/> octaves at (x, y) in units of the first octave's lattice:
    /// each octave has twice the frequency of the one before and <paramref name="roughness"/> times its
    /// weight. Divided by the total weight, so the result stays within [-1, 1].
    /// </summary>
    public static double Fractal(double x, double y, int octaves, double roughness, int seed)
    {
        octaves = Math.Clamp(octaves, 1, MaxOctaves);
        roughness = Math.Clamp(roughness, 0.0, 1.0);
        double sum = 0.0;
        double total = 0.0;
        double weight = 1.0;
        double frequency = 1.0;
        for (int k = 0; k < octaves; k++)
        {
            // A fixed offset per octave keeps their lattices from sharing zeros at the origin, and a
            // different seed per octave keeps them from repeating one another.
            double ox = k * 17.31;
            double oy = k * -9.77;
            sum += weight * Sample((x * frequency) + ox, (y * frequency) + oy, unchecked(seed + (k * 1013)));
            total += weight;
            weight *= roughness;
            frequency *= 2.0;
        }

        return total > 0.0 ? Math.Clamp(sum / total, -1.0, 1.0) : 0.0;
    }

    /// <summary>The most octaves <see cref="Fractal"/> sums.</summary>
    public const int MaxOctaves = 8;

    private static double Corner(long ix, long iy, double dx, double dy, int seed)
    {
        int g = Hash(ix, iy, seed);
        return (GradientX[g] * dx) + (GradientY[g] * dy);
    }

    /// <summary>6t⁵ − 15t⁴ + 10t³: continuous slope and curvature across lattice cells.</summary>
    private static double Fade(double t) => t * t * t * ((t * ((t * 6.0) - 15.0)) + 10.0);

    /// <summary>A lattice point's gradient index: the MurmurHash3 64-bit finalizer over the mixed coordinates.</summary>
    private static int Hash(long ix, long iy, int seed)
    {
        unchecked
        {
            ulong k = ((ulong)ix * 0x9E3779B97F4A7C15UL) ^
                      ((ulong)iy * 0xC2B2AE3D27D4EB4FUL) ^
                      ((ulong)(uint)seed * 0x165667B19E3779F9UL);
            k ^= k >> 33;
            k *= 0xFF51AFD7ED558CCDUL;
            k ^= k >> 33;
            k *= 0xC4CEB9FE1A85EC53UL;
            k ^= k >> 33;
            return (int)(k & 7UL);
        }
    }
}
