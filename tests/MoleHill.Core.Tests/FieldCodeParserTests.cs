using MoleHill.Core.Interop;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class FieldCodeParserTests
{
    private static FieldCodeTable Table() => FieldCodeTable.CreateDefault();

    [Fact]
    public void Parse_BareCode_HasNoFigureNumberOrMarkers()
    {
        ParsedFieldCode parsed = FieldCodeParser.Parse("EP", Table());

        Assert.Equal("EP", parsed.Code);
        Assert.Null(parsed.FigureNumber);
        Assert.False(parsed.IsStart || parsed.IsEnd || parsed.IsArc || parsed.IsClose);
    }

    [Fact]
    public void Parse_TrailingDigits_AreTheFigureNumber()
    {
        ParsedFieldCode parsed = FieldCodeParser.Parse("EP12", Table());

        Assert.Equal("EP", parsed.Code);
        Assert.Equal(12, parsed.FigureNumber);
    }

    [Fact]
    public void Parse_SeparatedFigureNumber_ReadsTheSameAsASuffix()
    {
        Assert.Equal(FieldCodeParser.Parse("EP 3", Table()), FieldCodeParser.Parse("EP3", Table()));
    }

    [Fact]
    public void Parse_AllDigitCode_KeepsItsDigitsAndGetsNoFigureNumber()
    {
        // Reading "101" as figure 101 of the empty code would merge every numerically coded point into one run.
        ParsedFieldCode parsed = FieldCodeParser.Parse("101", Table());

        Assert.Equal("101", parsed.Code);
        Assert.Null(parsed.FigureNumber);
    }

    [Fact]
    public void Parse_LowerCaseCode_IsUppercased()
    {
        Assert.Equal("EP", FieldCodeParser.Parse("ep", Table()).Code);
    }

    [Theory]
    [InlineData("EP ST")]
    [InlineData("EP.ST")]
    [InlineData("EP,ST")]
    [InlineData("EP/ST")]
    public void Parse_MarkerAfterAnySeparator_IsRecognised(string description)
    {
        Assert.True(FieldCodeParser.Parse(description, Table()).IsStart);
    }

    [Fact]
    public void Parse_ContinuationSuffix_IsStrippedFromTheCode()
    {
        ParsedFieldCode parsed = FieldCodeParser.Parse("EP-", Table());

        Assert.Equal("EP", parsed.Code);
        Assert.True(parsed.IsContinuation);
    }

    [Fact]
    public void Parse_EndArcAndCloseMarkers_AreEachRecognised()
    {
        Assert.True(FieldCodeParser.Parse("EP END", Table()).IsEnd);
        Assert.True(FieldCodeParser.Parse("EP AR", Table()).IsArc);
        Assert.True(FieldCodeParser.Parse("BLD CLOSE", Table()).IsClose);
    }

    [Fact]
    public void Parse_CustomMarkerSpelling_IsHonoured()
    {
        // An office writing BEG must not need a recompile.
        FieldCodeTable table = Table();
        table.StartTokens = new List<string> { "BEG" };

        Assert.True(FieldCodeParser.Parse("EP BEG", table).IsStart);
        Assert.False(FieldCodeParser.Parse("EP ST", table).IsStart);
    }

    [Fact]
    public void Parse_EmptyDescription_HasNoCode()
    {
        Assert.False(FieldCodeParser.Parse("  ", Table()).HasCode);
        Assert.False(FieldCodeParser.Parse(null, Table()).HasCode);
    }

    [Fact]
    public void Parse_UnknownTrailingToken_IsIgnoredRatherThanTreatedAsAMarker()
    {
        ParsedFieldCode parsed = FieldCodeParser.Parse("EP RUBBISH", Table());

        Assert.Equal("EP", parsed.Code);
        Assert.False(parsed.IsStart || parsed.IsEnd || parsed.IsArc || parsed.IsClose);
    }

    [Fact]
    public void RunKey_BareCodeAndZeroFigure_AreDifferentRuns()
    {
        Assert.NotEqual(
            FieldCodeParser.Parse("EP", Table()).RunKey,
            FieldCodeParser.Parse("EP0", Table()).RunKey);
    }

    [Fact]
    public void Parse_ContinuationSuffixDisabled_LeavesTheHyphenOnTheCode()
    {
        FieldCodeTable table = Table();
        table.ContinuationSuffix = string.Empty;

        ParsedFieldCode parsed = FieldCodeParser.Parse("EP-", table);

        Assert.Equal("EP-", parsed.Code);
        Assert.False(parsed.IsContinuation);
    }

    [Theory]
    [InlineData("TOE 1.5")]
    [InlineData("TOE 0.25 ST")]
    [InlineData("TOE -1.5")]
    [InlineData("TOE .5")]
    public void Parse_DecimalAttribute_IsNotReadAsAFigureNumber(string description)
    {
        // A full stop is a separator ("EP.ST"), but not inside a number: "TOE 1.5" read as figure 1
        // split the toe away from every other TOE shot.
        ParsedFieldCode parsed = FieldCodeParser.Parse(description, Table());

        Assert.Equal("TOE", parsed.Code);
        Assert.Null(parsed.FigureNumber);
        Assert.Equal("TOE", parsed.RunKey);
    }

    [Fact]
    public void Parse_DecimalAttributeFollowedByMarker_StillSeesTheMarker()
    {
        ParsedFieldCode parsed = FieldCodeParser.Parse("TOE 0.25 ST", Table());

        Assert.True(parsed.IsStart);
    }

    [Fact]
    public void Parse_FigureNumberThenStopMarker_StillSplitsOnTheStop()
    {
        // "EP1.ST" and "EP 1." are the stop used as punctuation, not decimals.
        Assert.Equal(1, FieldCodeParser.Parse("EP 1.", Table()).FigureNumber);

        ParsedFieldCode parsed = FieldCodeParser.Parse("EP1.ST", Table());
        Assert.Equal(1, parsed.FigureNumber);
        Assert.True(parsed.IsStart);
    }
}
