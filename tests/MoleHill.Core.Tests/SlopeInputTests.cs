using System.Globalization;
using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class SlopeInputTests
{
    private const SlopeAnalyzer.SlopeUnit Percent = SlopeAnalyzer.SlopeUnit.Percent;
    private const SlopeAnalyzer.SlopeUnit Degrees = SlopeAnalyzer.SlopeUnit.Degrees;
    private const SlopeAnalyzer.SlopeUnit Promille = SlopeAnalyzer.SlopeUnit.Promille;
    private const SlopeAnalyzer.SlopeUnit Ratio = SlopeAnalyzer.SlopeUnit.Ratio;

    [Theory]
    [InlineData("25", 0.25)]
    [InlineData("25%", 0.25)]
    [InlineData("25 %", 0.25)]
    [InlineData("25percent", 0.25)]
    [InlineData("25 PCT", 0.25)]
    [InlineData("-5%", -0.05)]
    public void TryParse_PercentForms_ReturnsRatio(string text, double expected)
    {
        Assert.True(SlopeInput.TryParse(text, Percent, out double ratio));
        Assert.Equal(expected, ratio, 9);
    }

    // The keyboard-reachable spellings matter most: the two symbols that name these units best, promille
    // and degrees, are exactly the two a user cannot type without a character map.
    [Theory]
    [InlineData("50‰")]
    [InlineData("50 promille")]
    [InlineData("50 permille")]
    [InlineData("50 o/oo")]
    [InlineData("50prom")]
    [InlineData("50 prom")]
    [InlineData("50PROM")]
    [InlineData("50promil")]
    [InlineData("50permil")]
    [InlineData("50ppt")]
    public void TryParse_PromilleForms_OverrideDisplayUnit(string text)
    {
        Assert.True(SlopeInput.TryParse(text, Percent, out double ratio));
        Assert.Equal(0.05, ratio, 9);
    }

    [Theory]
    [InlineData("45°")]
    [InlineData("45 deg")]
    [InlineData("45 degrees")]
    [InlineData("45degree")]
    public void TryParse_DegreeForms_OverrideDisplayUnit(string text)
    {
        Assert.True(SlopeInput.TryParse(text, Percent, out double ratio));
        Assert.Equal(1.0, ratio, 6);
    }

    [Fact]
    public void TryParse_BareNumber_UsesDisplayUnit()
    {
        Assert.True(SlopeInput.TryParse("45", Degrees, out double asDegrees));
        Assert.Equal(1.0, asDegrees, 6);

        Assert.True(SlopeInput.TryParse("45", Promille, out double asPromille));
        Assert.Equal(0.045, asPromille, 9);
    }

    [Theory]
    [InlineData("4", 0.25)]
    [InlineData("3", 1.0 / 3.0)]
    [InlineData("0.5", 2.0)]
    [InlineData("0", 0.0)]
    public void TryParse_BareNumberUnderRatio_IsTheRunOfOneToN(string text, double expected)
    {
        // The command line has always read a Ratio-unit number as n of 1:n; the panel now agrees, so a
        // bare 4 is the 1:4 batter in both places rather than a 76° one in the panel.
        Assert.True(SlopeInput.TryParse(text, Ratio, out double ratio));
        Assert.Equal(expected, ratio, 9);
        Assert.True(SlopeInput.TryConvertDisplayNumber(double.Parse(text, CultureInfo.InvariantCulture), Ratio, out double commandLine));
        Assert.Equal(commandLine, ratio, 9);
    }

    [Fact]
    public void TryParse_WrittenRatioSuffix_IsRiseOverRun()
    {
        Assert.True(SlopeInput.TryParse("0.25 ratio", Percent, out double fromPercent));
        Assert.Equal(0.25, fromPercent, 9);
        Assert.True(SlopeInput.TryParse("0.25 ratio", Ratio, out double fromRatio));
        Assert.Equal(0.25, fromRatio, 9);
    }

    // 1:3 is vertical:horizontal — the flat batter, not the steep one. This is the plugin-wide reading
    // and matches OffsetVerticalMode.Ratio ("the run of 1:n") at the command line.
    [Theory]
    [InlineData("1:3", 1.0 / 3.0)]
    [InlineData("1 : 3", 1.0 / 3.0)]
    [InlineData("3:1", 3.0)]
    [InlineData("2:5", 0.4)]
    [InlineData("-1:4", -0.25)]
    public void TryParse_RatioPair_ReadsVerticalOverHorizontal(string text, double expected)
    {
        Assert.True(SlopeInput.TryParse(text, Percent, out double ratio));
        Assert.Equal(expected, ratio, 9);
    }

    [Theory]
    [InlineData("1v:3h")]
    [InlineData("3h:1v")]
    [InlineData("3h:1")]
    [InlineData("1:3h")]
    public void TryParse_RatioPairWithAxisLetters_ResolvesRegardlessOfOrder(string text)
    {
        Assert.True(SlopeInput.TryParse(text, Percent, out double ratio));
        Assert.Equal(1.0 / 3.0, ratio, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("steep")]
    [InlineData("1:0")]
    [InlineData("1:2:3")]
    [InlineData("90 deg")]
    [InlineData("-91 deg")]
    public void TryParse_UnusableText_Fails(string text)
    {
        Assert.False(SlopeInput.TryParse(text, Percent, out _));
    }

    [Theory]
    [InlineData(95.0)]
    [InlineData(-95.0)]
    [InlineData(90.0)]
    public void TryConvertDisplayNumber_AngleBeyondMaxSlope_Fails(double degrees)
    {
        // tan(95°) is negative: without the bound a command-line 95° became a falling slope.
        Assert.False(SlopeInput.TryConvertDisplayNumber(degrees, Degrees, out _));
    }

    [Theory]
    [InlineData(45.0, Degrees, 1.0)]
    [InlineData(25.0, Percent, 0.25)]
    [InlineData(50.0, Promille, 0.05)]
    [InlineData(4.0, Ratio, 0.25)]
    [InlineData(0.0, Ratio, 0.0)]
    public void TryConvertDisplayNumber_NumberInUnit_ReturnsRatio(double value, SlopeAnalyzer.SlopeUnit unit, double expected)
    {
        Assert.True(SlopeInput.TryConvertDisplayNumber(value, unit, out double ratio));
        Assert.Equal(expected, ratio, 9);
    }

    [Theory]
    [InlineData(Percent)]
    [InlineData(Promille)]
    [InlineData(Degrees)]
    [InlineData(Ratio)]
    public void ToDisplayNumber_ThenTryConvertDisplayNumber_RoundTrips(SlopeAnalyzer.SlopeUnit unit)
    {
        Assert.True(SlopeInput.TryConvertDisplayNumber(SlopeInput.ToDisplayNumber(0.4, unit), unit, out double ratio));
        Assert.Equal(0.4, ratio, 9);
    }

    [Fact]
    public void TryParseToDegrees_RatioPair_ConvertsToStoredAngle()
    {
        Assert.True(SlopeInput.TryParseToDegrees("1:1", Percent, out double degrees));
        Assert.Equal(45.0, degrees, 6);
    }

    [Fact]
    public void TryParseToDegrees_SlopeTooSteepForAnAngle_Fails()
    {
        // 100000% is 89.943°, past the angle clamp the grading batters accept.
        Assert.False(SlopeInput.TryParseToDegrees("100000%", Percent, out _));
    }

    [Theory]
    [InlineData(0.25, Percent, "25")]
    [InlineData(0.05, Promille, "50")]
    [InlineData(1.0, Degrees, "45")]
    [InlineData(1.0 / 3.0, Ratio, "1:3")]
    [InlineData(3.0, Ratio, "3:1")]
    [InlineData(0.0, Ratio, "0")]
    public void FormatValue_WritesTheNumberInTheDisplayUnit(double ratio, SlopeAnalyzer.SlopeUnit unit, string expected)
    {
        Assert.Equal(expected, SlopeInput.FormatValue(ratio, unit));
    }

    [Fact]
    public void FormatValue_RatioUnit_KeepsTheOneOnTheUnitSide()
    {
        // A flat batter reads 1:4, not 0.25:1 — that pairing is the whole reason to pick this unit.
        Assert.Equal("1:4", SlopeInput.FormatValue(0.25, Ratio));
        Assert.Equal("-1:4", SlopeInput.FormatValue(-0.25, Ratio));
    }

    [Theory]
    [InlineData(Percent)]
    [InlineData(Promille)]
    [InlineData(Degrees)]
    [InlineData(Ratio)]
    public void FormatValue_ThenTryParse_RoundTrips(SlopeAnalyzer.SlopeUnit unit)
    {
        const double ratio = 1.0 / 3.0;
        string text = SlopeInput.FormatValue(ratio, unit);

        Assert.True(SlopeInput.TryParse(text, unit, out double parsed));
        Assert.Equal(ratio, parsed, 3);
    }

    [Fact]
    public void TryParse_LocaleDecimalComma_ParsesWithoutGroupingConfusion()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nb-NO");
            Assert.True(SlopeInput.TryParse("12,5%", Percent, out double comma));
            Assert.Equal(0.125, comma, 9);

            // Text this code formatted under an invariant path must still read back as 12.5, not 125.
            Assert.True(SlopeInput.TryParse("12.5%", Percent, out double dot));
            Assert.Equal(0.125, dot, 9);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TryParse_AsciiSpellingsCoverEveryUnit_SoNoUnitNeedsACharacterMap()
    {
        // Guards the promise the tooltip makes: every unit is reachable from a plain keyboard.
        foreach (string text in new[] { "150prom", "150 promille", "15pct", "15 percent", "15deg", "15 degrees" })
            Assert.True(SlopeInput.TryParse(text, Percent, out _), text);

        Assert.True(SlopeInput.TryParse("150prom", Percent, out double prom));
        Assert.Equal(0.15, prom, 9);
    }

    [Fact]
    public void DegreesToUnit_AndBack_RoundTrips()
    {
        double percent = SlopeInput.DegreesToUnit(18.4349, Percent);
        Assert.Equal(33.333, percent, 2);
        Assert.Equal(18.4349, SlopeInput.UnitToDegrees(percent, Percent), 3);
    }
}
