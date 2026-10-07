namespace MoleHill.Rhino.Registry;

/// <summary>How the panel draws a <see cref="ResultRow"/>.</summary>
internal enum ResultRowKind
{
    /// <summary>A label with a single wrapped value beside it.</summary>
    ReadOnlyValue,

    /// <summary>A label above a read-only text area, for multi-line values the user may want to copy.</summary>
    SelectableSummary
}

/// <summary>
/// One line of a card's "what the last build produced" readout, as pure data: the descriptor decides what
/// to say, the panel decides how to draw it. Keeping the text out of the panel is what lets a test assert
/// it, and what stops two cards of the same family from wording the same row differently.
/// </summary>
internal sealed record ResultRow(string Label, string Value, string Help, ResultRowKind Kind = ResultRowKind.ReadOnlyValue, int MinHeight = 110)
{
    /// <summary>A label with one value and a tooltip.</summary>
    public static ResultRow Of(string label, string value, string help) =>
        new(label, value, help);

    /// <summary>A copyable multi-line summary.</summary>
    public static ResultRow Summary(string label, string value, string help, int minHeight = 110) =>
        new(label, value, help, ResultRowKind.SelectableSummary, minHeight);

    /// <summary>The placeholder every card shows before its first build.</summary>
    public static ResultRow RebuildRequired(string help) =>
        Summary("Summary", "Rebuild required", help, 42);
}

/// <summary>
/// The unit-aware formatters a result readout needs, supplied by the host. The panel passes the document's
/// unit context and the user's slope preference; a test passes fixed stand-ins, which is what keeps the
/// readout text assertable without a running Rhino.
/// </summary>
internal sealed record ResultFormatter(
    Func<double, string> Area,
    Func<double, string> Volume,
    Func<double, string> Length,
    Func<double, string> SlopeDegrees);
