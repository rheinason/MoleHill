using System.Runtime.CompilerServices;

namespace MoleHill.Core.Engine;

/// <summary>
/// An immutable spatial-cell → item index in flat CSR form: cell key → slot, and slot →
/// <c>[start[slot], start[slot + 1])</c> in one item array. The face grids and the 2D bounds hash all
/// store their cells this way.
/// </summary>
/// <remarks>
/// A <c>Dictionary&lt;long, List&lt;int&gt;&gt;</c> allocated a list object and its backing array for every
/// occupied cell, millions on a large terrain. The index is built once and never mutated, so the flat
/// layout costs nothing. Build it with <see cref="Builder"/>: count every membership, then
/// <see cref="Builder.BeginFill"/>, then add the same memberships in the same item order. Each cell's run
/// is then in the order its items were added, ascending when items are visited by index, which every
/// first-match query here depends on.
/// </remarks>
internal sealed class CellMembershipIndex
{
    private readonly Dictionary<long, int> _slots;
    private readonly int[] _start;
    private readonly int[] _items;

    private CellMembershipIndex(Dictionary<long, int> slots, int[] start, int[] items)
    {
        _slots = slots;
        _start = start;
        _items = items;
    }

    /// <summary>An index with no occupied cells.</summary>
    public static CellMembershipIndex Empty { get; } =
        new(new Dictionary<long, int>(IndexedMeshTools.CellKeyComparer.Instance), new int[1], Array.Empty<int>());

    public int CellCount => _slots.Count;

    /// <summary>The items registered in a cell, in the order they were added. Empty when unoccupied.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> Items(long cellKey)
    {
        if (!_slots.TryGetValue(cellKey, out int slot))
            return ReadOnlySpan<int>.Empty;

        int start = _start[slot];
        return _items.AsSpan(start, _start[slot + 1] - start);
    }

    /// <summary>The items of the cell in <paramref name="slot"/>, as <see cref="Items"/> returns them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> ItemsAt(int slot)
    {
        int start = _start[slot];
        return _items.AsSpan(start, _start[slot + 1] - start);
    }

    /// <summary>Two-pass builder: <see cref="Count"/> everything, <see cref="BeginFill"/>, then <see cref="Add"/>.</summary>
    internal sealed class Builder
    {
        private readonly Dictionary<long, int> _slots;
        private readonly List<int> _counts;
        private int[]? _start;
        private int[]? _cursor;
        private int[]? _items;

        /// <param name="expectedCells">Initial capacity; spatial-cell keys always take the cell comparer.</param>
        public Builder(int expectedCells)
        {
            int capacity = Math.Max(16, expectedCells);
            _slots = new Dictionary<long, int>(capacity, IndexedMeshTools.CellKeyComparer.Instance);
            _counts = new List<int>(capacity);
        }

        public int CellCount => _slots.Count;

        /// <summary>Largest membership count of any one cell. Valid after <see cref="BeginFill"/>.</summary>
        public int MaxOccupancy { get; private set; }

        /// <summary>
        /// Pass 1: records one membership of <paramref name="cellKey"/> and returns the cell's slot, for
        /// <see cref="ItemsAt"/> on the built index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Count(long cellKey)
        {
            if (!_slots.TryGetValue(cellKey, out int slot))
            {
                slot = _counts.Count;
                _slots[cellKey] = slot;
                _counts.Add(0);
            }

            _counts[slot]++;
            return slot;
        }

        /// <summary>
        /// Lays out the runs and allocates the item array, the largest allocation here, so a cancellable
        /// caller checks for cancellation before calling this.
        /// </summary>
        public void BeginFill()
        {
            int cellCount = _counts.Count;
            var start = new int[cellCount + 1];
            int running = 0;
            int maxOccupancy = 0;
            for (int slot = 0; slot < cellCount; slot++)
            {
                start[slot] = running;
                int count = _counts[slot];
                running += count;
                if (count > maxOccupancy)
                    maxOccupancy = count;
            }

            start[cellCount] = running;
            _start = start;
            _cursor = new int[cellCount];
            Array.Copy(start, _cursor, cellCount);
            _items = new int[running];
            MaxOccupancy = maxOccupancy;
        }

        /// <summary>Pass 2: places <paramref name="item"/> in a cell counted in pass 1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(long cellKey, int item) => _items![_cursor![_slots[cellKey]]++] = item;

        public CellMembershipIndex Build() =>
            new(_slots, _start ?? throw new InvalidOperationException("BeginFill was not called."), _items!);
    }
}
