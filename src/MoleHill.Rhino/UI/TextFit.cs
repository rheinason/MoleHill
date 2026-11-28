using Eto.Drawing;

namespace MoleHill.Rhino.UI;

/// <summary>
/// Shortens text to a width with a trailing ellipsis, for controls that draw their own text
/// (<see cref="TextLine"/>, <see cref="SegmentedControl"/>), so every drawn label truncates alike.
/// </summary>
internal static class TextFit
{
    private const string Ellipsis = "…";

    public static string Ellipsize(Font font, string text, float width)
    {
        if (width <= 0 || string.IsNullOrEmpty(text) || font.MeasureString(text).Width <= width)
            return text;

        // Longest prefix that still fits with the ellipsis; binary search, since this runs on every resize.
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (font.MeasureString(text[..mid].TrimEnd() + Ellipsis).Width <= width)
                low = mid;
            else
                high = mid - 1;
        }

        return low == 0 ? string.Empty : text[..low].TrimEnd() + Ellipsis;
    }

    /// <summary>The drawn height of one line of <paramref name="font"/>. Measured: on WPF
    /// <c>Font.LineHeight</c> reports ~12 px for text Eto draws ~16 px tall.</summary>
    public static float LineHeight(Font font) => font.MeasureString("Ag").Height;
}
