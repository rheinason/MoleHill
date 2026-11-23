using System.Globalization;
using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class UserNumberTests
{
    // fo-FO writes "3,5" and groups thousands with a point: the locale where the old stepper read "3.5" as 35
    // or 0. Every case below must hold there and in en-US alike.
    public static TheoryData<string> Cultures => new() { "fo-FO", "en-US", "de-DE" };

    private static T InCulture<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void TryParse_PointDecimal_ReadsAsDecimalInEveryLocale(string culture)
    {
        Assert.Equal(3.5, InCulture(culture, () => UserNumber.TryParse("3.5", out double v) ? v : double.NaN));
        Assert.Equal(1.0, InCulture(culture, () => UserNumber.TryParse("1.000", out double v) ? v : double.NaN));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void TryParse_DecimalComma_IsADecimalInEveryLocale(string culture)
    {
        Assert.Equal(3.5, InCulture(culture, () => UserNumber.TryParse("3,5", out double v) ? v : double.NaN));
        Assert.Equal(-0.25, InCulture(culture, () => UserNumber.TryParse("-0,25", out double v) ? v : double.NaN));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void TryParse_NeverReadsThousands(string culture)
    {
        // "1,000" is one, not a thousand; "1.000,5" and "1,2,3" are not numbers.
        Assert.Equal(1.0, InCulture(culture, () => UserNumber.TryParse("1,000", out double v) ? v : double.NaN));
        Assert.False(InCulture(culture, () => UserNumber.TryParse("1.000,5", out _)));
        Assert.False(InCulture(culture, () => UserNumber.TryParse("1,2,3", out _)));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void TryParseLeading_DecimalCommaWithUnit_Reads(string culture) =>
        Assert.Equal(2.5, InCulture(culture, () => UserNumber.TryParseLeading("2,5 m", out double v) ? v : double.NaN));

    [Theory]
    [InlineData("2.5 m", 2.5)]
    [InlineData("2.5m", 2.5)]
    [InlineData("  7 ", 7.0)]
    [InlineData("-0.25 °", -0.25)]
    [InlineData("12%", 12.0)]
    public void TryParseLeading_IgnoresTheUnitAfterTheNumber(string text, double expected)
    {
        Assert.True(UserNumber.TryParseLeading(text, out double value));
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("m")]
    [InlineData("Auto")]
    public void TryParseLeading_NoLeadingNumber_Fails(string text) =>
        Assert.False(UserNumber.TryParseLeading(text, out _));

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Format_AlwaysWritesAPoint(string culture)
    {
        Assert.Equal("2.5", InCulture(culture, () => UserNumber.Format(2.5, 3)));
        Assert.Equal("2.500", InCulture(culture, () => UserNumber.Format(2.5, "0.000")));
    }

    [Theory]
    [InlineData("2.5", "m", "2.5 m")]
    [InlineData("25", "%", "25%")]
    [InlineData("14", "°", "14°")]
    [InlineData("3", null, "3")]
    public void WithUnit_SpacesWordsAndJoinsSymbols(string number, string? unit, string expected) =>
        Assert.Equal(expected, UserNumber.WithUnit(number, unit));

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Format_ThenParse_RoundTripsInEveryLocale(string culture)
    {
        double back = InCulture(culture, () =>
            UserNumber.TryParseLeading(UserNumber.WithUnit(UserNumber.Format(1234.5678, 3), "m"), out double v) ? v : double.NaN);
        Assert.Equal(1234.568, back, 9);
    }

    [Theory]
    [InlineData("1,234.5")]
    [InlineData("1,234,567")]
    [InlineData("1,234.5 m")]
    public void TryParseLeading_ThousandsSeparatedNumber_IsRefusedNotMisread(string text) =>
        Assert.False(UserNumber.TryParseLeading(text, out _));
}
