using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// A choice between two or three modes shown as side-by-side segments, the selected one filled (design
/// review: "segmented mode controls for two or three meaningful modes"). The schema picks it for a
/// fixed 2–3 option choice row; document selections and longer lists stay drop-downs.
///
/// Drawn, like <see cref="IconButton"/>: equal segments, labels ellipsized with <see cref="TextFit"/>,
/// hover and keyboard focus feedback, Left/Right to move the selection. The full option list is the tooltip.
/// </summary>
internal sealed class SegmentedControl : Drawable
{
    private readonly IReadOnlyList<string> _labels;
    private readonly IReadOnlyList<string?>? _help;
    private string? _rowToolTip;
    private readonly Font _font = SystemFonts.Default();

    // One step down for a narrow panel, as the reviewed segments are set (Step 47): tried before any
    // label is shortened, and applied to every segment so one control never mixes sizes.
    private readonly Font _compactFont = SystemFonts.Default(SystemFonts.Default().Size * UiMetrics.SegmentCompactScale);
    private int _selected;
    private int _hover = -1;

    public SegmentedControl(IReadOnlyList<string> labels, int selectedIndex, IReadOnlyList<string?>? help = null)
    {
        _labels = labels;
        _help = help;
        // -1 (a stored value no option matches) shows nothing selected, so every segment stays pickable.
        _selected = selectedIndex >= 0 && selectedIndex < labels.Count ? selectedIndex : -1;
        Height = UiMetrics.ControlHeight;
        CanFocus = true;
        Paint += (_, e) => Draw(e.Graphics);

        MouseMove += (_, e) => SetHover(SegmentAt(e.Location.X));
        MouseLeave += (_, _) => SetHover(-1);
        MouseDown += (_, e) =>
        {
            e.Handled = true;
            if (!Enabled || e.Buttons != MouseButtons.Primary)
                return;

            Focus();
            Select(SegmentAt(e.Location.X));
        };
        KeyDown += (_, e) =>
        {
            if (!Enabled)
                return;

            if (e.Key == Keys.Left || e.Key == Keys.Right)
            {
                Select(_selected + (e.Key == Keys.Right ? 1 : -1));
                e.Handled = true;
            }
        };
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
        EnabledChanged += (_, _) => Invalidate();
    }

    public int SelectedIndex => _selected;

    private bool _rowToolTipCaptured;

    /// <summary>Raised when the user picks a different segment.</summary>
    public event EventHandler? SelectedIndexChanged;

    private void Select(int index)
    {
        if (index < 0 || index >= _labels.Count || index == _selected)
            return;

        _selected = index;
        Invalidate();
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetHover(int index)
    {
        if (index == _hover)
            return;

        // The hovered segment's own help, when it has some, replaces the row's tooltip while over it.
        if (!_rowToolTipCaptured)
        {
            _rowToolTip = ToolTip;
            _rowToolTipCaptured = true;
        }

        _hover = index;
        string? segmentHelp = index >= 0 && _help != null && index < _help.Count ? _help[index] : null;
        ToolTip = segmentHelp ?? _rowToolTip;
        Invalidate();
    }

    private int SegmentAt(float x)
    {
        if (_labels.Count == 0 || Width <= 0)
            return -1;

        var (edges, _) = Layout();
        for (int i = 0; i < _labels.Count; i++)
        {
            if (x < edges[i + 1])
                return i;
        }

        return _labels.Count - 1;
    }

    /// <summary>
    /// Segment edges and the font to draw with. Each segment is as wide as its label plus an equal share of
    /// what is left, so labels of different lengths all fit where equal widths would cut the longest; when
    /// even that is too narrow the smaller font is tried, and only then are labels shortened, each by the
    /// same proportion.
    /// </summary>
    private (float[] Edges, Font Font) Layout()
    {
        int count = _labels.Count;
        float pad = 2 * UiMetrics.SpaceSmall;
        Font font = _font;
        float[] natural = _labels.Select(label => font.MeasureString(label).Width + pad).ToArray();
        if (natural.Sum() > Width)
        {
            font = _compactFont;
            natural = _labels.Select(label => font.MeasureString(label).Width + pad).ToArray();
        }

        // Spare room is shared equally; a shortfall is taken in proportion, so the longest label loses no
        // more of its text than the others do.
        float total = natural.Sum();
        float[] widths = total <= Width
            ? natural.Select(width => width + (Width - total) / count).ToArray()
            : natural.Select(width => width * Width / total).ToArray();

        var edges = new float[count + 1];
        for (int i = 0; i < count; i++)
            edges[i + 1] = edges[i] + widths[i];
        return (edges, font);
    }

    private void Draw(Graphics g)
    {
        int count = _labels.Count;
        if (count == 0)
            return;

        var (edges, font) = Layout();
        float lineHeight = TextFit.LineHeight(font);
        for (int i = 0; i < count; i++)
        {
            float segment = edges[i + 1] - edges[i];
            var box = new RectangleF(edges[i], 0, segment, Height);
            bool selected = i == _selected;
            Color fill = selected
                ? UiTheme.SegmentSelected
                : Enabled && i == _hover ? UiTheme.IconHover : UiTheme.SegmentBackground;
            g.FillRectangle(fill, box);

            string text = TextFit.Ellipsize(font, _labels[i], segment - 2 * UiMetrics.SpaceSmall);
            float textWidth = font.MeasureString(text).Width;
            Color ink = selected ? UiTheme.SegmentSelectedText : Enabled ? UiTheme.PrimaryText : UiTheme.MutedText;
            g.DrawText(font, ink, box.X + (segment - textWidth) / 2f, (Height - lineHeight) / 2f, text);
        }

        // Dividers between unselected neighbours, then the outer border, then focus on top.
        for (int i = 1; i < count; i++)
        {
            if (i != _selected && i - 1 != _selected)
                g.DrawLine(UiTheme.SegmentBorder, edges[i], 3, edges[i], Height - 3);
        }

        g.DrawRectangle(UiTheme.SegmentBorder, 0.5f, 0.5f, Width - 1f, Height - 1f);
        if (HasFocus)
            g.DrawRectangle(UiTheme.FocusRing, 1.5f, 1.5f, Width - 3f, Height - 3f);
    }
}
