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

    // Flat CSR cells: key -> slot, slot -> [_cellStart[slot], _cellStart[slot + 1]) in _cellItems.
    // The index is built once and never mutated, so a List per occupied cell only costs a small object
    // plus a backing array for each of them - millions on a large terrain.
    private readonly Dictionary<long, int> _cellSlots;
    private readonly int[] _cellStart;
    private readonly int[] _cellItems;
    private readonly double _minX;
    private readonly double _maxX;
    private readonly double _minY;
    private readonly double _maxY;
    private readonly double _invCellSize;

    public int ItemCount { get; }

    private SpatialHashGrid2D(
        Dictionary<long, int> cellSlots,
        int[] cellStart,
        int[] cellItems,
        double minX,
        double maxX,
        double minY,
        double maxY,
        double invCellSize,
        int itemCount)
    {
        _cellSlots = cellSlots;
        _cellStart = cellStart;
        _cellItems = cellItems;
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
                new Dictionary<long, int>(),
                new int[1],
                Array.Empty<int>(),
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
                new Dictionary<long, int>(),
                new int[1],
                Array.Empty<int>(),
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
        var cellSlots = new Dictionary<long, int>(Math.Max(16, validCount));

        // Pass 1: assign a slot to every occupied cell and count its memberships.
        var counts = new List<int>(Math.Max(16, validCount));
        for (int i = 0; i < bounds.Length; i++)
        {
            if (!TryGetCellRange(bounds, valid, i, minX, minY, invCellSize, out long cminX, out long cmaxX, out long cminY, out long cmaxY))
                continue;

            for (long cx = cminX; cx <= cmaxX; cx++)
            {
                for (long cy = cminY; cy <= cmaxY; cy++)
                {
                    long key = PackKey(cx, cy);
                    if (!cellSlots.TryGetValue(key, out int slot))
                    {
                        slot = counts.Count;
                        cellSlots[key] = slot;
                        counts.Add(0);
                    }

                    counts[slot]++;
                }
            }
        }

        var cellStart = new int[counts.Count + 1];
        int running = 0;
        for (int slot = 0; slot < counts.Count; slot++)
        {
            cellStart[slot] = running;
            running += counts[slot];
        }

        cellStart[counts.Count] = running;

        // Pass 2: fill. Items are visited in index order in both passes, so each cell's run stays
        // ascending - the order the per-cell lists had, and the order candidates are gathered in.
        var cellItems = new int[running];
        var cursor = new int[counts.Count];
        Array.Copy(cellStart, cursor, counts.Count);
        for (int i = 0; i < bounds.Length; i++)
        {
            if (!TryGetCellRange(bounds, valid, i, minX, minY, invCellSize, out long cminX, out long cmaxX, out long cminY, out long cmaxY))
                continue;

            for (long cx = cminX; cx <= cmaxX; cx++)
            {
                for (long cy = cminY; cy <= cmaxY; cy++)
                    cellItems[cursor[cellSlots[PackKey(cx, cy)]]++] = i;
            }
        }

        return new SpatialHashGrid2D(cellSlots, cellStart, cellItems, minX, maxX, minY, maxY, invCellSize, bounds.Length);
    }

    private static bool TryGetCellRange(
        Bounds2D[] bounds,
        bool[]? valid,
        int index,
        double minX,
        double minY,
        double invCellSize,
        out long cminX,
        out long cmaxX,
        out long cminY,
        out long cmaxY)
    {
        cminX = cmaxX = cminY = cmaxY = 0;
        if (valid != null && !valid[index])
            return false;

        Bounds2D current = bounds[index];
        if (!double.IsFinite(current.MinX) ||
            !double.IsFinite(current.MaxX) ||
            !double.IsFinite(current.MinY) ||
            !double.IsFinite(current.MaxY) ||
            current.MinX > current.MaxX ||
            current.MinY > current.MaxY)
        {
            return false;
        }

        cminX = ToCell(current.MinX, minX, invCellSize);
        cmaxX = ToCell(current.MaxX, minX, invCellSize);
        cminY = ToCell(current.MinY, minY, invCellSize);
        cmaxY = ToCell(current.MaxY, minY, invCellSize);
        return true;
    }

    /// <summary>Items registered in cell (cellX, cellY), ascending. Empty when unoccupied.</summary>
    private ReadOnlySpan<int> CellItems(long cellX, long cellY)
    {
        if (!_cellSlots.TryGetValue(PackKey(cellX, cellY), out int slot))
            return ReadOnlySpan<int>.Empty;

        int start = _cellStart[slot];
        return _cellItems.AsSpan(start, _cellStart[slot + 1] - start);
    }

    public void GatherCandidates(in Bounds2D queryBounds, List<int> candidates, QueryScratch? scratch = null)
    {
        candidates.Clear();
        if (_cellSlots.Count == 0 || !queryBounds.Intersects(new Bounds2D(_minX, _maxX, _minY, _maxY)))
            return;

        // A query can dwarf the indexed geometry (a terrain face containing a tiny zone).
        // Cells beyond the index extent are empty, so never enumerate them.
        long minCellX = ToCell(Math.Max(queryBounds.MinX, _minX), _minX, _invCellSize);
        long maxCellX = ToCell(Math.Min(queryBounds.MaxX, _maxX), _minX, _invCellSize);
        long minCellY = ToCell(Math.Max(queryBounds.MinY, _minY), _minY, _invCellSize);
        long maxCellY = ToCell(Math.Min(queryBounds.MaxY, _maxY), _minY, _invCellSize);

        if (scratch != null)
            scratch.BeginQuery(ItemCount);

        HashSet<int>? seen = scratch == null ? new HashSet<int>() : null;

        for (long cy = minCellY; cy <= maxCellY; cy++)
        {
            for (long cx = minCellX; cx <= maxCellX; cx++)
            {
                foreach (int itemIndex in CellItems(cx, cy))
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
