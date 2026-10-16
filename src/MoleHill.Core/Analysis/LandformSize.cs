namespace MoleHill.Core.Analysis;

/// <summary>
/// Resolves the landform size a curvature card measures at: the size typed on the card, or, when that is
/// zero (Auto), a size taken from the terrain's plan extent.
/// </summary>
/// <remarks>
/// <para>Auto is one tenth of the shorter side of the plan bounding box, so a landform must span a tenth of
/// the site to dominate the map: 20 m on a 200 m site, where a fixed 5 m read an 8 m, 60 m wide mound as
/// planar. The factor is a tenth because a hill needs several landform radii across it to read as one
/// shape, and the site needs several hills to be worth classifying.</para>
/// <para>A long thin site would shrink the size to its width, so the base never drops below a fiftieth of
/// the longer side. The result is rounded to a readable 1, 2, 2.5 or 5 times a power of ten (nearest in
/// log space, so at most about 40% from the raw value).</para>
/// </remarks>
public static class LandformSize
{
    /// <summary>Auto's share of the shorter plan side.</summary>
    public const double ExtentFraction = 0.1;

    /// <summary>Auto never works from a shorter side below this fraction of the longer one.</summary>
    public const double ThinSiteFraction = 1.0 / 50.0;

    private static readonly double[] Mantissas = { 1.0, 2.0, 2.5, 5.0, 10.0 };

    /// <summary>
    /// The size to measure at: <paramref name="requested"/> when positive, otherwise the Auto size for the
    /// extent. Zero when there is neither a typed size nor an extent.
    /// </summary>
    public static double Resolve(double requested, double extentX, double extentY)
    {
        if (double.IsFinite(requested) && requested > 0.0)
            return requested;

        return FromExtent(extentX, extentY);
    }

    /// <summary>The Auto size for a plan extent, or zero when the extent has no length.</summary>
    public static double FromExtent(double extentX, double extentY)
    {
        double x = double.IsFinite(extentX) ? Math.Abs(extentX) : 0.0;
        double y = double.IsFinite(extentY) ? Math.Abs(extentY) : 0.0;
        double longer = Math.Max(x, y);
        if (!(longer > 0.0))
            return 0.0;

        double basis = Math.Max(Math.Min(x, y), longer * ThinSiteFraction);
        return RoundReadable(basis * ExtentFraction);
    }

    /// <summary>Resolves against the plan bounding box of a mesh's vertices.</summary>
    public static double Resolve(double requested, Engine.IndexedTriMesh mesh)
    {
        if (double.IsFinite(requested) && requested > 0.0)
            return requested;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double[] v = mesh.Vertices;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            double x = v[i * 3], y = v[(i * 3) + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return maxX >= minX ? FromExtent(maxX - minX, maxY - minY) : 0.0;
    }

    /// <summary>Nearest 1, 2, 2.5, 5 or 10 times a power of ten, by ratio.</summary>
    public static double RoundReadable(double value)
    {
        if (!(double.IsFinite(value) && value > 0.0))
            return 0.0;

        double scale = Math.Pow(10.0, Math.Floor(Math.Log10(value)));
        double mantissa = value / scale;
        double best = Mantissas[0];
        double bestError = double.MaxValue;
        foreach (double candidate in Mantissas)
        {
            double error = Math.Abs(Math.Log(mantissa / candidate));
            if (error < bestError)
            {
                bestError = error;
                best = candidate;
            }
        }

        return best * scale;
    }
}
