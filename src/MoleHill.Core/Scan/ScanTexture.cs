namespace MoleHill.Core.Scan;

/// <summary>A host-free diffuse bitmap, RGB bytes in top-down rows, sampled using mesh UVs.</summary>
public sealed class ScanTexture
{
    public ScanTexture(int width, int height, byte[] rgb, bool repeatU = false, bool repeatV = false)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (width <= 0 || height <= 0 || (long)width * height * 3 != rgb.Length)
            throw new ArgumentException("Texture dimensions must match its RGB bytes.", nameof(rgb));
        Width = width;
        Height = height;
        Rgb = rgb;
        RepeatU = repeatU;
        RepeatV = repeatV;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgb { get; }
    public bool RepeatU { get; }
    public bool RepeatV { get; }

    /// <summary>Packed 0xRRGGBB, or -1 for non-finite UVs. UV V=1 is the bitmap's top row.</summary>
    public int Sample(double u, double v)
    {
        if (!double.IsFinite(u) || !double.IsFinite(v))
            return -1;
        u = RepeatU ? u - Math.Floor(u) : Math.Clamp(u, 0, 1);
        v = RepeatV ? v - Math.Floor(v) : Math.Clamp(v, 0, 1);
        int x = Math.Clamp((int)Math.Round(u * (Width - 1)), 0, Width - 1);
        int y = Math.Clamp((int)Math.Round((1 - v) * (Height - 1)), 0, Height - 1);
        int i = (y * Width + x) * 3;
        return Rgb[i] << 16 | Rgb[i + 1] << 8 | Rgb[i + 2];
    }
}
