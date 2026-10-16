namespace MoleHill.Core.Displacement;

/// <summary>What an image displacement does beyond the edges of its one tile.</summary>
public enum DisplacementWrap
{
    /// <summary>The image repeats edge to edge in both directions.</summary>
    Repeat,

    /// <summary>One image; beyond it the edge pixels carry on outward.</summary>
    Clamp,

    /// <summary>One image; beyond it nothing moves (the neutral value).</summary>
    Once
}

/// <summary>
/// A greyscale heightmap as values from 0 (black) to 1 (white), decoded by the host. Row 0 is the top of
/// the image, as image files store it. Immutable once built, so one decode can serve every build.
/// </summary>
public sealed class HeightmapImage
{
    private readonly float[] _values;

    public HeightmapImage(int width, int height, float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "A heightmap needs at least one pixel.");
        if ((long)width * height != values.Length)
            throw new ArgumentException($"{width} x {height} pixels need {(long)width * height:N0} values; got {values.Length:N0}.", nameof(values));

        Width = width;
        Height = height;
        _values = values;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The value of pixel (column, row), row 0 at the top.</summary>
    public double this[int column, int row] => _values[(row * Width) + column];

    /// <summary>
    /// Bilinear sample at continuous image coordinates (<paramref name="s"/>, <paramref name="t"/>), each 0
    /// to 1 across the image: s from the left edge, t from the <b>bottom</b>, so the image reads upright in
    /// plan. Pixel centres sit half a pixel in from the edges. <paramref name="wrap"/> decides where samples
    /// beyond a pixel centre at the edge come from; <see cref="DisplacementWrap.Once"/> clamps like
    /// <see cref="DisplacementWrap.Clamp"/> here, its outside being the caller's to decide.
    /// </summary>
    public double Sample(double s, double t, DisplacementWrap wrap)
    {
        double x = (s * Width) - 0.5;
        double y = ((1.0 - t) * Height) - 0.5;
        double fx = Math.Floor(x);
        double fy = Math.Floor(y);
        double ax = x - fx;
        double ay = y - fy;
        long x0 = (long)fx;
        long y0 = (long)fy;

        int c0 = Index(x0, Width, wrap);
        int c1 = Index(x0 + 1, Width, wrap);
        int r0 = Index(y0, Height, wrap);
        int r1 = Index(y0 + 1, Height, wrap);

        double top = this[c0, r0] + (ax * (this[c1, r0] - this[c0, r0]));
        double bottom = this[c0, r1] + (ax * (this[c1, r1] - this[c0, r1]));
        return top + (ay * (bottom - top));
    }

    private static int Index(long i, int size, DisplacementWrap wrap)
    {
        if (wrap == DisplacementWrap.Repeat)
        {
            long m = i % size;
            return (int)(m < 0 ? m + size : m);
        }

        return (int)Math.Clamp(i, 0L, size - 1L);
    }
}
