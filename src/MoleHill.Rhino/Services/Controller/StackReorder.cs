namespace MoleHill.Rhino.Services;

/// <summary>
/// The list arithmetic behind dragging a card to a drop target in a panel stack. Every reorderable stack
/// (modifiers, zones, analyses, annotations) shares it; they differ only in which way the stack is drawn.
/// </summary>
internal static class StackReorder
{
    /// <summary>
    /// Moves <paramref name="sourceId"/> to the drop target above <paramref name="anchorId"/>. The stack is
    /// drawn in list order, or bottom-to-top when <paramref name="displayReversed"/> (modifiers, whose first
    /// item is the bottom card), so "above" means after the anchor in the list and the tail means the start.
    /// </summary>
    /// <param name="minimumIndex">Items below this index are pinned: they do not move and nothing lands among
    /// them (the base Triangulate is modifier 0).</param>
    public static void Move<T>(List<T> items, Func<T, Guid> idOf, Guid sourceId, Guid anchorId, bool displayReversed = false, int minimumIndex = 0)
    {
        if (sourceId == anchorId)
            return;

        int from = items.FindIndex(item => idOf(item) == sourceId);
        if (from < 0 || from < minimumIndex)
            return;

        T moved = items[from];
        items.RemoveAt(from);

        int floor = Math.Min(minimumIndex, items.Count);
        if (anchorId == Guid.Empty)
        {
            if (displayReversed)
                items.Insert(floor, moved);
            else
                items.Add(moved);
            return;
        }

        int anchor = items.FindIndex(item => idOf(item) == anchorId);
        int insert = anchor < 0
            ? items.Count
            : displayReversed ? Math.Min(anchor + 1, items.Count) : anchor;
        items.Insert(Math.Max(insert, floor), moved);
    }

}
