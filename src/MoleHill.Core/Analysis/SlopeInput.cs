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
///   <item><description><c>0.333 ratio</c> — rise/run written out. A bare number while the field is
///     showing ratios is instead the run <c>n</c> of 1:n, so a bare <c>3</c> is 1:3 — what the field
///     displays and what a command-line slope option in Ratio units has always meant.</description></item>
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
    public static string FormatValue(double ratio, SlopeAnalyzer.SlopeUnit unit) =>
        FormatValue(ratio, unit, "0.###", CultureInfo.CurrentCulture);

    /// <summary>
    /// The same value text for a report rather than a field: a fixed number of decimals so a column of
    /// slopes lines up, and invariant culture so a file written on a comma-decimal machine still parses
    /// on every other one. Kept here rather than spelled out at the caller so slope has one format point,
    /// including the ratio pair — a report must read "1:3" for the same reason a field does.
    /// </summary>
    public static string FormatValueForReport(double ratio, SlopeAnalyzer.SlopeUnit unit, int decimals = 1) =>
        FormatValue(
            ratio,
            unit,
            "F" + decimals.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);

    private static string FormatValue(
        double ratio,
        SlopeAnalyzer.SlopeUnit unit,
        string numericFormat,
        IFormatProvider provider)
    {
        if (!double.IsFinite(ratio))
            return string.Empty;

        if (unit != SlopeAnalyzer.SlopeUnit.Ratio)
        {
            double converted = SlopeAnalyzer.ConvertRatioToUnit(ratio, unit);
            return converted.ToString(numericFormat, provider);
        }

        if (Math.Abs(ratio) <= 1e-9)
            return "0";

        // Keep the "1" on the side that is 1, so a flat batter reads 1:3 and a steep one 3:1 — writing
        // 0.333:1 for the common case would defeat the point of choosing this unit.
        double magnitude = Math.Abs(ratio);
        string sign = ratio < 0.0 ? "-" : string.Empty;
        return magnitude <= 1.0
            ? $"{sign}1:{(1.0 / magnitude).ToString(numericFormat, provider)}"
            : $"{sign}{magnitude.ToString(numericFormat, provider)}:1";
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

        SlopeAnalyzer.SlopeUnit? writtenUnit = null;
        foreach ((string token, SlopeAnalyzer.SlopeUnit tokenUnit) in UnitTokens)
        {
            if (!trimmed.EndsWith(token, StringComparison.OrdinalIgnoreCase))
                continue;

            writtenUnit = tokenUnit;
            trimmed = trimmed[..^token.Length].TrimEnd();
            break;
        }

        if (!TryParseNumber(trimmed, out double value))
            return false;

        // A bare number is read exactly as a command-line option reads it, so "4" under Ratio is 1:4 in
        // both places. Only a written "ratio" suffix takes the number as rise/run.
        if (writtenUnit is not SlopeAnalyzer.SlopeUnit unit)
            return TryConvertDisplayNumber(value, displayUnit, out ratio);

        if (unit == SlopeAnalyzer.SlopeUnit.Degrees && Math.Abs(value) > MaxSlopeDegrees)
            return false;

        ratio = SlopeAnalyzer.ConvertUnitToRatio(value, unit);
        return double.IsFinite(ratio);
    }

    /// <summary>
    /// A bare number in <paramref name="unit"/> to a slope ratio (rise/run) — the conversion behind a
    /// command-line slope option, which takes a number and shows its unit beside it. Under
    /// <see cref="SlopeAnalyzer.SlopeUnit.Ratio"/> the number is the run <c>n</c> of 1:n (0 is flat).
    /// Fails, as <see cref="TryParse"/> does, for an angle steeper than <see cref="MaxSlopeDegrees"/>:
    /// past 90° <c>tan</c> wraps round and would read 95° as a falling slope.
    /// </summary>
    public static bool TryConvertDisplayNumber(double value, SlopeAnalyzer.SlopeUnit unit, out double ratio)
    {
        ratio = 0.0;
        if (!double.IsFinite(value))
            return false;

        if (unit == SlopeAnalyzer.SlopeUnit.Degrees && Math.Abs(value) > MaxSlopeDegrees)
            return false;

        if (unit == SlopeAnalyzer.SlopeUnit.Ratio)
        {
            ratio = Math.Abs(value) <= 1e-12 ? 0.0 : 1.0 / value;
            return double.IsFinite(ratio);
        }

        ratio = SlopeAnalyzer.ConvertUnitToRatio(value, unit);
        return double.IsFinite(ratio);
    }

    /// <summary>Inverse of <see cref="TryConvertDisplayNumber"/>: the number a slope option shows in
    /// <paramref name="unit"/>, the run <c>n</c> of 1:n under Ratio.</summary>
    public static double ToDisplayNumber(double ratio, SlopeAnalyzer.SlopeUnit unit)
    {
        if (unit != SlopeAnalyzer.SlopeUnit.Ratio)
            return SlopeAnalyzer.ConvertRatioToUnit(ratio, unit);

        return Math.Abs(ratio) <= 1e-12 ? 0.0 : 1.0 / ratio;
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
