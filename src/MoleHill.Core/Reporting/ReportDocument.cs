namespace MoleHill.Core.Reporting;

/// <summary>
/// A whole report: a title and the tables under it, in reading order.
///
/// This is the single assembled form both report outputs consume — the CSV file and the table drawn
/// into the document. Neither knows how a figure was measured or rounded; they only lay out what is
/// here, which is why the two cannot drift.
/// </summary>
public sealed class ReportDocument
{
    public ReportDocument(string title)
    {
        Title = title ?? string.Empty;
    }

    public string Title { get; }

    public List<ReportTable> Tables { get; } = new();

    /// <summary>Adds a table and returns it, so a caller can fill it in the same statement.</summary>
    public ReportTable AddTable(string title, params ReportColumn[] columns)
    {
        var table = new ReportTable(title, columns);
        Tables.Add(table);
        return table;
    }

    /// <summary>Drops tables that measured nothing, so an empty section is absent rather than a bare heading.</summary>
    public void RemoveEmptyTables() => Tables.RemoveAll(static table => !table.HasRows);
}
