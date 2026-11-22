using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

internal enum UiButtonRole
{
    Regular,
    Toolbar,
    Inline,

    Pill
}

internal enum UiLabelRole
{
    Body,
    Meta,
    Section,
    Warning,
    Input
}

/// <summary>
/// MoleHill's native-first Eto control vocabulary. Roles express why a control exists; this class
/// owns the dimensions and small theme adjustments needed to make equivalent controls look alike.
/// </summary>
internal static class UiControls
{
    /// <summary>
    /// Horizontal scrolling is never an acceptable fallback in MoleHill UI. Eto does not expose the
    /// native scrollbar visibility, so disable it through the Windows handler when available; layouts
    /// must still size/reflow their content to the client width.
    /// </summary>
    public static void DisableHorizontalScrolling(Scrollable scrollable)
    {
        void Apply()
        {
            object? native = scrollable.ControlObject;
            var property = native?.GetType().GetProperty("HorizontalScrollBarVisibility");
            if (property == null || !property.CanWrite || !property.PropertyType.IsEnum)
                return;

            object disabled = Enum.Parse(property.PropertyType, "Disabled");
            property.SetValue(native, disabled);
            if (scrollable.ScrollPosition.X != 0)
                scrollable.ScrollPosition = new Point(0, scrollable.ScrollPosition.Y);
        }

        scrollable.Shown += (_, _) => Apply();
        Apply();
    }

    /// <summary>
    /// Lays a scrollable's content out at the scrollable's client width. <c>ExpandContentWidth</c> is not
    /// enough on the WPF backend: it measures before reserving the vertical scrollbar, and it leaves out the
    /// content's own padding, so either one leaves a few pixels to scroll sideways (found live in the panel).
    /// <c>ClientSize</c> already excludes a visible scrollbar. Content that grows past the fold brings the
    /// scrollbar in without resizing the scrollable, so a content resize re-syncs too. That cannot toggle the
    /// bar: content only narrows while it is taller than the view, which narrowing keeps it, and only widens
    /// once it fits, which widening keeps it.
    /// <para>Not for a dialog's own content: in the scan dialog this kept the window's Shown event from ever
    /// firing, so its preview never started (found live); the dialog moves its padding onto the scrollable
    /// instead.</para>
    /// </summary>
    public static void FitContentToWidth(Scrollable scrollable)
    {
        Control? content = scrollable.Content;
        if (content == null)
            return;

        scrollable.ExpandContentWidth = false;
        DisableHorizontalScrolling(scrollable);

        void Sync()
        {
            int width = scrollable.ClientSize.Width;
            if (width > 0 && content.Width != width)
                content.Width = width;
        }

        scrollable.SizeChanged += (_, _) => Sync();
        scrollable.Shown += (_, _) => Sync();
        content.SizeChanged += (_, _) => Application.Instance.AsyncInvoke(() =>
        {
            if (!scrollable.IsDisposed)
                Sync();
        });
    }

    public static Button Button(
        string text,
        EventHandler<EventArgs>? onClick,
        string? toolTip = null,
        UiButtonRole role = UiButtonRole.Regular)
    {
        var button = new Button { Text = text };
        Apply(button, role);
        ApplyHelp(button, toolTip);
        if (onClick != null)
            button.Click += onClick;
        return button;
    }

    public static IconButton IconButton(
        PanelButtonIcon icon,
        EventHandler<EventArgs> onClick,
        string? toolTip = null,
        bool muted = false)
    {
        var button = new IconButton();
        PanelButtonIcons.Apply(button, icon, muted);
        if (!string.IsNullOrWhiteSpace(toolTip))
            button.ToolTip = toolTip;
        button.Click += onClick;
        return button;
    }

    public static void Apply(Button button, UiButtonRole role)
    {
        switch (role)
        {
            case UiButtonRole.Regular:
            case UiButtonRole.Toolbar:
                button.Height = UiMetrics.ControlHeight;
                button.MinimumSize = new Size(0, UiMetrics.ControlHeight);
                break;
            case UiButtonRole.Inline:
                button.Height = UiMetrics.CompactControlHeight;
                button.MinimumSize = new Size(0, UiMetrics.CompactControlHeight);
                break;
            case UiButtonRole.Pill:
                button.Height = UiMetrics.CompactControlHeight;
                button.MinimumSize = new Size(0, UiMetrics.CompactControlHeight);
                button.BackgroundColor = UiTheme.PillBackground;
                button.TextColor = UiTheme.InputText;
                break;
        }
    }

    public static Label Label(
        string text,
        UiLabelRole role = UiLabelRole.Body,
        WrapMode wrap = WrapMode.None)
    {
        var label = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = wrap
        };

        switch (role)
        {
            case UiLabelRole.Meta:
                label.TextColor = UiTheme.MutedText;
                break;
            case UiLabelRole.Section:
                label.TextColor = UiTheme.MutedText;
                label.Font = new Font(SystemFont.Bold);
                break;
            case UiLabelRole.Warning:
                label.TextColor = UiTheme.WarningText;
                break;
            case UiLabelRole.Input:
                label.TextColor = UiTheme.InputText;
                break;
        }

        return label;
    }

    public static Label HelpLabel(string text, string help, int width = 0)
    {
        var label = Label(text, wrap: WrapMode.Word);
        if (width > 0)
            label.Width = width;
        label.ToolTip = help;
        return label;
    }

    public static void StyleInput(ComboBox comboBox)
    {
        comboBox.BackgroundColor = UiTheme.InputBackground;
        comboBox.TextColor = UiTheme.InputText;
    }

    public static void StyleInput(TextArea textArea)
    {
        textArea.BackgroundColor = UiTheme.InputBackground;
        textArea.TextColor = UiTheme.InputText;
    }

    public static void StyleInput(TextBox textBox)
    {
        textBox.BackgroundColor = UiTheme.InputBackground;
        textBox.TextColor = UiTheme.InputText;
    }

    public static void StyleInput(NumericStepper stepper)
    {
        stepper.BackgroundColor = UiTheme.InputBackground;
        stepper.TextColor = UiTheme.InputText;
    }

    /// <summary>
    /// Gives every text-entry control under <paramref name="root"/> the one input look (dark field, light
    /// border). Cards run their whole tree through this as they enter a stack, so an input looks the same
    /// whichever editor built it: steppers used to keep Rhino's grey native fill beside dark text boxes.
    /// Drop-downs and buttons are choices, not entry, and keep their own look.
    /// </summary>
    public static void StyleInputs(Control root)
    {
        IEnumerable<Control> all = root is Container container
            ? container.Children.Prepend(root)
            : new[] { root };
        foreach (var control in all)
        {
            switch (control)
            {
                case TextBox textBox:
                    StyleInput(textBox);
                    break;
                case NumericStepper stepper:
                    StyleInput(stepper);
                    break;
                case TextArea textArea:
                    StyleInput(textArea);
                    break;
                case ComboBox comboBox:
                    StyleInput(comboBox);
                    break;
            }
        }
    }

    private static void ApplyHelp(Control control, string? toolTip)
    {
        if (!string.IsNullOrWhiteSpace(toolTip))
            control.ToolTip = toolTip;
    }
}

internal static class UiLayouts
{
    public static DynamicLayout CardBody() => new()
    {
        DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceMedium),
        Padding = new Padding(UiMetrics.CardHorizontalPadding, UiMetrics.SpaceLarge)
    };

    public static DynamicLayout CompactForm() => new()
    {
        DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceSmall),
        Padding = new Padding(UiMetrics.SpaceMedium, UiMetrics.SpaceSmall)
    };
}
