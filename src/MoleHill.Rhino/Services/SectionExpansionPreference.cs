namespace MoleHill.Rhino.Services;

/// <summary>
/// Whether a card section is open, remembered per user and per card type: open Grade Pad's Settings once
/// and every Grade Pad card shows it open, in every document, until you close it again.
///
/// <para>A per-user display preference, not document state, for the same reason as
/// <see cref="SlopeUnitPreference"/>: opening a section must never dirty a document, churn a build
/// fingerprint or differ between two people reading the same file. Storage is injected so the Registry
/// and Services sources stay testable without a live plug-in; unwired, it remembers for the session.</para>
/// </summary>
internal static class SectionExpansionPreference
{
    private static readonly Dictionary<string, bool> Session = new(StringComparer.Ordinal);

    /// <summary>Set by the plug-in on load: reads a persisted state, or null when none was saved.</summary>
    public static Func<string, bool?>? Provider { get; set; }

    /// <summary>Set by the plug-in on load to persist a change.</summary>
    public static Action<string, bool>? Writer { get; set; }

    /// <summary>The settings key for one section of one card type ("grade-pad.settings").</summary>
    public static string Key(string cardKind, string sectionKey) => $"{cardKind}.{sectionKey}";

    public static bool IsExpanded(string cardKind, string sectionKey, bool expandedByDefault)
    {
        string key = Key(cardKind, sectionKey);
        if (Session.TryGetValue(key, out bool session))
            return session;

        return Provider?.Invoke(key) ?? expandedByDefault;
    }

    public static void SetExpanded(string cardKind, string sectionKey, bool expanded)
    {
        string key = Key(cardKind, sectionKey);
        Session[key] = expanded;
        Writer?.Invoke(key, expanded);
    }

    public static void ResetForTests()
    {
        Session.Clear();
        Provider = null;
        Writer = null;
    }
}
