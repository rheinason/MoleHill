using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The ramp strip: a histogram of the analysed values, the ramp itself, and — when the card is expanded —
/// a draggable handle per stop.
///
/// This is the one place the card paints, and it paints through <see cref="AnalysisColorMapper"/> rather
/// than interpolating colours of its own, so the strip and the terrain mesh cannot disagree about what a
/// value looks like. Collapsed it is a legend; expanded it is the instrument. Same pixels, same code.
/// </summary>
internal sealed class ColorRampBar : Drawable
{
    /// <summary>Height of the histogram strip above the bar. Zero when there is no distribution to show.</summary>
    private const int HistogramHeight = UiMetrics.CompactControlHeight;

    /// <summary>
    /// Gap between the histogram and the ramp. The histogram is drawn in the ramp's own colours — that is
    /// what shows which part of the data each colour is being spent on — but without this gap the two
    /// merge into a single ambiguous colour field and the ramp stops reading as a ramp.
    /// </summary>
    private const int HistogramGap = UiMetrics.SpaceSmall;

    /// <summary>Bar height when collapsed — a legend, not a target.</summary>
    private const int CollapsedBarHeight = UiMetrics.CompactControlHeight;

    /// <summary>Bar height when expanded, tall enough that a stop line is unambiguous.</summary>
    private const int ExpandedBarHeight = UiMetrics.ControlHeight;

    /// <summary>The colour grip below the bar that a stop is actually dragged by.</summary>
    private const int GripHeight = UiMetrics.SpaceXLarge;

    private const int GripWidth = UiMetrics.SpaceXLarge;

    /// <summary>Half-width of a handle's hit zone. Generous: these are small targets in a docked panel.</summary>
    private const int HandleHitHalfWidth = UiMetrics.SpaceLarge;

    private ColorRamp _ramp;
    private AnalysisRange _range;
    private AnalysisColorMapper.Mode _mode;
    private double _interval;
    private double[]? _histogram;
    private bool _expanded;
    private int _selectedIndex;
    private int _draggingIndex = -1;

    public ColorRampBar()
    {
        _ramp = ColorRampPresets.ResolveRamp(null);
        _range = AnalysisRange.Unit;
        UpdateHeight();
    }

    /// <summary>
    /// Raised continuously while a stop is dragged, so the terrain recolours under the cursor. The bool is
    /// true for the in-flight updates and false for the single commit on release.
    /// </summary>
    public event EventHandler<(ColorRamp Ramp, bool Live)>? RampChanged;

    /// <summary>Raised when the selected stop changes, so the stop table can follow.</summary>
    public event EventHandler<int>? SelectionChanged;

    /// <summary>Raised when the collapsed bar is clicked — the card's "click the ramp to edit" gesture.</summary>
    public event EventHandler? EditRequested;

    public int SelectedIndex => _selectedIndex;

    public void Update(
        ColorRamp ramp,
        AnalysisRange range,
        AnalysisColorMapper.Mode mode,
        double interval,
        double[]? histogram,
        bool expanded,
        int selectedIndex)
    {
        // Mid-drag the bar owns the ramp, and the caller's copy is a frame behind: refreshing the stop
        // table re-enters here with the ramp as it was before this move, which would throw away the very
        // motion being made. Everything else (range, mode, histogram) is still safe to take.
        if (_draggingIndex < 0)
        {
            _ramp = ramp;
            _selectedIndex = Math.Clamp(selectedIndex, 0, ramp.Count - 1);
        }

        _range = range.EnsureNonDegenerate();
        _mode = mode;
        _interval = interval;
        _histogram = histogram;
        _expanded = expanded;

        UpdateHeight();
        Invalidate();
    }

    private int BarHeight => _expanded ? ExpandedBarHeight : CollapsedBarHeight;

    private int HistogramBand => _histogram is { Length: > 0 } ? HistogramHeight + HistogramGap : 0;

    private int BarTop => HistogramBand;

