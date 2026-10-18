namespace MoleHill.Core.Scan;

/// <summary>Colours carried alongside exact scan triangles: per-face RGB or a bitmap and per-corner UVs.</summary>
public sealed class ScanColourSource
{
    public ScanColourSource(int[] faceRgb)
    {
        ArgumentNullException.ThrowIfNull(faceRgb);
        FaceRgb = faceRgb;
    }

    public ScanColourSource(ScanTexture texture, float[] cornerUv)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(cornerUv);
        Texture = texture;
        CornerUv = cornerUv;
    }

    public int[]? FaceRgb { get; }
    public ScanTexture? Texture { get; }
    public float[]? CornerUv { get; }

    internal void Validate(int faceCount)
    {
        if (FaceRgb != null && FaceRgb.Length != faceCount || CornerUv != null && CornerUv.Length != (long)faceCount * 6)
            throw new ArgumentException("Colour arrays must describe the same triangles as the scan.");
    }

    internal int Sample(int face, double a, double b, double c)
    {
        if (FaceRgb != null)
            return FaceRgb[face];
        int i = face * 6;
        return Texture!.Sample(a * CornerUv![i] + b * CornerUv[i + 2] + c * CornerUv[i + 4],
            a * CornerUv[i + 1] + b * CornerUv[i + 3] + c * CornerUv[i + 5]);
    }
}
