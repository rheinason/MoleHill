using System.Linq;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// A primary control (a status label, a layer-path display) followed by a run of action buttons.
/// Keeps both on one line while there's room; once the actions no longer fit alongside the primary
/// control, they drop to their own line below (itself wrapping further via <see cref="AdaptiveControlGroup"/>
/// if it still doesn't fit alone) rather than clipping the primary control or forcing horizontal scroll.
/// </summary>
internal sealed class AdaptivePrimaryActionRow : Panel
{
    private readonly Control _primaryControl;
    private readonly AdaptiveControlGroup _actionsGroup;
    private readonly int _spacing;
    private bool? _stacked;

    public AdaptivePrimaryActionRow(Control primaryControl, int spacing, params Control[] actionControls)
    {
        _primaryControl = primaryControl;
        _spacing = spacing;
        _actionsGroup = new AdaptiveControlGroup(spacing, actionControls);
        Content = BuildHorizontal();
        SizeChanged += (_, _) => Relayout();
    }

    private void Relayout()
    {
        if (Width <= 0)
            return;

        bool stack = NeedsStack(Width);
        if (_stacked == stack)
            return;

        _stacked = stack;
        SuspendLayout();
        Content = null; // detach children before re-parenting into the new layout
        Content = stack ? BuildStacked() : BuildHorizontal();
        ResumeLayout();
    }

    private bool NeedsStack(int availableWidth)
    {
        var actionControls = _actionsGroup.ManagedControls.Where(c => c.Visible).ToList();
        if (actionControls.Count == 0)
            return false;

        int actionsWidth = actionControls.Sum(AdaptiveWidth.Estimate) + _spacing * (actionControls.Count - 1);
        int primaryMinWidth = UiMetrics.Chs(10);
        return primaryMinWidth + _spacing + actionsWidth > availableWidth;
    }

    private Control BuildHorizontal()
    {
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = _spacing,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(_primaryControl, expand: true),
                // Explicit VerticalAlignment.Center: an implicitly-added Panel-derived item (which
                // AdaptiveControlGroup is) stretches to the row's full height instead of inheriting
                // VerticalContentAlignment like a plain StackLayout does, then doesn't re-center its own
                // (shorter) content within that stretched height — leaving it a couple pixels high.
                new StackLayoutItem(_actionsGroup, VerticalAlignment.Center, expand: false)
            }
        };
    }

    private Control BuildStacked()
    {
        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(_primaryControl, HorizontalAlignment.Stretch),
                new StackLayoutItem(_actionsGroup, HorizontalAlignment.Stretch)
            }
        };
    }
}