    private void UpdateHeight()
    {
        // The grip hangs below the bar, so the expanded control has to reserve room for it or the stop a
        // user is dragging gets clipped by the next row.
        int height = HistogramBand + BarHeight + (_expanded ? GripHeight + UiMetrics.SpaceXSmall : 0);
        Height = height;
        MinimumSize = new Size(0, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        int width = Width;
        if (width <= 0)
            return;

        var g = e.Graphics;
        PaintHistogram(g, width);
        PaintBar(g, width);

        if (_expanded)
            PaintHandles(g, width);
    }

    /// <summary>
    /// The distribution, drawn in the ramp's own colours at low opacity. Colouring the bars by where they
    /// fall on the ramp — rather than a flat grey — is what makes moving Min/Max legible: you can see which
    /// part of the data each colour is currently being spent on.
    /// </summary>
    private void PaintHistogram(Graphics g, int width)
    {
        if (_histogram is not { Length: > 0 } bars)
            return;

        float barWidth = width / (float)bars.Length;
        for (int i = 0; i < bars.Length; i++)
        {
            double normalized = Math.Clamp(bars[i], 0.0, 1.0);
            float height = (float)(2.0 + (normalized * (HistogramHeight - 4)));
            double centre = (i + 0.5) / bars.Length;
            var mapped = _mode == AnalysisColorMapper.Mode.Constant
                ? AnalysisColorMapper.SampleConstant(centre, _ramp.Stops)
                : _ramp.Sample(centre);
            // Muted, so the distribution reads as context behind the ramp rather than as a second ramp.
            var colour = Color.FromArgb(mapped.R, mapped.G, mapped.B, 120);
            g.FillRectangle(colour, i * barWidth, HistogramHeight - height, Math.Max(1f, barWidth - 0.5f), height);
        }
    }

    private void PaintBar(Graphics g, int width)
    {
        int top = BarTop;
        int height = BarHeight;

        if (_mode == AnalysisColorMapper.Mode.Stepped)
        {
            // Each band is a flat swatch sized by its share of the range, so an interval that does not
            // divide the span shows its short last band honestly rather than pretending to be even.
            var bands = AnalysisColorMapper.ResolveBands(_range, _interval, _ramp.Stops);
            for (int i = 0; i < bands.Count; i++)
            {
                var band = bands[i];
                float x0 = (float)_range.Normalize(band.Low) * width;
                float x1 = (float)_range.Normalize(band.High) * width;
                var colour = Color.FromArgb(band.Color.R, band.Color.G, band.Color.B);
                g.FillRectangle(colour, x0, top, Math.Max(1f, x1 - x0), height);
            }
        }
        else
        {
            const int steps = 256;
            bool constant = _mode == AnalysisColorMapper.Mode.Constant;
            for (int i = 0; i < steps; i++)
            {
                double t = i / (double)(steps - 1);
                var mapped = constant
                    ? AnalysisColorMapper.SampleConstant(t, _ramp.Stops)
                    : _ramp.Sample(t);
                float x0 = (float)i / steps * width;
                float x1 = (float)(i + 1) / steps * width;
                var colour = Color.FromArgb(mapped.R, mapped.G, mapped.B);
                g.FillRectangle(colour, x0, top, Math.Max(1f, x1 - x0), height);
            }
        }

        g.DrawRectangle(UiTheme.RampSwatchOutline, 0f, top, width - 1f, height - 1f);
    }

    private void PaintHandles(Graphics g, int width)
    {
        int top = BarTop;
        int gripTop = top + BarHeight + UiMetrics.SpaceXSmall;

        for (int i = 0; i < _ramp.Count; i++)
        {
            var stop = _ramp[i];
            bool selected = i == _selectedIndex;
            float x = (float)stop.Position * (width - 1);

            g.FillRectangle(
                selected ? UiTheme.RampStopLine : UiTheme.RampStopLineInactive,
                x - 1f, top, 2f, BarHeight);

            float gripLeft = Math.Clamp(x - (GripWidth * 0.5f), 0f, Math.Max(0f, width - GripWidth));
            g.FillRectangle(Color.FromArgb(stop.R, stop.G, stop.B), gripLeft, gripTop, GripWidth, GripHeight);
            // Selected grips get a light ring, unselected a dark one, so the selection survives whatever
            // colour the stop happens to be.
            g.DrawRectangle(
                selected ? UiTheme.RampStopLine : UiTheme.RampSwatchOutline,
                gripLeft, gripTop, GripWidth - 1f, GripHeight - 1f);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Buttons != MouseButtons.Primary || Width <= 0)
        {
            base.OnMouseDown(e);
            return;
        }

        if (!_expanded)
        {
            // Collapsed, the whole strip is one button: "let me edit this".
            EditRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        int hit = HitTestHandle(e.Location.X);
        if (hit >= 0)
        {
            _draggingIndex = hit;
            SetMouseCapture(true);
            Select(hit);
        }
        else
        {
            // A click on the bar itself selects the nearest stop rather than doing nothing; it is the
            // fastest way to aim the stop table at the colour you can see.
            Select(_ramp.IndexNearest(PositionAt(e.Location.X)));
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingIndex < 0)
        {
            base.OnMouseMove(e);
            return;
        }

        double position = PositionAt(e.Location.X);
        var moved = _ramp.WithStopAt(_draggingIndex, position);

        // Stops are re-sorted on every change, so the dragged stop's index can move out from under us as it
        // passes a neighbour. Re-find it by position and keep dragging the same stop the user grabbed.
        _ramp = moved;
        _draggingIndex = moved.IndexNearest(position);
        _selectedIndex = _draggingIndex;

        Invalidate();

        // Ramp first, selection second. The selection handler refreshes the stop table, which reads the
        // ramp back from the card — so the card has to have been told about this move before it does.
        RampChanged?.Invoke(this, (_ramp, true));
        SelectionChanged?.Invoke(this, _selectedIndex);
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_draggingIndex >= 0)
        {
            _draggingIndex = -1;
            SetMouseCapture(false);
            // One real commit for the whole gesture: this is the call that saves the document and lets
            // the card rebuild, now that there is no drag left to destroy.
            RampChanged?.Invoke(this, (_ramp, false));
            e.Handled = true;
            return;
        }

        base.OnMouseUp(e);
    }

    private void Select(int index)
    {
        if (index == _selectedIndex)
            return;

        _selectedIndex = index;
        Invalidate();
        SelectionChanged?.Invoke(this, index);
    }

    private double PositionAt(float x) => Math.Clamp(x / Math.Max(1.0, Width - 1.0), 0.0, 1.0);

    /// <summary>
    /// Holds the pointer for the duration of a stop drag.
    ///
    /// Eto has no capture API, and neither WPF nor WinForms captures on its own, so a drag would otherwise
    /// stop the instant the cursor left this strip — which, at 60-odd pixels tall in a narrow dock, is
    /// almost immediately. The native control is reached reflectively because this assembly deliberately
    /// references neither UI framework; if the shape is not what we expect, we simply do not capture and
    /// the drag degrades to "works while the cursor stays on the bar".
    /// </summary>
    private void SetMouseCapture(bool capture)
    {
        try
        {
            object? native = ControlObject;
            if (native == null)
                return;

            var type = native.GetType();

            // WPF: CaptureMouse() / ReleaseMouseCapture().
            var method = type.GetMethod(capture ? "CaptureMouse" : "ReleaseMouseCapture", Type.EmptyTypes);
            if (method != null)
            {
                method.Invoke(native, null);
                return;
            }

            // WinForms: a settable Capture property.
            var property = type.GetProperty("Capture");
            if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                property.SetValue(native, capture);
        }
        catch
        {
            // Capture is an enhancement, never a requirement. Losing it must not break the gesture.
        }
    }

    /// <summary>
    /// The stop under the cursor, or -1. Ties go to the nearer one, so two stops dragged close together
    /// stay individually grabbable instead of the lower index always winning.
    /// </summary>
    private int HitTestHandle(float x)
    {
        int best = -1;
        float bestDistance = HandleHitHalfWidth;
        for (int i = 0; i < _ramp.Count; i++)
        {
            float handleX = (float)_ramp[i].Position * (Width - 1);
            float distance = Math.Abs(handleX - x);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Round tick values inside a range: five or so, on a 1/2/5/10 step. Shared with the card's
    /// tick row, which is why it lives here rather than in the panel.</summary>
    public static IReadOnlyList<double> BuildTicks(AnalysisRange range)
    {
        AnalysisRange safe = range.EnsureNonDegenerate();
        double step = AnalysisRange.NiceStep(safe.Span / 4.0);
        var ticks = new List<double>();
        double first = Math.Ceiling(safe.Low / step) * step;
        for (double value = first; value <= safe.High + (step * 1e-6); value += step)
        {
            // -0 prints as "-0"; fold it onto zero.
            ticks.Add(Math.Abs(value) < step * 1e-9 ? 0.0 : value);
            if (ticks.Count > 12)
                break;
        }

        if (ticks.Count == 0)
            ticks.Add(safe.Low);

        return ticks;
    }
}
