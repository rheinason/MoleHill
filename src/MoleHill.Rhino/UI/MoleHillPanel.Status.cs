using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Grading;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

// Status bar/log helpers: build messages, structured grading diagnostics, and copy-case actions.
public sealed partial class MoleHillPanel
{
    private void RestoreStatusText()
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        var text = terrain?.LastBuildMessage ?? "Create a terrain to start.";
        SetStatusText(text, terrain?.LastStructuredDiagnostics);
    }

    private void SetStatusText(string text, IReadOnlyList<GradingDiagnostic>? structuredDiagnostics = null)
    {
        string statusText = FormatStatusText(text, structuredDiagnostics);
        var statusColor = GetStatusColor(statusText, structuredDiagnostics);
        _statusTextArea.Text = statusText;
        _statusTextArea.TextColor = statusColor;
        _statusHintLabel.Text = GetStatusHintText(statusText, structuredDiagnostics);
    }

    private void CopyStatusLog()
    {
        string text = _statusTextArea.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return;

        Clipboard.Instance.Text = text;
    }

    private void CopyCaseBundle()
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        if (_controller.TryExportTerrainCaseBundle(doc, terrain.TerrainId, out string? archivePath, out string? coreTestCode, out string? errorMessage) &&
            !string.IsNullOrWhiteSpace(archivePath))
        {
            string copiedMessage = string.IsNullOrWhiteSpace(coreTestCode)
                ? $"MoleHill copied case bundle path: {archivePath}"
                : $"MoleHill copied test case source; bundle path: {archivePath}";
            Clipboard.Instance.Text = copiedMessage;
            RhinoApp.WriteLine(copiedMessage);
            return;
        }

        MessageBox.Show(
            RhinoEtoApp.MainWindowForDocument(doc),
            errorMessage ?? "Could not export the selected terrain case bundle.",
            "Copy Case",
            MessageBoxButtons.OK,
            MessageBoxType.Error);
    }

    private static string FormatStatusText(string text, IReadOnlyList<GradingDiagnostic>? structuredDiagnostics)
    {
        if (structuredDiagnostics == null || structuredDiagnostics.Count == 0)
            return text;

        var diagnostics = structuredDiagnostics
            .Where(static diagnostic => !string.IsNullOrWhiteSpace(diagnostic.Message))
            .ToArray();
        if (diagnostics.Length == 0)
            return text;

        var lines = new List<string>
        {
            $"grading diagnostics: {FormatDiagnosticCounts(diagnostics)}"
        };

        foreach (var diagnostic in diagnostics.Take(12))
        {
            string code = string.IsNullOrWhiteSpace(diagnostic.Code)
                ? string.Empty
                : $" [{diagnostic.Code}]";
            string target = diagnostic.TargetIndex.HasValue
                ? $" #{diagnostic.TargetIndex.Value}"
                : string.Empty;
            lines.Add($"- {FormatDiagnosticSeverity(diagnostic.Severity)}{target}{code}: {diagnostic.Message}");
        }

        if (diagnostics.Length > 12)
            lines.Add($"- {diagnostics.Length - 12:N0} more structured diagnostic(s).");

        if (!string.IsNullOrWhiteSpace(text))
        {
            lines.Add(string.Empty);
            lines.Add("build log:");
            lines.Add(text);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static Color GetStatusColor(string text, IReadOnlyList<GradingDiagnostic>? structuredDiagnostics = null)
    {
        if (structuredDiagnostics != null)
        {
            if (structuredDiagnostics.Any(static diagnostic => diagnostic.Severity == GradingDiagnosticSeverity.Error))
                return Colors.Red;
            if (structuredDiagnostics.Any(static diagnostic => diagnostic.Severity == GradingDiagnosticSeverity.Warning))
                return Color.FromArgb(200, 120, 0);
        }

        if (text.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            return Colors.Red;
        if (text.Contains("Scheduled", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Building", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(200, 120, 0);
        return UiTheme.PrimaryText;
    }

    private static string GetStatusHintText(string text, IReadOnlyList<GradingDiagnostic>? structuredDiagnostics = null)
    {
        if (structuredDiagnostics != null && structuredDiagnostics.Count > 0)
            return $"Grading: {FormatDiagnosticCounts(structuredDiagnostics)}";

        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string firstLine = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? text;

        return firstLine.Length > 45 ? firstLine[..45] + "..." : firstLine;
    }

    private static string FormatDiagnosticCounts(IReadOnlyList<GradingDiagnostic> diagnostics)
    {
        int errors = diagnostics.Count(static diagnostic => diagnostic.Severity == GradingDiagnosticSeverity.Error);
        int warnings = diagnostics.Count(static diagnostic => diagnostic.Severity == GradingDiagnosticSeverity.Warning);
        int information = diagnostics.Count(static diagnostic => diagnostic.Severity == GradingDiagnosticSeverity.Information);

        var parts = new List<string>(3);
        if (errors > 0)
            parts.Add($"{errors:N0} error{Plural(errors)}");
        if (warnings > 0)
            parts.Add($"{warnings:N0} warning{Plural(warnings)}");
        if (information > 0)
            parts.Add($"{information:N0} info");

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static string FormatDiagnosticSeverity(GradingDiagnosticSeverity severity)
    {
        return severity switch
        {
            GradingDiagnosticSeverity.Error => "Error",
            GradingDiagnosticSeverity.Warning => "Warning",
            _ => "Info"
        };
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";
}
