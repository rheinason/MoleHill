namespace MoleHill.Rhino.Registry;

/// <summary>One choosable entry in a searchable Add menu (Add Modifier, Add Analysis, ...).</summary>
internal sealed record AddMenuEntry(string Label, string? Detail, string? Keywords, string? IconName, Action Invoke);

/// <summary>
/// The filter behind the searchable Add menus. Pure, so it is testable without Eto. Every typed word must
/// match somewhere; entries rank by where the best-matching word landed, and ties keep menu order, so an
/// empty query shows the menu exactly as before.
/// </summary>
internal static class AddMenuSearch
{
    // Lower is better.
    private const int LabelPrefix = 0;
    private const int WordPrefix = 1;
    private const int Initials = 1;
    private const int LabelContains = 2;
    private const int DetailContains = 3;
    private const int NoMatch = int.MaxValue;

    public static IReadOnlyList<AddMenuEntry> Filter(IReadOnlyList<AddMenuEntry> entries, string? query)
    {
        string q = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length == 0)
            return entries;

        string[] tokens = q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(AddMenuEntry Entry, int Score, int Order)>();
        for (int i = 0; i < entries.Count; i++)
        {
            int score = Score(entries[i], q, tokens);
            if (score != NoMatch)
                scored.Add((entries[i], score, i));
        }

        return scored
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Order)
            .Select(item => item.Entry)
            .ToList();
    }

    internal static int Score(AddMenuEntry entry, string query, string[] tokens)
    {
        string label = entry.Label.ToLowerInvariant();
        if (label.StartsWith(query, StringComparison.Ordinal))
            return LabelPrefix;

        string[] words = SplitWords(label);
        string initials = string.Concat(words.Select(word => word[0]));
        string detail = ((entry.Detail ?? string.Empty) + " " + (entry.Keywords ?? string.Empty)).ToLowerInvariant();

        int worst = LabelPrefix;
        foreach (string token in tokens)
        {
            int best;
            if (words.Any(word => word.StartsWith(token, StringComparison.Ordinal)))
                best = WordPrefix;
            else if (token.Length >= 2 && initials.StartsWith(token, StringComparison.Ordinal))
                best = Initials;
            else if (label.Contains(token, StringComparison.Ordinal))
                best = LabelContains;
            else if (detail.Contains(token, StringComparison.Ordinal))
                best = DetailContains;
            else
                return NoMatch;

            worst = Math.Max(worst, best);
        }

        return worst;
    }

    private static string[] SplitWords(string text) =>
        text.Split(new[] { ' ', '-', '/', '(', ')', ',', '.', '&' }, StringSplitOptions.RemoveEmptyEntries);
}
