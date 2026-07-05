using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

internal sealed class PropertyRow : Panel
{
    private readonly Control _label;
    private readonly Control _widget;
    private readonly bool _expandWidget;

    public PropertyRow(Control label, Control widget, bool expandWidget = false)
    {
        _label = label;
        _widget = widget;
        _expandWidget = expandWidget;
        Content = BuildSplit();
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

    private Control SizedLabel(int width)
    {
        _label.Width = width > 0 ? width : -1;
        return _label;
    }
}
