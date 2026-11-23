using Eto.Drawing;
using Eto.Forms;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The one frame every MoleHill modal dialog is built in: Rhino styling, padding, the button row, intro
/// and note text, section headings, field rows and the search box. Each dialog used to assemble these by
/// hand, and they had drifted - the primary button first in some and last in others, two heading styles,
/// colons on some labels, inputs that never got the panel's input look. A dialog now says what it holds;
/// this class decides how that looks. <c>DialogGuardTests</c> keeps Default/Abort buttons in here.
/// </summary>
internal static class UiDialogs
{
    /// <summary>Title, padding, sizing and Rhino styling for a dialog about to be filled.</summary>
    public static void Frame(Dialog dialog, string title, Size? minimumSize = null, bool resizable = false)
    {
        dialog.Title = title;
        dialog.Padding = new Padding(UiMetrics.DialogPadding);
        dialog.Resizable = resizable;
        dialog.Maximizable = false;
        dialog.Minimizable = false;
        if (minimumSize is { } size)
            dialog.MinimumSize = size;
        dialog.UseRhinoStyle();
    }

    /// <summary>
    /// Sets the content and gives every text entry in it the panel's input look, so a field in a dialog
    /// looks like the same field on a card.
    /// </summary>
    public static void SetContent(Dialog dialog, Control content)
    {
        dialog.Content = content;
        UiControls.StyleInputs(content);
    }

    /// <summary>
    /// A dialog button: the panel's regular height, its caption with room either side, and never narrower
    /// than <see cref="UiMetrics.DialogButtonWidth"/>. A native button sizes itself to its text with no side
    /// padding, which clipped "Restore Defaults" and "Open Settings Folder" live.
    /// </summary>
    public static Button Button(string text, EventHandler<EventArgs>? onClick, string? toolTip = null)
    {
        var button = UiControls.Button(text, onClick, toolTip);
        int width = Math.Max(UiMetrics.DialogButtonWidth, TextWidth(text) + 2 * UiMetrics.SpaceXLarge);
        button.MinimumSize = new Size(width, UiMetrics.ControlHeight);
        return button;
    }

    /// <summary>
    /// The bottom button row, in Rhino's order: secondary actions on the left, then the primary action
    /// and Cancel on the right. The primary answers Enter and Cancel answers Escape.
    /// </summary>
    public static Control Actions(Dialog dialog, Button primary, Button cancel, params Control[] secondary)
    {
        dialog.DefaultButton = primary;
        dialog.AbortButton = cancel;

        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.DialogSpacing,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        foreach (Control control in secondary)
            row.Items.Add(control);
        row.Items.Add(new StackLayoutItem(null, expand: true));
        row.Items.Add(primary);
        row.Items.Add(cancel);
        return row;
    }

    /// <summary>A row of buttons that act on what is above them (Add, Remove, ...); a null pushes the rest right.</summary>
    public static Control ButtonRow(params Control?[] buttons)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.DialogRowSpacing,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        foreach (Control? button in buttons)
            row.Items.Add(button == null ? new StackLayoutItem(null, expand: true) : new StackLayoutItem(button));
        return row;
    }

    /// <summary>What the dialog is for, in a sentence or two, at the top.</summary>
    public static Label Intro(string text) => UiControls.Label(text, wrap: WrapMode.Word);

    /// <summary>Secondary explanation under a group or field.</summary>
    public static Label Note(string text = "") => UiControls.Label(text, UiLabelRole.Meta, WrapMode.Word);

    /// <summary>
    /// A group heading: bold, primary text, one line that ellipsizes. Drawn, because Rhino's dialog styling
    /// resets a <see cref="Label"/>'s font to the regular UI font, so a bold label heading rendered as plain
    /// text (seen live; the card header's drawn name keeps its weight).
    /// </summary>
    public static Control Heading(string text) => new TextLine(text, new Font(SystemFont.Bold), UiTheme.PrimaryText);

    /// <summary>
    /// One labelled field: the label in the shared label column (Title Case, no colon, as on a card), the
    /// editor taking the rest of the row.
    /// </summary>
    public static Control Field(string label, Control editor, string? toolTip = null, bool expand = true)
    {
        Label caption = UiControls.Label(label);
        // The shared column, widened for a label longer than it rather than clipping it ("Unmatched Codes").
        caption.Width = Math.Max(UiMetrics.LabelColumn, TextWidth(label) + UiMetrics.SpaceSmall);
        if (!string.IsNullOrWhiteSpace(toolTip))
        {
            caption.ToolTip = toolTip;
            editor.ToolTip ??= toolTip;
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.DialogRowSpacing,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { caption, new StackLayoutItem(editor, expand) }
        };
    }

    /// <summary>Two fields on one row, each label in the label column, splitting the width between them.</summary>
    public static Control FieldPair(
        string firstLabel, Control first, string? firstHelp,
        string secondLabel, Control second, string? secondHelp) =>
        new TableLayout
        {
            Spacing = new Size(UiMetrics.DialogSpacing, 0),
            Rows =
            {
                new TableRow(
                    new TableCell(Field(firstLabel, first, firstHelp), scaleWidth: true),
                    new TableCell(Field(secondLabel, second, secondHelp), scaleWidth: true))
            }
        };

    private static int TextWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        using var font = new Font(SystemFont.Default);
        return (int)Math.Ceiling(font.MeasureString(text).Width);
    }

    /// <summary>The filter box over a list. The placeholder names what is filtered: "Filter layers…".</summary>
    public static TextBox Search(string what)
    {
        var box = new TextBox { PlaceholderText = $"Filter {what}…" };
        UiControls.StyleInput(box);
        return box;
    }

    /// <summary>
    /// Asks for one line of text (a name). Returns the trimmed text, or null when cancelled or left blank.
    /// </summary>
    public static string? PromptText(Control owner, string title, string prompt, string initial, string confirmText = "OK")
    {
        var dialog = new Dialog<string?>();
        Frame(dialog, title);

        var box = new TextBox { Text = initial, Width = UiMetrics.Chs(28) };
        var confirm = Button(confirmText, (_, _) => dialog.Close(box.Text?.Trim()));
        var cancel = Button("Cancel", (_, _) => dialog.Close(null));

        SetContent(dialog, new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.DialogSpacing,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                Intro(prompt),
                box,
                Actions(dialog, confirm, cancel)
            }
        });

        string? result = dialog.ShowModal(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }
}
