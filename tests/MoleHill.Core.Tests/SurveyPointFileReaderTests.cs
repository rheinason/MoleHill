using MoleHill.Core.Interop;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class SurveyPointFileReaderTests
{
    private static SurveyReadOptions Options(string preset, char? delimiter = ',') => new()
    {
        ColumnMap = SurveyColumnMap.FromPreset(preset)!,
        Delimiter = delimiter
    };

    [Fact]
    public void Read_Pnezd_MapsNorthingToYAndEastingToX()
    {
        // The whole point of the format's name: N comes before E, so the second column is Y.
        SurveyPointFile file = SurveyPointFileReader.Read("1,5000.000,2000.000,12.50,EP", Options("PNEZD"));

        SurveyPoint point = Assert.Single(file.Points);
        Assert.Equal(2000.000, point.X, 6);
        Assert.Equal(5000.000, point.Y, 6);
        Assert.Equal(12.50, point.Z, 6);
        Assert.Equal(1, point.Number);
        Assert.Equal("EP", point.RawDescription);
    }

    [Fact]
    public void Read_PenzdVersusPnezd_SwapsTheTwoCoordinateColumns()
    {
        const string row = "1,5000.000,2000.000,12.50,EP";

        SurveyPoint asPnezd = Assert.Single(SurveyPointFileReader.Read(row, Options("PNEZD")).Points);
        SurveyPoint asPenzd = Assert.Single(SurveyPointFileReader.Read(row, Options("PENZD")).Points);

        Assert.Equal(asPnezd.X, asPenzd.Y, 6);
        Assert.Equal(asPnezd.Y, asPenzd.X, 6);
    }

    [Fact]
    public void Read_EnzWithoutPointNumber_LeavesNumberNull()
    {
        SurveyPointFile file = SurveyPointFileReader.Read("2000.0,5000.0,12.5,TOE", Options("ENZ"));

        SurveyPoint point = Assert.Single(file.Points);
        Assert.Null(point.Number);
        Assert.Equal(2000.0, point.X, 6);
        Assert.Equal("TOE", point.RawDescription);
    }

    [Fact]
    public void Read_DescriptionContainingTheDelimiter_KeepsTheWholeDescription()
    {
        SurveyPointFile file = SurveyPointFileReader.Read("1,5000,2000,12.5,EP,START OF KERB", Options("PNEZD"));

        SurveyPoint point = Assert.Single(file.Points);
        Assert.Equal("EP,START OF KERB", point.RawDescription);
    }

    [Fact]
    public void Read_QuotedDescription_IsOneFieldWithQuotesRemoved()
    {
        SurveyPointFile file = SurveyPointFileReader.Read("1,5000,2000,12.5,\"EP, KERB\"", Options("PNEZD"));

        Assert.Equal("EP, KERB", Assert.Single(file.Points).RawDescription);
    }

    [Fact]
    public void Read_BlankLinesAndComments_AreSkippedWithoutDiagnostics()
    {
        const string content = "# survey export\n\n1,5000,2000,12.5,EP\n; trailing note\n";

        SurveyPointFile file = SurveyPointFileReader.Read(content, Options("PNEZD"));

        Assert.Single(file.Points);
        Assert.Empty(file.Diagnostics);
        Assert.Equal(1, file.DataLineCount);
    }

    [Fact]
    public void Read_ByteOrderMark_DoesNotBreakTheFirstRow()
    {
        SurveyPointFile file = SurveyPointFileReader.Read("﻿1,5000,2000,12.5,EP", Options("PNEZD"));

        Assert.Equal(1, Assert.Single(file.Points).Number);
        Assert.Empty(file.Diagnostics);
    }

    [Fact]
    public void Read_CrLfLineEndings_DoNotLeaveCarriageReturnsInDescriptions()
    {
        SurveyPointFile file = SurveyPointFileReader.Read("1,5000,2000,12.5,EP\r\n2,5001,2001,12.6,EP\r\n", Options("PNEZD"));

        Assert.Equal(2, file.Points.Count);
        Assert.All(file.Points, point => Assert.Equal("EP", point.RawDescription));
    }

    [Fact]
    public void Read_ShortRow_ReportsTheLineAndKeepsReadingTheRest()
    {
        const string content = "1,5000,2000,12.5,EP\n2,5001\n3,5002,2002,12.7,EP\n";

        SurveyPointFile file = SurveyPointFileReader.Read(content, Options("PNEZD"));

        Assert.Equal(2, file.Points.Count);
        SurveyReadDiagnostic diagnostic = Assert.Single(file.Diagnostics);
        Assert.Equal(2, diagnostic.LineNumber);
        Assert.Contains("columns", diagnostic.Message);
    }

    [Fact]
    public void Read_UnparseableCoordinate_NamesTheFieldAndTheLine()
    {
        SurveyPointFile file = SurveyPointFileReader.Read("1,5000,NOTANUMBER,12.5,EP", Options("PNEZD"));

        Assert.Empty(file.Points);
        SurveyReadDiagnostic diagnostic = Assert.Single(file.Diagnostics);
        Assert.Equal(1, diagnostic.LineNumber);
        Assert.Contains("easting", diagnostic.Message);
    }

    [Fact]
    public void Read_CommaDecimalInSemicolonFile_FailsLoudlyInsteadOfReadingTooLarge()
    {
        // A comma-decimal locale exports semicolon-delimited rows. Read with thousands separators
        // allowed, "512345,67" became 51234567 — a hundred times too far away and entirely silent.
        SurveyPointFile file = SurveyPointFileReader.Read(
            "1;6123456,78;512345,67;12,5;EP",
            Options("PNEZD", delimiter: ';'));

        Assert.Empty(file.Points);
        SurveyReadDiagnostic diagnostic = Assert.Single(file.Diagnostics);
        Assert.Equal(1, diagnostic.LineNumber);
        Assert.Contains("512345,67", diagnostic.Message);
        Assert.Contains("decimal comma", diagnostic.Message);
    }

    [Fact]
    public void Read_PointDecimalInSemicolonFile_StillReads()
    {
        SurveyPointFile file = SurveyPointFileReader.Read(
            "1;6123456.78;512345.67;12.5;EP",
            Options("PNEZD", delimiter: ';'));

        SurveyPoint point = Assert.Single(file.Points);
        Assert.Equal(512345.67, point.X, 6);
        Assert.Equal(6123456.78, point.Y, 6);
    }

    [Fact]
    public void Read_HeaderRow_IsSkippedWhenDeclared()
    {
        var options = Options("PNEZD");
        options.HeaderRowCount = 1;

        SurveyPointFile file = SurveyPointFileReader.Read("P,N,E,Z,D\n1,5000,2000,12.5,EP\n", options);

        Assert.Single(file.Points);
        Assert.Empty(file.Diagnostics);
    }

    [Fact]
    public void Read_MaxPoints_StopsEarlyForAPreview()
    {
        var options = Options("PNEZD");
        options.MaxPoints = 2;

        SurveyPointFile file = SurveyPointFileReader.Read(
            "1,5000,2000,12.5,EP\n2,5001,2001,12.6,EP\n3,5002,2002,12.7,EP\n",
            options);

        Assert.Equal(2, file.Points.Count);
    }

    [Fact]
    public void Read_UnitScale_ScalesAllThreeCoordinates()
    {
        var options = Options("PNEZD");
        options.UnitScale = 0.3048;

        SurveyPoint point = Assert.Single(SurveyPointFileReader.Read("1,100,200,10,EP", options).Points);

        Assert.Equal(200 * 0.3048, point.X, 6);
        Assert.Equal(100 * 0.3048, point.Y, 6);
        Assert.Equal(10 * 0.3048, point.Z, 6);
    }

    [Fact]
    public void Read_VerticalOffset_AppliesAfterScalingAndOnlyToZ()
    {
        var options = Options("PNEZD");
        options.UnitScale = 2.0;
        options.VerticalOffset = -5.0;

        SurveyPoint point = Assert.Single(SurveyPointFileReader.Read("1,100,200,10,EP", options).Points);

        Assert.Equal(400.0, point.X, 6);
        Assert.Equal(200.0, point.Y, 6);
        Assert.Equal((10 * 2.0) - 5.0, point.Z, 6);
    }

    [Theory]
    [InlineData("1\t5000\t2000\t12.5\tEP", '\t')]
    [InlineData("1;5000;2000;12.5;EP", ';')]
    [InlineData("1 5000 2000 12.5 EP", ' ')]
    public void Read_DelimiterNotStated_IsDetectedFromTheFile(string row, char expected)
    {
        SurveyPointFile file = SurveyPointFileReader.Read(row, Options("PNEZD", delimiter: null));

        Assert.Equal(expected, file.Delimiter);
        Assert.Equal(2000.0, Assert.Single(file.Points).X, 6);
    }

    [Fact]
    public void Read_SpaceDelimitedWithAlignmentPadding_CollapsesRunsOfSpaces()
    {
        SurveyPointFile file = SurveyPointFileReader.Read("1    5000.00   2000.00   12.50  EP", Options("PNEZD", delimiter: ' '));

        SurveyPoint point = Assert.Single(file.Points);
        Assert.Equal(2000.00, point.X, 6);
        Assert.Equal("EP", point.RawDescription);
    }

    [Fact]
    public void Read_EmptyContent_ReturnsNoPointsAndNoDiagnostics()
    {
        SurveyPointFile file = SurveyPointFileReader.Read(string.Empty, Options("PNEZD"));

        Assert.False(file.HasPoints);
        Assert.Empty(file.Diagnostics);
        Assert.Equal(0, file.DataLineCount);
    }

    [Fact]
    public void FromPreset_UnknownName_ReturnsNull() => Assert.Null(SurveyColumnMap.FromPreset("PZDNE"));

    [Fact]
    public void RequiredColumnCount_Pnezd_DoesNotCountTheOptionalDescription() =>
        Assert.Equal(4, SurveyColumnMap.FromPreset("PNEZD")!.RequiredColumnCount);

    [Fact]
    public void Read_RowWithNoDescriptionColumn_KeepsTheCoordinatesWithAnEmptyDescription()
    {
        // Exporters routinely omit the trailing field on an uncoded shot instead of writing an empty
        // one. Dropping the row would lose a good level and leave only a line number behind.
        SurveyPointFile file = SurveyPointFileReader.Read("1,5000,2000,12.5", Options("PNEZD"));

        SurveyPoint point = Assert.Single(file.Points);
        Assert.Equal(2000.0, point.X, 6);
        Assert.Equal(12.5, point.Z, 6);
        Assert.Equal(string.Empty, point.RawDescription);
        Assert.Empty(file.Diagnostics);
    }

    [Fact]
    public void Read_RowMissingAnElevation_IsStillReported()
    {
        // The relaxation above must not swallow a row that is genuinely short of coordinates.
        SurveyPointFile file = SurveyPointFileReader.Read("1,5000,2000", Options("PNEZD"));

        Assert.Empty(file.Points);
        Assert.Single(file.Diagnostics);
    }
}
