using System;
using System.Globalization;

namespace MoleHill.Core.Analysis;

/// <summary>
/// The one place slope values are turned into text a user reads and back out of text a user types.
///
/// <para>Slope is stored as a ratio (rise/run) or, on the grading definitions, as an angle in degrees —
/// but a landscape architect writes it as a percent, a civil engineer as 1:3, a road engineer in
/// promille, and a mesh tool in degrees. Rather than pick one and make everybody else convert, every
/// slope field displays in the user's chosen unit (see the slope-unit preference) and accepts an
/// explicit unit typed into the value, exactly the way Rhino accepts "25mm" in a length field. A bare
/// number means "the unit currently shown".</para>
///
/// <para>Accepted forms — the expert path, discoverable through each field's tooltip:</para>
/// <list type="bullet">
///   <item><description><c>25%</c> / <c>25 percent</c> / <c>25pct</c></description></item>
///   <item><description><c>50‰</c> / <c>50prom</c> / <c>50 promille</c> / <c>50 permille</c> — ‰ needs a
///     character map, so <c>prom</c> is the one to reach for</description></item>
///   <item><description><c>14°</c> / <c>14 deg</c> / <c>14 degrees</c></description></item>
///   <item><description><c>1:3</c> — vertical:horizontal, so 1:3 is the flat one (33.3%, 18.4°).
///     <c>1v:3h</c> and <c>3h:1v</c> both work and say so explicitly.</description></item>
///   <item><description><c>0.333 ratio</c>, or a bare <c>0.333</c> when the field is showing ratios.</description></item>
/// </list>
///
/// <para>The vertical:horizontal reading of <c>a:b</c> is the plugin-wide convention: it is what
/// <c>OffsetVerticalMode.Ratio</c> ("the run of 1:n") already means at the command line, and it is the
/// dominant civil/landscape reading. A field echoes the parsed value back in the display unit as soon
/// as it commits, so a wrong guess corrects itself on the first try.</para>
/// </summary>
public static class SlopeInput
{
    /// <summary>Steepest slope expressible as an angle. Beyond this <c>tan</c> is useless, and the
    /// grading clamp (<c>GradingSlope.RatioFor</c>) would reject it anyway.</summary>
    public const double MaxSlopeDegrees = 89.9;

    /// <summary>One-line description of the accepted input forms, appended to slope field tooltips.</summary>
    public const string AcceptedFormatsHelp =
        "Type a plain number in the unit shown, or write the unit to override it: 25%, 150prom (or 150‰), "
        + "14deg (or 14°), or 1:3 (vertical:horizontal, so 1:3 is the flatter one).";

    /// <summary>
    /// Unit tokens accepted as a suffix, longest first so "degrees" wins over "deg" and "promille" over
    /// "prom". Every unit has at least one plain-ASCII spelling, because the two symbols that name these
    /// units best — ‰ and ° — are the two a user cannot type without a character map: promille takes
    /// <c>prom</c>, degrees takes <c>deg</c>.
    /// </summary>
    private static readonly (string Token, SlopeAnalyzer.SlopeUnit Unit)[] UnitTokens =
    {
        ("promille", SlopeAnalyzer.SlopeUnit.Promille),
        ("permille", SlopeAnalyzer.SlopeUnit.Promille),
        ("promil", SlopeAnalyzer.SlopeUnit.Promille),
        ("permil", SlopeAnalyzer.SlopeUnit.Promille),
        ("percent", SlopeAnalyzer.SlopeUnit.Percent),
        ("degrees", SlopeAnalyzer.SlopeUnit.Degrees),
        ("degree", SlopeAnalyzer.SlopeUnit.Degrees),
        ("ratio", SlopeAnalyzer.SlopeUnit.Ratio),
        ("prom", SlopeAnalyzer.SlopeUnit.Promille),
        ("deg", SlopeAnalyzer.SlopeUnit.Degrees),
        ("pct", SlopeAnalyzer.SlopeUnit.Percent),
        ("o/oo", SlopeAnalyzer.SlopeUnit.Promille),
        ("ppt", SlopeAnalyzer.SlopeUnit.Promille),
        ("‰", SlopeAnalyzer.SlopeUnit.Promille),
        ("°", SlopeAnalyzer.SlopeUnit.Degrees),
        ("%", SlopeAnalyzer.SlopeUnit.Percent),
    };

