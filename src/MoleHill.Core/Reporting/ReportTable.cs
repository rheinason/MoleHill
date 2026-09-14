namespace MoleHill.Core.Reporting;

/// <summary>
/// One titled block of a <see cref="ReportDocument"/>: a heading row and the rows under it, already
/// formatted as text.
///
/// Cells are strings rather than numbers on purpose. Rounding is a presentation decision — how many
/// decimals a volume is worth is the same question in a spreadsheet and on a drawing — and settling it
/// once, where the report is assembled, is what stops the exported CSV and the drawn table disagreeing
/// about the same figure.
/// </summary>
public sealed class ReportTable
{
    public ReportTable(string title, IReadOnlyList<ReportColumn> columns)
    {
        Title = title ?? string.Empty;
        Columns = columns ?? Array.Empty<ReportColumn>();
    }

    public string Title { get; }

    public IReadOnlyList<ReportColumn> Columns { get; }

    public List<IReadOnlyList<string>> Rows { get; } = new();

    /// <summary>Adds a row, padding or trimming it to the column count so no consumer has to.</summary>
    public void AddRow(params string?[] cells)
    {
        var row = new string[Columns.Count];
        for (int i = 0; i < row.Length; i++)
            row[i] = i < cells.Length ? cells[i] ?? string.Empty : string.Empty;
        Rows.Add(row);
    }

    public bool HasRows => Rows.Count > 0;
}
