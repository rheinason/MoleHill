// Immutable spatial grid with dense or sparse caller-owned query scratch.
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
        private readonly HashSet<int>? _visited;

        // Sparse scratch is for localized queries on a large shared index: workers then retain
        // only the candidates they visit, rather than an item-sized stamp array each.
        public QueryScratch(int itemCapacity = 0, bool sparse = false)
        {
            _visited = sparse ? new HashSet<int>() : null;
            _marks = !sparse && itemCapacity > 0 ? new int[itemCapacity] : Array.Empty<int>();
        }

        internal void EnsureCapacity(int itemCount)
        {
            if (_visited == null && _marks.Length < itemCount)
                _marks = new int[itemCount];
        }

        internal bool TryVisit(int itemIndex)
        {
            if (_visited != null)
                return _visited.Add(itemIndex);

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
            if (_visited != null)
            {
                _visited.Clear();
                return;
            }

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

    /// <summary>
    /// What building the index actually cost. An item is registered into every cell its bounding box
    /// covers, so memberships - not item count - is what the flattened array is sized from, and what a
    /// long diagonal or a huge box in a fine grid inflates. Recorded so the growth can be measured
    /// rather than assumed.
    /// </summary>
    internal readonly record struct BuildStatistics(
        int ItemCount,
        int IndexedItemCount,
        int CellCount,
        long Memberships,
        int MaxCellOccupancy,
        double CellSize,
        int CoarseningPasses);

    /// <summary>The statistics for the build that produced this index.</summary>
    internal BuildStatistics Statistics { get; private init; }

    /// <summary>
    /// Memberships allowed per indexed item before the grid is coarsened. Generous: a well-behaved
    /// distribution sits near 1-4, and the point is to catch memberships growing super-linearly in the
    /// item count, not to tune ordinary builds.
    /// </summary>
    internal const int MembershipsPerItemBudget = 32;

    /// <summary>Floor for the budget, so a handful of items is never coarsened on a ratio alone.</summary>
    internal const int MinimumMembershipBudget = 1 << 20;

    /// <summary>Cell-size multiplier per coarsening pass; each pass cuts a box's cell coverage ~16x.</summary>
    private const double CoarseningFactor = 4.0;

    /// <summary>
    /// Coarsening passes before the grid collapses straight to a single cell. Bounded so a degenerate
    /// input cannot spin: 8 passes is a 65,536x cell size, past any real distribution.
    /// </summary>
    private const int MaxCoarseningPasses = 8;

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

    /// <param name="cancellation">
    /// Consulted while scanning items and filling cells. Index construction is a phase in its own right
    /// on a large model - a superseded build must be able to stop inside it, not only before it.
    /// </param>
    public static SpatialHashGrid2D Build(Bounds2D[] bounds, bool[]? valid = null, CancellationProbe? cancellation = null)
    {
        return Build(bounds, valid, cancellation, out _);
    }

    /// <summary>
    /// As the three-argument overload, and reports what the build cost.
    /// </summary>
    internal static SpatialHashGrid2D Build(
        Bounds2D[] bounds,
        bool[]? valid,
        CancellationProbe? cancellation,
        out BuildStatistics statistics)
    {
        if (bounds.Length == 0)
        {
            statistics = new BuildStatistics(0, 0, 0, 0, 0, 1, 0);
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

        CancellationProbe probe = cancellation ?? CancellationProbe.None;
        for (int i = 0; i < bounds.Length; i++)
        {
            probe.ThrowIfCancelledOften();
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
            statistics = new BuildStatistics(bounds.Length, 0, 0, 0, 0, 1, 0);
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

        // Pass 1: assign a slot to every occupied cell and count its memberships.
        //
        // Memberships are accumulated into a long and checked against a budget before anything is sized
        // from them. An item is registered into EVERY cell its bounding box covers, so a long diagonal,
        // a huge box among small ones, or overlapping bounds over a fine grid make memberships grow
        // super-linearly in the item count - and the flattened array was previously sized from an int
        // sum of exactly that. Past the budget the grid is coarsened and recounted. That is a guard,
        // not a tuning knob: an ordinary distribution sits orders of magnitude below the budget and
        // takes the first pass unchanged. Coarsening is a pure function of the input, so a build stays
        // reproducible; it widens the candidate set a query gathers, which callers already filter by
        // an exact bounds test.
        long membershipBudget = Math.Max(
            (long)validCount * MembershipsPerItemBudget,
            MinimumMembershipBudget);

        Dictionary<long, int> cellSlots;
        List<int> counts;
        long memberships;
        int coarseningPasses = 0;
        while (true)
        {
            double invCellSizeAttempt = 1.0 / cellSize;
            cellSlots = new Dictionary<long, int>(Math.Max(16, validCount));
            counts = new List<int>(Math.Max(16, validCount));
            memberships = 0;
            bool overBudget = false;

            for (int i = 0; i < bounds.Length && !overBudget; i++)
            {
                probe.ThrowIfCancelledOften();
                if (!TryGetCellRange(bounds, valid, i, minX, minY, invCellSizeAttempt, out long cminX, out long cmaxX, out long cminY, out long cmaxY))
                    continue;

                for (long cx = cminX; cx <= cmaxX && !overBudget; cx++)
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

                        // Abandoned the moment the budget is passed, so a pathological distribution is
                        // cheap to reject instead of being counted all the way out first.
                        if (++memberships > membershipBudget)
                        {
                            overBudget = true;
                            break;
                        }
                    }
                }
            }

            if (!overBudget)
                break;

            // One cell holds every item exactly once, so memberships there equals the item count: the
            // coarsening always terminates inside the budget rather than failing.
            double coarser = cellSize * CoarseningFactor;
            coarseningPasses++;
            cellSize = !double.IsFinite(coarser) || coarser <= cellSize || coarseningPasses > MaxCoarseningPasses
                ? Math.Max(span * 2.0, 1.0)
                : coarser;
        }

        double invCellSize = 1.0 / cellSize;

        var cellStart = new int[counts.Count + 1];
        int running = 0;
        int maxOccupancy = 0;
        for (int slot = 0; slot < counts.Count; slot++)
        {
            cellStart[slot] = running;
            running += counts[slot];
            if (counts[slot] > maxOccupancy)
                maxOccupancy = counts[slot];
        }

        cellStart[counts.Count] = running;
        statistics = new BuildStatistics(
            bounds.Length,
            validCount,
            counts.Count,
            memberships,
            maxOccupancy,
            cellSize,
            coarseningPasses);

        // Pass 2: fill. Items are visited in index order in both passes, so each cell's run stays
        // ascending - the order the per-cell lists had, and the order candidates are gathered in.
        // Checked before the membership buffer is allocated: it is the largest allocation here, and a
        // build already known to be superseded should not reserve it.
        probe.ThrowIfCancelled();
        var cellItems = new int[running];
        var cursor = new int[counts.Count];
        Array.Copy(cellStart, cursor, counts.Count);
        for (int i = 0; i < bounds.Length; i++)
        {
            probe.ThrowIfCancelledOften();
            if (!TryGetCellRange(bounds, valid, i, minX, minY, invCellSize, out long cminX, out long cmaxX, out long cminY, out long cmaxY))
                continue;

            for (long cx = cminX; cx <= cmaxX; cx++)
            {
                for (long cy = cminY; cy <= cmaxY; cy++)
                    cellItems[cursor[cellSlots[PackKey(cx, cy)]]++] = i;
            }
        }

        return new SpatialHashGrid2D(cellSlots, cellStart, cellItems, minX, maxX, minY, maxY, invCellSize, bounds.Length)
        {
            Statistics = statistics
        };
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
