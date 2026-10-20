using MoleHill.Core.Processing;

namespace MoleHill.Core.Scan;

/// <summary>
/// Where scan recovery measures what. Smooth soft ground is contoured at the terrain accuracy. Paving (carried
/// by its outline breaklines) and rough soft ground (bushes, hedges, bed edges: on the reference scan 62% of
/// spot heights sat on the roughest 19% of ground) are measured at a looser tolerance with spot heights only.
/// Contours are still clipped to the whole soft outline, so they run through rough patches unbroken.
/// </summary>
public sealed class ScanRecoveryRegion : ITerrainRecoveryRegion
{
    private readonly Func<double, double, bool> _contains;
    private readonly ITerrainRecoveryRegion _clip;

    private ScanRecoveryRegion(Func<double, double, bool> contains, ITerrainRecoveryRegion clip)
    {
        _contains = contains;
        _clip = clip;
    }

    /// <summary>Smooth soft ground.</summary>
    public static ScanRecoveryRegion Contoured(ScanSurfaceAreas.Result areas, bool[]? rough)
    {
        var soft = new ScanSoftRegion(areas);
        return new((x, y) => soft.Contains(x, y) && !Rough(areas.Ground, rough, x, y), soft);
    }

    /// <summary>Paving and rough soft ground.</summary>
    public static ScanRecoveryRegion Loose(ScanSurfaceAreas.Result areas, bool[]? rough)
    {
        var soft = new ScanSoftRegion(areas);
        var paved = new ScanSoftRegion(areas, ScanSurfaceKind.Paved);
        return new((x, y) => paved.Contains(x, y) || Rough(areas.Ground, rough, x, y) && soft.Contains(x, y), paved);
    }

    public bool Contains(double x, double y) => _contains(x, y);

    public IReadOnlyList<RegionInputClipper.InputPolyline> Clip(double[] points, bool closed) => _clip.Clip(points, closed);

    private static bool Rough(ScanGrid grid, bool[]? rough, double x, double y)
    {
        if (rough == null) return false;
        int column = (int)Math.Round((x - grid.OriginX) / grid.CellSize), row = (int)Math.Round((y - grid.OriginY) / grid.CellSize);
        return column >= 0 && row >= 0 && column < grid.Columns && row < grid.Rows && rough[row * grid.Columns + column];
    }

    /// <summary>
    /// Nodes within <paramref name="radius"/> of one whose height departs from its neighbourhood mean by more
    /// than <paramref name="height"/>: clutter and small raised growth rather than a ground form.
    /// </summary>
    public static bool[] RoughGround(ScanGrid grid, double radius, double height, CancellationToken token = default)
    {
        int columns = grid.Columns, rows = grid.Rows, reach = Math.Max(1, (int)Math.Round(radius / grid.CellSize));
        var known = grid.Values.Where(double.IsFinite).DefaultIfEmpty(0).Average();
        var z = grid.Values.Select(v => double.IsFinite(v) ? v : known).ToArray();
        var departure = new double[z.Length];
        Parallel.For(0, rows, new ParallelOptions { CancellationToken = token }, row =>
        {
            for (int column = 0; column < columns; column++)
            {
                double sum = 0; int count = 0;
                for (int r = Math.Max(0, row - reach); r <= Math.Min(rows - 1, row + reach); r++)
                    for (int c = Math.Max(0, column - reach); c <= Math.Min(columns - 1, column + reach); c++) { sum += z[r * columns + c]; count++; }
                departure[row * columns + column] = Math.Abs(z[row * columns + column] - sum / count);
            }
        });
        var rough = new bool[z.Length];
        Parallel.For(0, rows, new ParallelOptions { CancellationToken = token }, row =>
        {
            for (int column = 0; column < columns; column++)
            {
                for (int r = Math.Max(0, row - reach); r <= Math.Min(rows - 1, row + reach) && !rough[row * columns + column]; r++)
                    for (int c = Math.Max(0, column - reach); c <= Math.Min(columns - 1, column + reach); c++)
                        if (departure[r * columns + c] > height) { rough[row * columns + column] = true; break; }
            }
        });
        return rough;
    }
}
