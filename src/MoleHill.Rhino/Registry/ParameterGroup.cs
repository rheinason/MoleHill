namespace MoleHill.Rhino.Registry;

/// <summary>One group of a card's rows: a section heading (or none, for the always-visible rows) and the
/// keys of the rows it holds, in order.</summary>
internal sealed record ParameterGroup(string? SectionKey, string Title, IReadOnlyList<string> Keys, bool Expanded = false);
