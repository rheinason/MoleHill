using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

internal sealed class PropertyRow : Panel
{
    private readonly Control _label;
    private readonly Control _widget;
    private readonly bool _expandWidget;
    private bool? _stacked;

    public PropertyRow(Control label, Control widget, bool expandWidget = false)
    {
        _label = label;
        _widget = widget;
        _expandWidget = expandWidget;
        SizeChanged += (_, _) => Apply(Width);
        Content = BuildSplit();
        _stacked = false;
    }

    private void Apply(int width)
    {
        if (width <= 0)
            return;

        bool stacked = _stacked == true
            ? width < UiMetrics.RowBreak + UiMetrics.Hysteresis
            : width < UiMetrics.RowBreak - UiMetrics.Hysteresis;
        if (_stacked == stacked)
            return;

        _stacked = stacked;
        SuspendLayout();
        Content = null;
        Content = stacked ? BuildStacked() : BuildSplit();
        ResumeLayout();
    }

    private Control BuildSplit()
    {
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 3),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(SizedLabel(UiMetrics.LabelColumn)),
                new StackLayoutItem(_widget, expand: _expandWidget)
            }
        };
    }

    private Control BuildStacked()
    {
        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Padding(0, 3),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(SizedLabel(0)),
                new StackLayoutItem(_widget, HorizontalAlignment.Stretch)
            }
        };
    }

    private Control SizedLabel(int width)
    {
        _label.Width = width > 0 ? width : -1;
        return _label;
    }
}
