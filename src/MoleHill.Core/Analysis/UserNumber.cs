using System.Globalization;

namespace MoleHill.Core.Analysis;

/// <summary>
/// The one reading and writing of a number a user types or sees, everywhere in MoleHill: panel fields,
/// command prompts, slope fields, labels. Numbers are written with a point, as Rhino writes them whatever
/// the Windows locale. Typed text is read the same way, but a decimal comma ("3,5") is understood on every
/// machine, so a comma-decimal habit, or the numpad decimal key on a fo-FO or de-DE keyboard, is never
/// silently misread.
///
/// Neither reading allows thousands separators. With them, "1.000" (a value this class wrote) would read
/// back as one thousand in a locale whose group separator is the point, and "3.5" as 35.
/// </summary>
public static class UserNumber
{
    private const NumberStyles Styles = NumberStyles.Float;

    /// <summary>Reads a whole string as a number: point decimal first, then the locale's decimal.</summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0.0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();
        return (double.TryParse(trimmed, Styles, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(trimmed, Styles, CultureInfo.CurrentCulture, out value) ||
                double.TryParse(DecimalComma(trimmed), Styles, CultureInfo.InvariantCulture, out value)) &&
               double.IsFinite(value);
    }

    /// <summary>
    /// A lone comma with no point is a decimal comma on any machine: "3,5" is 3.5 even where Windows writes
    /// a point, because that is what a comma-decimal hand (or a numpad decimal key on such a keyboard)
    /// types. Anything else is left alone, so "1,2,3" is still not a number.
    /// </summary>
    private static string DecimalComma(string text)
    {
        int comma = text.IndexOf(',');
        return comma >= 0 && comma == text.LastIndexOf(',') && !text.Contains('.')
            ? text.Replace(',', '.')
            : text;
    }

    /// <summary>
    /// Reads the number at the start of <paramref name="text"/> and ignores what follows, so a field that
    /// shows its unit ("2.5 m") reads back as 2.5 whether or not the user kept the unit.
    /// </summary>
    public static bool TryParseLeading(string? text, out double value)
    {
        value = 0.0;
        string trimmed = (text ?? string.Empty).Trim();
        for (int length = trimmed.Length; length > 0; length--)
        {
            if (TryParse(trimmed[..length], out value))
                return true;
        }

        return false;
    }

    /// <summary>Writes <paramref name="value"/> with a point decimal, using a .NET numeric format.</summary>
    public static string Format(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Writes <paramref name="value"/> with up to <paramref name="decimals"/> decimals, trailing zeros dropped.</summary>
    public static string Format(double value, int decimals) =>
        Format(value, decimals <= 0 ? "0" : "0." + new string('#', decimals));

    /// <summary>
    /// Appends a unit the way it is written after a number: a symbol unit (%, ‰, °) touches the number
    /// ("25%"), a word or abbreviation is spaced ("2.5 m").
    /// </summary>
    public static string WithUnit(string number, string? unit)
    {
        if (string.IsNullOrEmpty(unit))
            return number;

        return IsSymbolUnit(unit) ? number + unit : number + " " + unit;
    }

    private static bool IsSymbolUnit(string unit) => unit is "%" or "‰" or "°";
}
