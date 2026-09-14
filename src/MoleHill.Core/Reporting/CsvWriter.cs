using System.Globalization;
using System.Text;

namespace MoleHill.Core.Reporting;

/// <summary>
/// Renders a <see cref="ReportDocument"/> as RFC 4180 CSV.
///
/// Two decisions worth stating, because both are invisible until the file is opened somewhere else:
///
/// The separator is a comma and the numbers are invariant-culture, so a file written on a machine whose
/// list separator is a semicolon still parses everywhere. Excel in a comma-decimal locale will want the
/// separator declared, which is what <see cref="SeparatorDeclaration"/> is for — it is a spreadsheet
/// convention rather than part of the format, so it is opt-in.
///
/// Tables are stacked in one file with a blank line between them and a title line above each. A report
/// is several differently shaped tables; one file per table would make the deliverable a folder.
/// </summary>
public static class CsvWriter
{
    public const string SeparatorDeclaration = "sep=,";

    public static string Write(ReportDocument document, bool includeSeparatorDeclaration = false)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder();
        if (includeSeparatorDeclaration)
            builder.Append(SeparatorDeclaration).Append("\r\n");

        if (!string.IsNullOrWhiteSpace(document.Title))
        {
            builder.Append(Escape(document.Title)).Append("\r\n");
            builder.Append("\r\n");
        }

        for (int i = 0; i < document.Tables.Count; i++)
        {
            ReportTable table = document.Tables[i];
            if (i > 0)
                builder.Append("\r\n");

            if (!string.IsNullOrWhiteSpace(table.Title))
                builder.Append(Escape(table.Title)).Append("\r\n");

            AppendRow(builder, table.Columns.Select(static column => column.DisplayHeading));
            foreach (IReadOnlyList<string> row in table.Rows)
                AppendRow(builder, row);
        }

        return builder.ToString();
    }

    /// <summary>Formats a number for a CSV cell: invariant culture, fixed decimals, no thousands separator —
    /// a grouped "1,250.00" would split across two cells.</summary>
    public static string Number(double value, int decimals = 2)
    {
        if (!double.IsFinite(value))
            return string.Empty;

        // Round first, then strip a negative zero: a cut/fill net that balances to within a millionth of a
        // cubic metre is zero, and "-0.00" in a quantity column reads as a defect in the report.
        double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0.0)
            rounded = 0.0;

        return rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static void AppendRow(StringBuilder builder, IEnumerable<string> cells)
    {
        bool first = true;
        foreach (string cell in cells)
        {
            if (!first)
                builder.Append(',');
            builder.Append(Escape(cell));
            first = false;
        }

        builder.Append("\r\n");
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ||
            value.Length != value.Trim().Length;
        if (!needsQuotes)
            return value;

        return string.Concat("\"", value.Replace("\"", "\"\""), "\"");
    }
}
