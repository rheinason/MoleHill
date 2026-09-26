using MoleHill.Core.Engine;
namespace MoleHill.Core.Grading;

internal class SpatialVertexHash
{
    private readonly double _cellSize;
    private readonly double _invCell;
    private readonly Dictionary<long, List<int>> _grid = new(IndexedMeshTools.CellKeyComparer.Instance);

    public SpatialVertexHash(double tolerance)
    {
        _cellSize = Math.Max(tolerance * 2, 1e-10);
        _invCell = 1.0 / _cellSize;
    }

    public void Insert(int index, double x, double y)
    {
        long key = CellKey(x, y);
        if (!_grid.TryGetValue(key, out var list))
        {
            list = new List<int>();
            _grid[key] = list;
        }

        list.Add(index);
    }

    public int FindNearest(List<double> xyList, double px, double py, double tolerance)
    {
        double tolSq = tolerance * tolerance;
        long cx = (long)Math.Floor(px * _invCell);
        long cy = (long)Math.Floor(py * _invCell);

        for (long dx = -1; dx <= 1; dx++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                long key = PackKey(cx + dx, cy + dy);
                if (!_grid.TryGetValue(key, out var indices))
                    continue;

                foreach (int idx in indices)
                {
                    double ex = xyList[idx * 2];
                    double ey = xyList[idx * 2 + 1];
                    double d2 = (px - ex) * (px - ex) + (py - ey) * (py - ey);
                    if (d2 < tolSq)
                        return idx;
                }
            }
        }

        return -1;
    }

    private long CellKey(double x, double y) =>
        PackKey((long)Math.Floor(x * _invCell), (long)Math.Floor(y * _invCell));

    private static long PackKey(long cx, long cy) =>
        (cx * 0x100000001L) ^ (cy * 0x27d4eb2dL);
}
