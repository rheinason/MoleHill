using System;
using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// A labelled numeric field you change by dragging across it, Blender-style: <c>Min  -120</c>.
///
/// A spinner would need a label, a box, and arrows to do what this does in one chip, and the ramp card has
/// four of these to fit beside a colour bar. Drag scrubs, alt-click resets to the value the field would
/// have if the user had never touched it, and a plain click (a drag that never moved) opens a text box for
/// an exact number — so the gesture is fast without making precise entry unreachable.
/// </summary>
internal sealed class ScrubField : Panel
{
    /// <summary>The full drag width maps to this many steps, so a field crosses its range in one sweep.</summary>
    private const int DragSteps = 300;

    /// <summary>Movement under this many pixels is a click, not a drag.</summary>
    private const int ClickSlop = 3;

    private readonly Drawable _display;
    private readonly string _label;
    private readonly Func<double, string> _format;
    private readonly Action<double, bool> _onChanged;
    private readonly Func<double> _resetValue;
    private readonly double? _min;
    private readonly double? _max;
    private readonly bool _integral;

    private double _value;
    private double _step = 0.01;
    private bool _dragging;
    private bool _moved;
    private float _dragOriginX;
    private double _dragOriginValue;

    public ScrubField(
        string label,
        double value,
        Func<double, string> format,
        Action<double, bool> onChanged,
        Func<double> resetValue,
        double? min = null,
        double? max = null,
        bool integral = false,
        string? help = null)
    {
        _label = label;
        _value = value;
        _format = format;
        _onChanged = onChanged;
        _resetValue = resetValue;
        _min = min;
        _max = max;
        _integral = integral;

        _display = new Drawable
        {
            Height = UiMetrics.CompactControlHeight,
            // Wide enough for a label plus a number at the narrowest supported dock width, and no wider:
            // two of these share a line, and an over-large minimum is what forces horizontal scroll.
            MinimumSize = new Size(UiMetrics.Chs(8), UiMetrics.CompactControlHeight),
            Cursor = Cursors.HorizontalSplit
        };
        _display.Paint += (_, e) => Paint(e.Graphics);
        _display.MouseDown += OnFieldMouseDown;
        _display.MouseMove += OnFieldMouseMove;
        _display.MouseUp += OnFieldMouseUp;

        if (!string.IsNullOrWhiteSpace(help))
            _display.ToolTip = help;

        Content = _display;
    }

    /// <summary>Distance one pixel of drag moves the value. Set from the range so a field always feels the
    /// same whether it spans metres or per-mille.</summary>
    public double Step
    {
        get => _step;
        set => _step = value > 0.0 && double.IsFinite(value) ? value : 0.01;
    }

    public void SetValue(double value)
    {
        if (Math.Abs(_value - value) < 1e-12)
            return;

        _value = value;
        _display.Invalidate();
    }

    /// <summary>Convenience for callers that size the step from a span: one sweep crosses the whole span.</summary>
    public static double StepForSpan(double span) =>
        double.IsFinite(span) && span > 0.0 ? span / DragSteps : 0.01;

    private void Paint(Graphics g)
    {
        int width = _display.Width;
        int height = _display.Height;
        if (width <= 0)
            return;

        g.FillRectangle(Enabled ? UiTheme.RampControl : UiTheme.RampSurface, 0, 0, width, height);

        // Outlined even when disabled. Auto-fit turns these into a readout, but a readout with no edges
        // dissolves into the card and stops looking like the thing you would turn auto-fit off to edit.
        g.DrawRectangle(Enabled ? UiTheme.RampControl : UiTheme.RampDivider, 0, 0, width - 1, height - 1);

        var font = SystemFonts.Default(SystemFonts.Default().Size - 1f);
        string valueText = _format(_value);

        SizeF valueSize = g.MeasureString(font, valueText);
        SizeF labelSize = g.MeasureString(font, _label);
        float top = (height - labelSize.Height) * 0.5f;

        // The value is right-aligned and always drawn; the label yields when the panel gets narrow, because
        // a truncated number is useless while a missing label is merely terse.
        float valueLeft = Math.Max(UiMetrics.SpaceXSmall, width - UiMetrics.SpaceSmall - valueSize.Width);
        if (labelSize.Width + valueSize.Width + UiMetrics.SpaceXLarge <= width)
            g.DrawText(font, Enabled ? UiTheme.RampControlText : UiTheme.MutedText, UiMetrics.SpaceSmall, top, _label);

        g.DrawText(font, Enabled ? UiTheme.RampControlText : UiTheme.MutedText, valueLeft, top, valueText);
    }

    private void OnFieldMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Buttons != MouseButtons.Primary || !Enabled)
            return;

        if (e.Modifiers.HasFlag(Keys.Alt))
        {
            Commit(_resetValue(), live: false);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _moved = false;
        _dragOriginX = e.Location.X;
        _dragOriginValue = _value;
        e.Handled = true;
    }

    private void OnFieldMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        float delta = e.Location.X - _dragOriginX;
        if (!_moved && Math.Abs(delta) < ClickSlop)
            return;

        _moved = true;
        Commit(_dragOriginValue + (delta * _step), live: true);
        e.Handled = true;
    }

    private void OnFieldMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        if (_moved)
        {
            // Close the gesture: one commit that is allowed to save and rebuild.
            _onChanged(_value, false);
        }
        else
        {
            PromptForValue();
        }

        e.Handled = true;
    }

    /// <summary>A click swaps the chip for a text box until it loses focus or takes Enter.</summary>
    private void PromptForValue()
    {
        var box = new TextBox
        {
            Text = _format(_value),
            Width = Math.Max(UiMetrics.Chs(8), _display.Width),
            Height = UiMetrics.CompactControlHeight
        };
        UiControls.StyleInput(box);

        bool closed = false;
        void Close(bool commit)
        {
            if (closed)
                return;

            closed = true;
            if (commit && double.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out double parsed))
                Commit(parsed, live: false);

            Content = _display;
            _display.Invalidate();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Enter)
            {
                Close(true);
                e.Handled = true;
            }
            else if (e.Key == Keys.Escape)
            {
                Close(false);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Close(true);

        Content = box;
        box.Focus();
        box.SelectAll();
    }

    private void Commit(double value, bool live)
    {
        double next = value;
        if (_integral)
            next = Math.Round(next);
        if (_min.HasValue)
            next = Math.Max(_min.Value, next);
        if (_max.HasValue)
            next = Math.Min(_max.Value, next);

        if (!double.IsFinite(next) || Math.Abs(next - _value) < 1e-12)
            return;

        _value = next;
        _display.Invalidate();
        _onChanged(next, live);
    }
}
