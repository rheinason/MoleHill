using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// One line of text that never wraps and never widens its row: when it does not fit, it ends in an
/// ellipsis, and the full text is the tooltip. Card headers use it for the name and the type/summary line,
/// which is what keeps every collapsed header the same two lines at every panel width.
///
/// A <see cref="Label"/> cannot do this: it asks the layout for its full text width, so a long name pushes
/// the row (and the panel) wider instead of truncating. A drawable asks for nothing and paints to whatever
/// width the row gives it.
/// </summary>
internal sealed class TextLine : Drawable
{
    private const string Ellipsis = "…";

    private string _text;
    private readonly Font _font;
    private readonly Color _color;
    private readonly float _lineHeight;

    public TextLine(string text, Font font, Color color)
    {
        _text = text;
        _font = font;
        _color = color;
        // Measured, not Font.LineHeight: on WPF that reports ~12 px for a font Eto draws ~16 px tall,
        // which clipped the bottom of every line.
        _lineHeight = font.MeasureString("Ag").Height;
        Height = (int)Math.Ceiling(_lineHeight);
        ToolTip = text;
        Paint += (_, e) =>
        {
            string shown = Fit(_text, Width);
            if (shown.Length > 0)
                e.Graphics.DrawText(_font, _color, 0, (Height - _lineHeight) / 2f, shown);
        };
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            ToolTip = value;
            Invalidate();
        }
    }

    private string Fit(string text, float width)
    {
        if (width <= 0 || string.IsNullOrEmpty(text) || _font.MeasureString(text).Width <= width)
            return text;

        // Longest prefix that still fits with the ellipsis; binary search, since this runs on every resize.
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (_font.MeasureString(text[..mid].TrimEnd() + Ellipsis).Width <= width)
                low = mid;
            else
                high = mid - 1;
        }

        return low == 0 ? string.Empty : text[..low].TrimEnd() + Ellipsis;
    }
}