    /// <summary>Trailing label shown beside a slope field, naming the unit its number is in.</summary>
    public static string Suffix(SlopeAnalyzer.SlopeUnit unit) => unit switch
    {
        SlopeAnalyzer.SlopeUnit.Percent => "%",
        SlopeAnalyzer.SlopeUnit.Promille => "‰",
        SlopeAnalyzer.SlopeUnit.Degrees => "°",
        SlopeAnalyzer.SlopeUnit.Ratio => "V:H",
        _ => "%"
    };

    /// <summary>Long-form unit name, for prompts and dropdowns.</summary>
    public static string Name(SlopeAnalyzer.SlopeUnit unit) => unit switch
    {
        SlopeAnalyzer.SlopeUnit.Percent => "Percent",
        SlopeAnalyzer.SlopeUnit.Promille => "Promille",
        SlopeAnalyzer.SlopeUnit.Degrees => "Degrees",
        SlopeAnalyzer.SlopeUnit.Ratio => "Ratio",
        _ => "Percent"
    };

    /// <summary>Decimals a value in this unit is worth showing.</summary>
    public static int DecimalPlaces(SlopeAnalyzer.SlopeUnit unit) => unit switch
    {
        SlopeAnalyzer.SlopeUnit.Ratio => 3,
        _ => 1
    };

    /// <summary>
    /// The value text for a field in <paramref name="unit"/> — the number only, since the unit is shown
    /// as its own trailing label. The exception is <see cref="SlopeAnalyzer.SlopeUnit.Ratio"/>, which is
    /// written as the pair itself ("1:3") because that pair <em>is</em> how the unit is read.
    /// </summary>
    public static string FormatValue(double ratio, SlopeAnalyzer.SlopeUnit unit)
    {
        if (!double.IsFinite(ratio))
            return string.Empty;

        if (unit != SlopeAnalyzer.SlopeUnit.Ratio)
        {
            double converted = SlopeAnalyzer.ConvertRatioToUnit(ratio, unit);
            return converted.ToString("0.###", CultureInfo.CurrentCulture);
        }

        if (Math.Abs(ratio) <= 1e-9)
            return "0";

        // Keep the "1" on the side that is 1, so a flat batter reads 1:3 and a steep one 3:1 — writing
        // 0.333:1 for the common case would defeat the point of choosing this unit.
        double magnitude = Math.Abs(ratio);
        string sign = ratio < 0.0 ? "-" : string.Empty;
        return magnitude <= 1.0
            ? $"{sign}1:{(1.0 / magnitude).ToString("0.###", CultureInfo.CurrentCulture)}"
            : $"{sign}{magnitude.ToString("0.###", CultureInfo.CurrentCulture)}:1";
    }

    /// <summary>Value text plus its unit, for prompts and read-only summaries.</summary>
    public static string FormatWithUnit(double ratio, SlopeAnalyzer.SlopeUnit unit)
    {
        string value = FormatValue(ratio, unit);
        return unit == SlopeAnalyzer.SlopeUnit.Ratio ? value : value + Suffix(unit);
    }

    /// <summary>
    /// Parses user text to a slope ratio (rise/run). <paramref name="displayUnit"/> is what a bare
    /// number means; any unit written into the text overrides it.
    /// </summary>
    public static bool TryParse(string? text, SlopeAnalyzer.SlopeUnit displayUnit, out double ratio)
    {
        ratio = 0.0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();
        if (trimmed.Contains(':'))
            return TryParseRatioPair(trimmed, out ratio);

        SlopeAnalyzer.SlopeUnit unit = displayUnit;
        foreach ((string token, SlopeAnalyzer.SlopeUnit tokenUnit) in UnitTokens)
        {
            if (!trimmed.EndsWith(token, StringComparison.OrdinalIgnoreCase))
                continue;

            unit = tokenUnit;
            trimmed = trimmed[..^token.Length].TrimEnd();
            break;
        }

        if (!TryParseNumber(trimmed, out double value))
            return false;

        if (unit == SlopeAnalyzer.SlopeUnit.Degrees && Math.Abs(value) > MaxSlopeDegrees)
            return false;

        ratio = SlopeAnalyzer.ConvertUnitToRatio(value, unit);
        return double.IsFinite(ratio);
    }

