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
    public static void Move<T>(List<T> items, Func<T, Guid> idOf, Guid sourceId, Guid anchorId, bool displayReversed = false)
    {
        if (sourceId == anchorId)
            return;

        int from = items.FindIndex(item => idOf(item) == sourceId);
        if (from < 0)
            return;

        T moved = items[from];
        items.RemoveAt(from);

        if (anchorId == Guid.Empty)
        {
            if (displayReversed)
                items.Insert(0, moved);
            else
                items.Add(moved);
            return;
        }

        int anchor = items.FindIndex(item => idOf(item) == anchorId);
        int insert = anchor < 0
            ? items.Count
            : displayReversed ? Math.Min(anchor + 1, items.Count) : anchor;
        items.Insert(insert, moved);
    }

}
