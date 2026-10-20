namespace MoleHill.Core.Scan;

/// <summary>
/// Packed RGB on a lattice <see cref="Factor"/> times finer than the ground grid, sharing its origin, so
/// fine node <c>(c * Factor, r * Factor)</c> sits on ground node <c>(c, r)</c>. -1 means no colour.
/// A texture holds far more detail than one sample per height cell; classifying at this resolution keeps
/// area outlines off the height raster's staircase.
/// </summary>
internal sealed class ScanColourLattice
{
    public ScanColourLattice(ScanGrid ground, int factor, int[] rgb)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(rgb);
        if (factor < 1)
            throw new ArgumentOutOfRangeException(nameof(factor));
        Factor = factor;
        Columns = ground.Columns == 0 ? 0 : (ground.Columns - 1) * factor + 1;
        Rows = ground.Rows == 0 ? 0 : (ground.Rows - 1) * factor + 1;
        if (rgb.Length != (long)Columns * Rows)
            throw new ArgumentException("Colours must cover the fine lattice.", nameof(rgb));
        OriginX = ground.OriginX;
        OriginY = ground.OriginY;
        CellSize = ground.CellSize / factor;
        Rgb = rgb;
    }

    public int Factor { get; }
    public int Columns { get; }
    public int Rows { get; }
    public double OriginX { get; }
    public double OriginY { get; }
    public double CellSize { get; }
    public int[] Rgb { get; }

    /// <summary>The ground grid's own colours as a lattice of factor one.</summary>
    public static ScanColourLattice FromGround(ScanGrid ground, int[] colours) => new(ground, 1, colours);

    /// <summary>
    /// The finest factor (at most four) that keeps the lattice within the raster node limit. Per-face colours
    /// carry nothing finer than a triangle, so only a texture is resampled.
    /// </summary>
    public static int ChooseFactor(ScanGrid ground, ScanColourSource colours)
    {
        if (colours.Texture == null || ground.Columns < 2 || ground.Rows < 2)
            return 1;
        int factor = 4;
        while (factor > 1 && ((long)(ground.Columns - 1) * factor + 1) * ((long)(ground.Rows - 1) * factor + 1) > ScanRasterizer.MaximumNodeCount)
            factor--;
        return factor;
    }
}
