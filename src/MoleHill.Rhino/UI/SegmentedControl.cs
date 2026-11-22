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
    private int _selected;
    private int _hover = -1;

    public SegmentedControl(IReadOnlyList<string> labels, int selectedIndex, IReadOnlyList<string?>? help = null)
    {
        _labels = labels;
        _help = help;
        _selected = Math.Clamp(selectedIndex, 0, Math.Max(0, labels.Count - 1));
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
        _rowToolTip ??= ToolTip;
        _hover = index;
        string? segmentHelp = index >= 0 && _help != null && index < _help.Count ? _help[index] : null;
        ToolTip = segmentHelp ?? _rowToolTip;
        Invalidate();
    }

    private int SegmentAt(float x)
    {
        if (_labels.Count == 0 || Width <= 0)
            return -1;

        return Math.Clamp((int)(x / (Width / (float)_labels.Count)), 0, _labels.Count - 1);
    }

    private void Draw(Graphics g)
    {
        int count = _labels.Count;
        if (count == 0)
            return;

        float segment = Width / (float)count;
        float lineHeight = TextFit.LineHeight(_font);
        for (int i = 0; i < count; i++)
        {
            var box = new RectangleF(i * segment, 0, segment, Height);
            bool selected = i == _selected;
            Color fill = selected
                ? UiTheme.SegmentSelected
                : Enabled && i == _hover ? UiTheme.IconHover : UiTheme.SegmentBackground;
            g.FillRectangle(fill, box);

            string text = TextFit.Ellipsize(_font, _labels[i], segment - 2 * UiMetrics.SpaceSmall);
            float textWidth = _font.MeasureString(text).Width;
            Color ink = selected ? UiTheme.SegmentSelectedText : Enabled ? UiTheme.PrimaryText : UiTheme.MutedText;
            g.DrawText(_font, ink, box.X + (segment - textWidth) / 2f, (Height - lineHeight) / 2f, text);
        }

        // Dividers between unselected neighbours, then the outer border, then focus on top.
        for (int i = 1; i < count; i++)
        {
            if (i != _selected && i - 1 != _selected)
                g.DrawLine(UiTheme.SegmentBorder, i * segment, 3, i * segment, Height - 3);
        }

        g.DrawRectangle(UiTheme.SegmentBorder, 0.5f, 0.5f, Width - 1f, Height - 1f);
        if (HasFocus)
            g.DrawRectangle(UiTheme.FocusRing, 1.5f, 1.5f, Width - 3f, Height - 3f);
    }
}
