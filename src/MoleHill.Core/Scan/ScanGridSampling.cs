namespace MoleHill.Core.Scan;

/// <summary>Finite bilinear samples on scan lattices; missing neighbours contribute no weight.</summary>
internal static class ScanGridSampling
{
    public static bool TryHeight(ScanGrid grid, double x, double y, out double height)
    {
        height = double.NaN;
        if (grid.Columns == 0 || grid.Rows == 0)
            return false;
        double fx = (x - grid.OriginX) / grid.CellSize, fy = (y - grid.OriginY) / grid.CellSize;
        if (!double.IsFinite(fx + fy) || fx < -1e-8 || fy < -1e-8 || fx > grid.Columns - 1 + 1e-8 || fy > grid.Rows - 1 + 1e-8)
            return false;
        fx = Math.Clamp(fx, 0, grid.Columns - 1);
        fy = Math.Clamp(fy, 0, grid.Rows - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, grid.Columns - 1), y1 = Math.Min(y0 + 1, grid.Rows - 1);
        double tx = fx - x0, ty = fy - y0, sum = 0, weight = 0;
        Add(x0, y0, (1 - tx) * (1 - ty));
        Add(x1, y0, tx * (1 - ty));
        Add(x0, y1, (1 - tx) * ty);
        Add(x1, y1, tx * ty);
        if (weight <= 0)
            return false;
        height = sum / weight;
        return true;
        void Add(int column, int row, double w)
        {
            double z = grid[column, row];
            if (w <= 0 || !double.IsFinite(z))
                return;
            sum += w * z;
            weight += w;
        }
    }
}
