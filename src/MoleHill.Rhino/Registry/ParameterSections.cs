namespace MoleHill.Rhino.Registry;

/// <summary>One group of a card's rows: a section heading (or none, for the always-visible rows) and the
/// keys of the rows it holds, in order.</summary>
internal sealed record ParameterGroup(string? SectionKey, string Title, IReadOnlyList<string> Keys, bool Expanded = false);

/// <summary>
/// Arranges a type's declared rows into groups, for cards whose rows come from more than one list (a
/// type's own rows plus a shared catalog), where writing them in order is not possible. A group with a
/// section key opens a <see cref="ParameterKind.Section"/>; rows no group names keep their order at the end,
/// so a newly declared row is never lost, only ungrouped.
/// </summary>
internal static class ParameterSections
{
    public static ParameterDescriptor<TDefinition>[] Arrange<TDefinition>(
        IEnumerable<ParameterDescriptor<TDefinition>> rows,
        params ParameterGroup[] groups)
        where TDefinition : class
    {
        var byKey = rows.ToList();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ParameterDescriptor<TDefinition>>(byKey.Count + groups.Length);

        foreach (ParameterGroup group in groups)
        {
            if (group.SectionKey != null)
                result.Add(ParameterDescriptor<TDefinition>.Section(group.SectionKey, group.Title, group.Expanded));

            foreach (string key in group.Keys)
            {
                var row = byKey.FirstOrDefault(item => item.Key == key)
                    ?? throw new InvalidOperationException($"No row '{key}' to place in group '{group.Title}'.");
                result.Add(row);
                placed.Add(key);
            }
        }

        result.AddRange(byKey.Where(row => !placed.Contains(row.Key)));
        return result.ToArray();
    }
}
