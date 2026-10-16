namespace MoleHill.Core.Displacement;

/// <summary>
/// Displacement from a greyscale heightmap laid on the ground as a tile of a stated real-world size. The
/// tile's lower-left corner sits at the placement origin and turns with its rotation. A pixel at the
/// <see cref="Neutral"/> value leaves the ground as it is; white rises (1 − Neutral) × Height and black
/// falls Neutral × Height, so Height is the offset between black and white.
/// </summary>
public sealed class ImageDisplacementSource : IDisplacementSource
{
    public ImageDisplacementSource(
        HeightmapImage image,
        double tileSizeX,
        double tileSizeY,
        double height,
        double neutral,
        DisplacementWrap wrap,
        DisplacementPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!(tileSizeX > 0.0) || !(tileSizeY > 0.0) || !double.IsFinite(tileSizeX) || !double.IsFinite(tileSizeY))
            throw new ArgumentOutOfRangeException(nameof(tileSizeX), "The tile size must be positive.");

        Image = image;
        TileSizeX = tileSizeX;
        TileSizeY = tileSizeY;
        Height = double.IsFinite(height) ? height : 0.0;
        Neutral = Math.Clamp(neutral, 0.0, 1.0);
        Wrap = wrap;
        Placement = placement;
    }

    public HeightmapImage Image { get; }

    /// <summary>Plan width of one image along its own X axis.</summary>
    public double TileSizeX { get; }

    /// <summary>Plan height of one image along its own Y axis (towards the image's top).</summary>
    public double TileSizeY { get; }

    public double Height { get; }

    /// <summary>The image value, 0 to 1, that means no offset.</summary>
    public double Neutral { get; }

    public DisplacementWrap Wrap { get; }

    public DisplacementPlacement Placement { get; }

    /// <summary>Plan size of one pixel: the finer of its two sides.</summary>
    public double PixelSize => Math.Min(TileSizeX / Image.Width, TileSizeY / Image.Height);

    public double DetailSize => PixelSize;

    public double Offset(double x, double y)
    {
        (double u, double v) = Placement.ToLocal(x, y);
        double s = u / TileSizeX;
        double t = v / TileSizeY;
        if (Wrap == DisplacementWrap.Once && (s < 0.0 || s > 1.0 || t < 0.0 || t > 1.0))
            return 0.0;

        return (Image.Sample(s, t, Wrap) - Neutral) * Height;
    }
}
