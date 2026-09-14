using System.Globalization;
using MoleHill.Core.Reporting;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class CsvWriterTests
{
    [Fact]
    public void Write_Column_PutsTheUnitInTheHeadingNotTheCell()
    {
        var document = new ReportDocument("Report");
        ReportTable table = document.AddTable("Zones", new ReportColumn("Plan Area", "m²"));
        table.AddRow("1250.00");

        string csv = CsvWriter.Write(document);

        Assert.Contains("Plan Area (m²)", csv);
        Assert.Contains("1250.00", csv);
        Assert.DoesNotContain("1250.00 m²", csv);
    }

    [Fact]
    public void Write_CellWithCommaOrQuote_IsEscapedRfc4180()
    {
        var document = new ReportDocument(string.Empty);
        ReportTable table = document.AddTable(string.Empty, new ReportColumn("Zone"), new ReportColumn("Note"));
        table.AddRow("Lawn, upper", "He said \"level\"");

        string csv = CsvWriter.Write(document);

        Assert.Contains("\"Lawn, upper\",\"He said \"\"level\"\"\"", csv);
    }

    [Fact]
    public void Write_TwoTables_SeparatesThemWithABlankLine()
    {
        var document = new ReportDocument(string.Empty);
        document.AddTable("First", new ReportColumn("A")).AddRow("1");
        document.AddTable("Second", new ReportColumn("B")).AddRow("2");

        string csv = CsvWriter.Write(document);

        Assert.Contains("A\r\n1\r\n\r\nSecond\r\n", csv);
    }

    /// <summary>
    /// A report written on a comma-decimal machine has to parse on every other machine, so numbers are
    /// invariant and ungrouped — a grouped "1,250.00" would split itself across two cells.
    /// </summary>
    [Fact]
    public void Number_InACommaDecimalCulture_StaysInvariantAndUngrouped()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1250.50", CsvWriter.Number(1250.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>A net volume that balances to within a millionth of a cubic metre is zero; "-0.00" in a
    /// quantity column reads as a defect in the report.</summary>
    [Fact]
    public void Number_ATinyNegativeValue_DoesNotPrintAsNegativeZero()
    {
        Assert.Equal("0.00", CsvWriter.Number(-4.0e-13));
        Assert.Equal("0.00", CsvWriter.Number(-0.0));
    }

    [Fact]
    public void Number_NonFiniteValue_IsBlankRatherThanNaN()
    {
        Assert.Equal(string.Empty, CsvWriter.Number(double.NaN));
        Assert.Equal(string.Empty, CsvWriter.Number(double.PositiveInfinity));
    }

    [Fact]
    public void RemoveEmptyTables_DropsASectionThatMeasuredNothing()
    {
        var document = new ReportDocument("Report");
        document.AddTable("Empty", new ReportColumn("A"));
        document.AddTable("Full", new ReportColumn("A")).AddRow("1");

        document.RemoveEmptyTables();

        Assert.Single(document.Tables);
        Assert.Equal("Full", document.Tables[0].Title);
    }

    [Fact]
    public void AddRow_ShortRow_IsPaddedToTheColumnCount()
    {
        ReportTable table = new ReportDocument(string.Empty)
            .AddTable(string.Empty, new ReportColumn("A"), new ReportColumn("B"), new ReportColumn("C"));

        table.AddRow("1");

        Assert.Equal(new[] { "1", string.Empty, string.Empty }, table.Rows[0]);
    }
}
