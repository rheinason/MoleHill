namespace MoleHill.Rhino.Registry;

/// <summary>
/// What a modifier stage's last run measured, for its card: read-only <see cref="ResultRow"/>s and an
/// optional warning drawn on the card's warning surface. A stage stores it on its own stage cache entry, so
/// a cache hit keeps showing what the cached run measured, and the panel draws it generically — no type
/// switch, any modifier may report. Immutable: shared by reference between the UI cache and worker copies.
/// </summary>
internal sealed record ModifierStageReport(IReadOnlyList<ResultRow> Rows, string? Warning = null)
{
    /// <summary>A stable text form for the card-result signature: the cards redraw when it changes.</summary>
    public string Signature =>
        string.Join("|", Rows.Select(row => $"{row.Label}={row.Value}")) + "|" + Warning;
}
