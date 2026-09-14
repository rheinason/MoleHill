namespace MoleHill.Core.Reporting;

/// <summary>How a report column's cells line up when the table is drawn. CSV ignores it.</summary>
public enum ReportAlignment
{
    Left,
    Right
}

/// <summary>
/// One column of a <see cref="ReportTable"/>: a heading, the unit its cells are measured in, and how
/// those cells sit in a drawn cell.
///
/// The unit is a property of the column, not of the cell, because that is the only arrangement that
/// works in both outputs. A CSV cell that reads "1250.00 m²" is a string a spreadsheet cannot sum; a
/// bare 1250 with the unit nowhere is worse than no report at all. Naming the unit once in the heading
/// leaves every cell a plain number and still says what it is.
/// </summary>
public sealed record ReportColumn(string Heading, string? Unit = null, ReportAlignment Alignment = ReportAlignment.Left)
{
    /// <summary>The heading as a reader sees it: "Plan Area (m²)", or just "Zone" when unitless.</summary>
    public string DisplayHeading => string.IsNullOrWhiteSpace(Unit) ? Heading : $"{Heading} ({Unit})";
}
