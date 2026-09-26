using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.UI;

// Panel-wide control styling and factories: text and combo styling, toolbar/icon/inline/pill buttons,
// help labels and tooltips, responsive rows, and small display formatters shared across tabs.
public sealed partial class MoleHillPanel : Panel
{
    private static Control CreateResponsiveControlGroup(int spacing, params Control[] controls) =>
        new AdaptiveControlGroup(spacing, controls);

    private Control CreateResponsivePrimaryActionRow(Control primaryControl, int spacing, params Control[] actionControls)
    {
        return new AdaptivePrimaryActionRow(primaryControl, spacing, actionControls);
    }

    private static string EllipsizeText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars || maxChars <= 3)
            return text;

        return text[..(maxChars - 3)] + "...";
    }

    private static void StyleTextBox(TextBox textBox)
    {
        UiControls.StyleInput(textBox);
    }

    private static void StyleComboBox(ComboBox comboBox)
    {
        UiControls.StyleInput(comboBox);
    }

    private static void StyleTextArea(TextArea textArea)
    {
        UiControls.StyleInput(textArea);
    }

    private static Button MakeToolbarButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null)
    {
        return UiControls.Button(text, onClick, toolTip, UiButtonRole.Toolbar);
    }

    private static Button MakeIconButton(PanelButtonIcon icon, EventHandler<EventArgs> onClick, string? toolTip = null)
    {
        return UiControls.IconButton(icon, onClick, toolTip);
    }

    private static Button MakeInlineButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null)
    {
        return UiControls.Button(text, onClick, toolTip, UiButtonRole.Inline);
    }

    private static void SetButtonIcon(Button button, PanelButtonIcon icon, bool muted = false) =>
        PanelButtonIcons.Apply(button, icon, muted);

    private static Button MakePillButton(string text, string? tooltip = null)
    {
        return UiControls.Button(text, null, tooltip, UiButtonRole.Pill);
    }

    private Label CreateHelpLabel(string text, string help, int width)
    {
        return UiControls.HelpLabel(text, help, width);
    }

    private static string FormatVolume(double value)
    {
        string prefix = value < 0 ? "-" : string.Empty;
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        if (!unitContext.IsSupported)
            unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
        return $"{prefix}{unitContext.FormatVolume(Math.Abs(value))}";
    }

    private void ApplyHelp(Control control, string help)
    {
        control.ToolTip = help;
    }

    private static int CountReferences(SourceReferenceSet sourceSet)
    {
        return sourceSet.ObjectIds.Count + sourceSet.LayerPaths.Count;
    }

    // ── Drag-and-drop: analysis cards ────────────────────────────────────
}