    /// <summary>
    /// <see cref="TryParse"/> against a field whose stored value is an angle in degrees — the form the
    /// grading definitions persist, so displaying them in another unit needs no schema change.
    /// </summary>
    public static bool TryParseToDegrees(string? text, SlopeAnalyzer.SlopeUnit displayUnit, out double degrees)
    {
        degrees = 0.0;
        if (!TryParse(text, displayUnit, out double ratio))
            return false;

        degrees = SlopeAnalyzer.ConvertRatioToUnit(ratio, SlopeAnalyzer.SlopeUnit.Degrees);
        return double.IsFinite(degrees) && Math.Abs(degrees) <= MaxSlopeDegrees;
    }

    /// <summary>Field text for a slope stored as an angle in degrees, shown in <paramref name="unit"/>.</summary>
    public static string FormatDegreesAsUnit(double degrees, SlopeAnalyzer.SlopeUnit unit)
    {
        return FormatValue(SlopeAnalyzer.ConvertUnitToRatio(degrees, SlopeAnalyzer.SlopeUnit.Degrees), unit);
    }

    /// <summary>Converts a slope held in degrees into <paramref name="unit"/>, for a slider or stepper
    /// that needs the number rather than its text.</summary>
    public static double DegreesToUnit(double degrees, SlopeAnalyzer.SlopeUnit unit)
    {
        double ratio = SlopeAnalyzer.ConvertUnitToRatio(degrees, SlopeAnalyzer.SlopeUnit.Degrees);
        return SlopeAnalyzer.ConvertRatioToUnit(ratio, unit);
    }

    /// <summary>Inverse of <see cref="DegreesToUnit"/>.</summary>
    public static double UnitToDegrees(double value, SlopeAnalyzer.SlopeUnit unit)
    {
        double ratio = SlopeAnalyzer.ConvertUnitToRatio(value, unit);
        return SlopeAnalyzer.ConvertRatioToUnit(ratio, SlopeAnalyzer.SlopeUnit.Degrees);
    }

    private static bool TryParseRatioPair(string text, out double ratio)
    {
        ratio = 0.0;
        string[] parts = text.Split(':');
        if (parts.Length != 2)
            return false;

        if (!TryParseRatioTerm(parts[0], out double first, out char firstAxis) ||
            !TryParseRatioTerm(parts[1], out double second, out char secondAxis))
            return false;

        // Default reading is vertical:horizontal. An explicit v/h on either side wins, and one letter is
        // enough — "3h:1" is unambiguous once the h is there.
        double vertical = first;
        double horizontal = second;
        if (firstAxis == 'h' || secondAxis == 'v')
        {
            vertical = second;
            horizontal = first;
        }

        if (Math.Abs(horizontal) <= 1e-12)
            return false;

        ratio = vertical / horizontal;
        return double.IsFinite(ratio);
    }

    /// <summary>One side of an <c>a:b</c> pair, with an optional leading or trailing v/h axis letter.</summary>
    private static bool TryParseRatioTerm(string text, out double value, out char axis)
    {
        value = 0.0;
        axis = '\0';

        string term = text.Trim();
        if (term.Length == 0)
            return false;

        char last = char.ToLowerInvariant(term[^1]);
        char first = char.ToLowerInvariant(term[0]);
        if (last is 'v' or 'h')
        {
            axis = last;
            term = term[..^1].TrimEnd();
        }
        else if (first is 'v' or 'h')
        {
            axis = first;
            term = term[1..].TrimStart();
        }

        return TryParseNumber(term, out value);
    }

    /// <summary>
    /// Culture-tolerant number parse, matching the panel's other free-typed fields: no AllowThousands,
    /// so a '.' is always a decimal point even in locales where it is the group separator, and an
    /// invariant fallback catches text this code formatted itself.
    /// </summary>
    private static bool TryParseNumber(string text, out double value)
    {
        const NumberStyles Styles = NumberStyles.Float;
        return double.TryParse(text, Styles, CultureInfo.CurrentCulture, out value) ||
               double.TryParse(text, Styles, CultureInfo.InvariantCulture, out value);
    }
}
