using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// A horizontal run of controls (checkboxes, small buttons) that wraps onto additional rows instead of
/// clipping or forcing horizontal scroll once the available width can't fit everything on one line —
/// the multi-control counterpart to <see cref="PropertyRow"/>'s single label+widget split.
/// </summary>
internal sealed class AdaptiveControlGroup : Panel
{
    private readonly Control[] _controls;
    private readonly int _spacing;
    private bool? _wrapped;

    /// <summary>The controls this group is laying out, exposed so a containing row (e.g.
    /// <see cref="AdaptivePrimaryActionRow"/>) can factor their width into its own layout decision.</summary>
    public Control[] ManagedControls => _controls;

    public AdaptiveControlGroup(int spacing, params Control[] controls)
    {
        _spacing = spacing;
        _controls = controls;
        Content = BuildSingleRow();
        SizeChanged += (_, _) => Relayout();
    }

    private void Relayout()
    {
        if (Width <= 0)
            return;

        bool wrap = NeedsWrap(Width);
        if (_wrapped == wrap)
            return;

        _wrapped = wrap;
        SuspendLayout();
        Content = null; // detach children before re-parenting into the new layout
        Content = wrap ? BuildWrapped(Width) : BuildSingleRow();
        ResumeLayout();
    }

    private bool NeedsWrap(int availableWidth)
    {
        var visible = VisibleControls();
        if (visible.Count == 0)
            return false;

        int total = visible.Sum(AdaptiveWidth.Estimate) + _spacing * (visible.Count - 1);
        // No safety margin here: a wrapped single-line group (BuildWrapped's outer vertical StackLayout,
        // even with exactly one inner line) reports a taller PreferredSize than the unwrapped row does
        // (observed +4px from Eto's own vertical Spacing bookkeeping), which the parent then honors as the
        // group's new allocated width/height — a margin here nudges genuinely-fitting content (e.g. two
        // 34px icons in an 80px slot) into wrapping once, and the inflated wrapped size never triggers a
        // re-check that would unwrap it again.
        return total > availableWidth;
    }

    /// <summary>Invisible controls (e.g. a conditionally-shown button) still enumerate here, but they
    /// occupy no space once laid out — counting them toward the wrap width would wrap prematurely.</summary>
    private System.Collections.Generic.List<Control> VisibleControls() => _controls.Where(c => c.Visible).ToList();

    private Control BuildSingleRow()
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = _spacing,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        foreach (var control in _controls)
            row.Items.Add(control);

        return row;
    }

    /// <summary>Greedy line-fill: add controls left to right, starting a new line whenever the next one
    /// wouldn't fit in the remaining width of the current line.</summary>
    private Control BuildWrapped(int availableWidth)
    {
        var lines = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        StackLayout? currentLine = null;
        int currentLineWidth = 0;

        foreach (var control in _controls)
        {
            if (!control.Visible)
            {
                // Still needs a parent, or Eto keeps it attached to whatever container held it before.
                (currentLine ?? lines).Items.Add(control);
                continue;
            }

            int controlWidth = AdaptiveWidth.Estimate(control);
            bool startNewLine = currentLine == null || currentLineWidth + _spacing + controlWidth > availableWidth;
            if (startNewLine)
            {
                currentLine = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = _spacing,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                lines.Items.Add(new StackLayoutItem(currentLine, HorizontalAlignment.Left));
                currentLineWidth = 0;
            }

            currentLine!.Items.Add(control);
            currentLineWidth += (currentLineWidth > 0 ? _spacing : 0) + controlWidth;
        }

        return lines;
    }
}

/// <summary>
/// Rough pixel-width estimate for common toolbar controls, used to decide when a control group needs to
/// wrap. Deliberately avoids <c>GetPreferredSize()</c>, which isn't reliable before a control is loaded
/// into a window (exactly when a resize-driven relayout first needs an answer).
/// </summary>
internal static class AdaptiveWidth
{
    public static int Estimate(Control control) => control switch
    {
        // Take whichever is larger of an explicit Width and the text-length guess: a control that's been
        // given a fixed Width (e.g. the layer-assignment button trio) should never be estimated smaller
        // than that, and one that's merely reporting its default/unmeasured Width shouldn't be trusted
        // below what its text needs.
        Button button => Math.Max(button.Width, UiMetrics.Chs((button.Text?.Length ?? 0) + 3)),
        CheckBox check => Math.Max(check.Width, UiMetrics.Chs((check.Text?.Length ?? 0) + 4)),
        _ when control.Width > 0 => control.Width,
        _ => UiMetrics.Chs(10)
    };
}

/// <summary>
/// Equal-width compact controls share a line while useful, then become a vertical stack. This is
/// intentionally separate from <see cref="AdaptiveControlGroup"/>, whose children keep intrinsic widths.
/// </summary>
internal sealed class AdaptiveColumns : Panel
{
    private readonly Control[] _controls;
    private readonly int _spacing;
    private readonly int _minimumColumnWidth;
    private bool? _stacked;

    public AdaptiveColumns(int spacing, int minimumColumnWidth, params Control[] controls)
    {
        _controls = controls;
        _spacing = spacing;
        _minimumColumnWidth = minimumColumnWidth;
        Content = BuildHorizontal();
        SizeChanged += (_, _) => Relayout();
    }

    private void Relayout()
    {
        if (Width <= 0)
            return;

        bool stacked = Width < _minimumColumnWidth * _controls.Length + _spacing * (_controls.Length - 1);
        if (_stacked == stacked)
            return;

        _stacked = stacked;
        SuspendLayout();
        Content = null;
        Content = stacked ? BuildVertical() : BuildHorizontal();
        ResumeLayout();
    }

    private Control BuildHorizontal()
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = _spacing,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        foreach (var control in _controls)
            row.Items.Add(new StackLayoutItem(control, expand: true));
        return row;
    }

    private Control BuildVertical()
    {
        var stack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = _spacing,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        foreach (var control in _controls)
            stack.Items.Add(new StackLayoutItem(control, HorizontalAlignment.Stretch));
        return stack;
    }
}
