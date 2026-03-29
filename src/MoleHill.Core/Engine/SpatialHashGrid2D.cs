namespace MoleHill.Core.Engine;

internal readonly record struct Bounds2D(double MinX, double MaxX, double MinY, double MaxY)
{
    public static Bounds2D FromPoint(double x, double y, double padding = 0.0)
    {
        return new Bounds2D(x - padding, x + padding, y - padding, y + padding);
    }

    public bool Intersects(in Bounds2D other)
    {
        return !(MaxX < other.MinX || MinX > other.MaxX || MaxY < other.MinY || MinY > other.MaxY);
    }
}

internal sealed class SpatialHashGrid2D
{
    public sealed class QueryScratch
    {
        private int[] _marks;
        private int _stamp;

        public QueryScratch(int itemCapacity = 0)
        {
            _marks = itemCapacity > 0 ? new int[itemCapacity] : Array.Empty<int>();
        }

        internal void EnsureCapacity(int itemCount)
        {
            if (_marks.Length < itemCount)
                _marks = new int[itemCount];
        }

        internal bool TryVisit(int itemIndex)
        {
            if (_marks.Length == 0)
                return true;

            if (_stamp == int.MaxValue)
            {
                Array.Clear(_marks, 0, _marks.Length);
                _stamp = 1;
            }
            else if (_stamp == 0)
            {
                _stamp = 1;
            }

            if (_marks[itemIndex] == _stamp)
                return false;

            _marks[itemIndex] = _stamp;
            return true;
        }

        internal void BeginQuery(int itemCount)
        {
            EnsureCapacity(itemCount);
            if (_stamp == int.MaxValue)
            {
                Array.Clear(_marks, 0, _marks.Length);
                _stamp = 1;
                return;
            }

            _stamp++;
            if (_stamp == 0)
                _stamp = 1;
        }
    }

    private readonly Dictionary<long, List<int>> _cells;
    private readonly double _minX;
    private readonly double _maxX;
    private readonly double _minY;
    private readonly double _maxY;
    private readonly double _invCellSize;

    public int ItemCount { get; }

    private SpatialHashGrid2D(
        Dictionary<long, List<int>> cells,
        double minX,
        double maxX,
        double minY,
        double maxY,
        double invCellSize,
        int itemCount)
    {
        _cells = cells;
        _minX = minX;
        _maxX = maxX;
        _minY = minY;
        _maxY = maxY;
        _invCellSize = invCellSize;
        ItemCount = itemCount;
    }

    public static SpatialHashGrid2D Build(Bounds2D[] bounds, bool[]? valid = null)
    {
        if (bounds.Length == 0)
        {
            return new SpatialHashGrid2D(
                new Dictionary<long, List<int>>(),
                0,
                0,
                0,
                0,
                1,
                0);
        }

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        int validCount = 0;

        for (int i = 0; i < bounds.Length; i++)
        {
            if (valid != null && !valid[i])
                continue;

            Bounds2D current = bounds[i];
            if (!double.IsFinite(current.MinX) ||
                !double.IsFinite(current.MaxX) ||
                !double.IsFinite(current.MinY) ||
                !double.IsFinite(current.MaxY) ||
                current.MinX > current.MaxX ||
                current.MinY > current.MaxY)
            {
                continue;
            }

            validCount++;
            if (current.MinX < minX) minX = current.MinX;
            if (current.MaxX > maxX) maxX = current.MaxX;
            if (current.MinY < minY) minY = current.MinY;
            if (current.MaxY > maxY) maxY = current.MaxY;
        }

        if (validCount == 0)
        {
            return new SpatialHashGrid2D(
                new Dictionary<long, List<int>>(),
                0,
                0,
                0,
                0,
                1,
                bounds.Length);
        }

        double span = Math.Max(maxX - minX, maxY - minY);
        double cellSize = span > 0
            ? Math.Max(span / Math.Max(8.0, Math.Sqrt(validCount)), 1e-9)
            : 1.0;
        double invCellSize = 1.0 / cellSize;
        var cells = new Dictionary<long, List<int>>(Math.Max(16, validCount));

        for (int i = 0; i < bounds.Length; i++)
        {
            if (valid != null && !valid[i])
                continue;

            Bounds2D current = bounds[i];
            if (!double.IsFinite(current.MinX) ||
                !double.IsFinite(current.MaxX) ||
                !double.IsFinite(current.MinY) ||
                !double.IsFinite(current.MaxY) ||
                current.MinX > current.MaxX ||
                current.MinY > current.MaxY)
            {
                continue;
            }

            long cminX = ToCell(current.MinX, minX, invCellSize);
            long cmaxX = ToCell(current.MaxX, minX, invCellSize);
            long cminY = ToCell(current.MinY, minY, invCellSize);
            long cmaxY = ToCell(current.MaxY, minY, invCellSize);

            for (long cx = cminX; cx <= cmaxX; cx++)
            {
                for (long cy = cminY; cy <= cmaxY; cy++)
                {
                    long key = PackKey(cx, cy);
                    if (!cells.TryGetValue(key, out var list))
                    {
                        list = new List<int>(4);
                        cells[key] = list;
                    }

                    list.Add(i);
                }
            }
        }

        return new SpatialHashGrid2D(cells, minX, maxX, minY, maxY, invCellSize, bounds.Length);
    }

    public void GatherCandidates(in Bounds2D queryBounds, List<int> candidates, QueryScratch? scratch = null)
    {
        candidates.Clear();
        if (_cells.Count == 0 || !queryBounds.Intersects(new Bounds2D(_minX, _maxX, _minY, _maxY)))
            return;

        long minCellX = ToCell(queryBounds.MinX, _minX, _invCellSize);
        long maxCellX = ToCell(queryBounds.MaxX, _minX, _invCellSize);
        long minCellY = ToCell(queryBounds.MinY, _minY, _invCellSize);
        long maxCellY = ToCell(queryBounds.MaxY, _minY, _invCellSize);

        if (scratch != null)
            scratch.BeginQuery(ItemCount);

        HashSet<int>? seen = scratch == null ? new HashSet<int>() : null;

        for (long cy = minCellY; cy <= maxCellY; cy++)
        {
            for (long cx = minCellX; cx <= maxCellX; cx++)
            {
                if (!_cells.TryGetValue(PackKey(cx, cy), out var list))
                    continue;

                foreach (int itemIndex in list)
                {
                    if (scratch != null)
                    {
                        if (!scratch.TryVisit(itemIndex))
                            continue;
                    }
                    else if (!(seen?.Add(itemIndex) ?? true))
                    {
                        continue;
                    }

                    candidates.Add(itemIndex);
                }
            }
        }
    }

    private static long ToCell(double value, double minValue, double invCellSize)
    {
        return (long)Math.Floor((value - minValue) * invCellSize);
    }

    private static long PackKey(long cellX, long cellY)
    {
        return (cellX * 0x100000001L) ^ (cellY * 0x27d4eb2dL);
    }
}
