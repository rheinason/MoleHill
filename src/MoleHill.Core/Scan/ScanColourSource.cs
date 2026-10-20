using MoleHill.Core.Engine;

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

    /// <summary>
    /// The faces the reconstruction filled rather than measured, by texture density: a gap fill is textured from
    /// a stretched texel or two, at about a thousandth of the scan's usual density (0.06 against 90 texels per m²
    /// on one reference, 0.02 against 420 on the other, both with no colour variation across the face). A face
    /// under <paramref name="share"/> of the median density is a fill. Per-face colours carry no density: null.
    /// </summary>
    internal bool[]? FilledFaces(IndexedTriMesh mesh, double share)
    {
        if (Texture == null || CornerUv == null || mesh.FaceCount == 0)
            return null;
        var density = new double[mesh.FaceCount];
        double[] v = mesh.Vertices;
        for (int face = 0; face < mesh.FaceCount; face++)
        {
            int a = mesh.Faces[face * 3] * 3, b = mesh.Faces[face * 3 + 1] * 3, c = mesh.Faces[face * 3 + 2] * 3;
            double ux = v[b] - v[a], uy = v[b + 1] - v[a + 1], uz = v[b + 2] - v[a + 2];
            double wx = v[c] - v[a], wy = v[c + 1] - v[a + 1], wz = v[c + 2] - v[a + 2];
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            double area = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            int i = face * 6;
            double texels = Math.Abs((CornerUv[i + 2] - CornerUv[i]) * (CornerUv[i + 5] - CornerUv[i + 1]) -
                (CornerUv[i + 3] - CornerUv[i + 1]) * (CornerUv[i + 4] - CornerUv[i])) * Texture.Width * Texture.Height;
            density[face] = area > 0 ? texels / area : double.NaN;
        }
        double[] finite = density.Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
            return null;
        Array.Sort(finite);
        double threshold = finite[finite.Length / 2] * share;
        return density.Select(d => d < threshold).ToArray();
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
