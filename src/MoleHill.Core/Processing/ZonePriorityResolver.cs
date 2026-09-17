namespace MoleHill.Core.Processing;

/// <summary>
/// Orders zone boundaries for the shared area splitter. Later entries win where
/// boundaries overlap. Stack-priority zones separate runs of elevation-priority
/// zones; sorting a mixed set with a pairwise comparator is non-transitive.
/// </summary>
public static class ZonePriorityResolver
{
    public readonly record struct BoundaryPriority(
        int ZoneOrder,
        int SourceOrder,
        double Elevation,
        bool UseInputElevation);

    public static void SortInPlace<T>(IList<T> entries, Func<T, BoundaryPriority> priority)
    {
        if (entries.Count < 2)
            return;

        var ordered = entries
            .Select((entry, index) => (Entry: entry, Priority: priority(entry), OriginalIndex: index))
            .OrderBy(item => item.Priority.ZoneOrder)
            .ThenBy(item => item.Priority.SourceOrder)
            .ThenBy(item => item.OriginalIndex)
            .ToArray();

        int start = 0;
        while (start < ordered.Length)
        {
            if (!ordered[start].Priority.UseInputElevation)
            {
                start++;
                continue;
            }

            int end = start + 1;
            while (end < ordered.Length && ordered[end].Priority.UseInputElevation)
                end++;

            Array.Sort(ordered, start, end - start,
                Comparer<(T Entry, BoundaryPriority Priority, int OriginalIndex)>.Create((left, right) =>
                {
                    int elevation = left.Priority.Elevation.CompareTo(right.Priority.Elevation);
                    if (elevation != 0)
                        return elevation;
                    int source = left.Priority.SourceOrder.CompareTo(right.Priority.SourceOrder);
                    if (source != 0)
                        return source;
                    int zone = left.Priority.ZoneOrder.CompareTo(right.Priority.ZoneOrder);
                    return zone != 0 ? zone : left.OriginalIndex.CompareTo(right.OriginalIndex);
                }));
            start = end;
        }

        for (int index = 0; index < entries.Count; index++)
            entries[index] = ordered[index].Entry;
    }
}
