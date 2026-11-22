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
    private string _text;
    private readonly Font _font;
    private Color _color;
    private readonly float _lineHeight;

    public TextLine(string text, Font font, Color color)
    {
        _text = text;
        _font = font;
        _color = color;
        _lineHeight = TextFit.LineHeight(font);
        Height = (int)Math.Ceiling(_lineHeight);
        ToolTip = text;
        Paint += (_, e) =>
        {
            string shown = TextFit.Ellipsize(_font, _text, Width);
            if (shown.Length > 0)
                e.Graphics.DrawText(_font, _color, 0, (Height - _lineHeight) / 2f, shown);
        };
    }

    public Color TextColor
    {
        get => _color;
        set
        {
            _color = value;
            Invalidate();
        }
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
}
