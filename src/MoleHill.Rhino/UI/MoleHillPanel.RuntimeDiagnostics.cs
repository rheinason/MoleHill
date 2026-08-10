using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.UI;

// Generic per-card issue counts and redraw-only Show Issues binding for runtime overlay owners.
public sealed partial class MoleHillPanel
{
    private Control? CreateRuntimeDiagnosticsRow(
        TerrainDefinition terrain,
        RuntimeOverlayOwner owner)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return null;

        RuntimeDiagnosticSummary summary = _controller.GetRuntimeDiagnosticSummary(doc, terrain.TerrainId, owner);
        if (summary.Total == 0)
            return null;

        var showIssues = new CheckBox
        {
            Text = "Show Issues",
            Checked = _controller.IsRuntimeDiagnosticsVisible(doc, terrain.TerrainId, owner)
        };
        ApplyHelp(showIssues, "Show this card's runtime diagnostic geometry in the viewport. This does not rebuild, bake, or alter the terrain definition.");
        showIssues.CheckedChanged += (_, _) =>
        {
            RhinoDoc? activeDoc = RhinoDoc.ActiveDoc;
            if (activeDoc != null)
                _controller.SetRuntimeDiagnosticsVisible(activeDoc, terrain.TerrainId, owner, showIssues.Checked == true);
        };

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Items =
            {
                showIssues,
                new Label
                {
                    Text = FormatRuntimeDiagnosticSummary(summary),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextColor = UiTheme.MutedText
                }
            }
        };
    }

    private string AppendRuntimeDiagnosticSummary(
        string summaryText,
        TerrainDefinition terrain,
        RuntimeOverlayOwner owner)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return summaryText;

        RuntimeDiagnosticSummary summary = _controller.GetRuntimeDiagnosticSummary(doc, terrain.TerrainId, owner);
        return summary.Total == 0
            ? summaryText
            : $"{summaryText} · {FormatRuntimeDiagnosticSummary(summary)}";
    }

    private static string FormatRuntimeDiagnosticSummary(RuntimeDiagnosticSummary summary)
    {
        var parts = new List<string>();
        if (summary.Errors > 0)
            parts.Add($"{summary.Errors} error{(summary.Errors == 1 ? string.Empty : "s")}");
        if (summary.Warnings > 0)
            parts.Add($"{summary.Warnings} warning{(summary.Warnings == 1 ? string.Empty : "s")}");
        if (summary.Information > 0)
            parts.Add($"{summary.Information} info");
        return string.Join(", ", parts);
    }
}
