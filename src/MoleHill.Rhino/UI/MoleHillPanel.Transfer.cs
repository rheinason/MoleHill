using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.UI;

/// <summary>
/// Import and export of a terrain definition (design review Steps 77, 78, 80): the terrain menu beside the
/// selector, and the import preview anchored below the selector. What travels is decided by
/// <see cref="TerrainTransfer"/>; this file is only the interaction.
/// </summary>
public sealed partial class MoleHillPanel
{
    private const string TransferFilterName = "MoleHill Terrain Definition";

    private IconButton BuildTerrainMenuButton()
    {
        IconButton? button = null;
        button = MakeIconButton(PanelButtonIcon.More, (_, _) =>
        {
            var menu = new ContextMenu();
            var import = new ButtonMenuItem { Text = "Import Terrain Definition..." };
            import.Click += (_, _) => ImportTerrainDefinition();
            var export = new ButtonMenuItem { Text = "Export Terrain Definition..." };
            export.Click += (_, _) => ExportTerrainDefinition();
            RhinoDoc? doc = RhinoDoc.ActiveDoc;
            export.Enabled = doc != null && _controller.GetSelectedTerrain(doc) != null;
            menu.Items.Add(import);
            menu.Items.Add(export);
            menu.Show(button!);
        }, "Import a terrain definition from a file, or export this one. Only assigned layers travel with it.");
        return button;
    }

    private void ExportTerrainDefinition()
    {
        if (RhinoDoc.ActiveDoc is not { } doc || _controller.GetSelectedTerrain(doc) is not { } terrain)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Terrain Definition",
            FileName = terrain.Name + TerrainTransfer.FileExtension,
        };
        dialog.Filters.Add(new FileFilter(TransferFilterName, "*" + TerrainTransfer.FileExtension));
        if (dialog.ShowDialog(this) != DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        string? json = _controller.ExportTerrainDefinition(doc, terrain.TerrainId);
        if (json == null)
            return;

        string path = dialog.FileName.EndsWith(TerrainTransfer.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? dialog.FileName
            : dialog.FileName + TerrainTransfer.FileExtension;
        try
        {
            File.WriteAllText(path, json);
            RhinoApp.WriteLine($"[MoleHill] Exported \"{terrain.Name}\" to {path}. Picked objects stay behind; assigned layers travel.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not write {path}: {exception.Message}", "Export Terrain Definition", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void ImportTerrainDefinition()
    {
        if (RhinoDoc.ActiveDoc is not { } doc)
            return;

        var dialog = new OpenFileDialog { Title = "Import Terrain Definition" };
        dialog.Filters.Add(new FileFilter(TransferFilterName, "*" + TerrainTransfer.FileExtension));
        if (dialog.ShowDialog(this) != DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        TerrainImportPreview? preview;
        try
        {
            preview = _controller.PreviewTerrainImport(doc, File.ReadAllText(dialog.FileName));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Import Terrain Definition", MessageBoxButtons.OK, MessageBoxType.Error);
            return;
        }

        if (preview == null || !ShowImportPreview(preview, Path.GetFileName(dialog.FileName)))
            return;

        _controller.ImportTerrain(doc, preview);
        RefreshUi();
    }

    /// <summary>
    /// The import preview, anchored below the terrain selector with its left edge (Step 80): what will be
    /// created, and which assigned layers this document has and which need reassigning.
    /// </summary>
    private bool ShowImportPreview(TerrainImportPreview preview, string fileName)
    {
        TerrainDefinition terrain = preview.Terrain;
        var body = UiLayouts.CardBody();

        body.AddRow(CreateReadOnlyValueRow("Terrain", terrain.Name, $"Read from {fileName}. It is added as a new terrain; nothing existing changes."));
        body.AddRow(CreateReadOnlyValueRow(
            "Cards",
            // Only what the file has: "1 modifier · 3 analyses", not a row of zeros.
            SummaryText.Join(
                terrain.Modifiers.Count > 0 ? Count(terrain.Modifiers.Count, "modifier", "modifiers") : null,
                terrain.Objects.Count > 0 ? Count(terrain.Objects.Count, "object card", "object cards") : null,
                terrain.Zones.Count > 0 ? Count(terrain.Zones.Count, "zone", "zones") : null,
                terrain.Analyses.Count > 0 ? Count(terrain.Analyses.Count, "analysis", "analyses") : null,
                terrain.Annotations.Count > 0 ? Count(terrain.Annotations.Count, "annotation", "annotations") : null),
            "Every card comes across with its settings."));
        if (RhinoDoc.ActiveDoc is { } doc && !string.Equals(preview.SourceUnits, doc.ModelUnitSystem.ToString(), StringComparison.Ordinal))
            body.AddRow(CreateNoteRow($"Lengths are converted from {preview.SourceUnits} to {doc.ModelUnitSystem}."));

        body.AddRow(CreateSectionRule("Layers"));
        if (preview.FoundLayers.Count == 0 && preview.MissingLayers.Count == 0)
            body.AddRow(CreateNoteRow("No layers are assigned. Assign sources on the cards after importing."));
        foreach (string path in preview.FoundLayers)
            body.AddRow(CreateReadOnlyValueRow("Found", path, "This document has this layer, so the card reads it straight away."));
        foreach (string path in preview.MissingLayers)
            body.AddRow(CreateReadOnlyValueRow("Missing", path, "This document has no such layer. Reassign the source on its card after importing."));
        if (preview.MissingLayers.Count > 0)
        {
            body.AddRow(CreateWarningRow(
                preview.MissingLayers.Count == 1
                    ? "1 layer is not in this document. The card that uses it imports without that source; reassign it on the card."
                    : $"{preview.MissingLayers.Count} layers are not in this document. The cards that use them import without " +
                      "those sources; reassign them on the cards."));
        }

        body.AddRow(CreateNoteRow(
            "Picked objects, build results and links to other terrains stay in the document they came from" +
            (preview.ClearedTerrainLinks > 0 ? $" ({Count(preview.ClearedTerrainLinks, "link", "links")} cleared)." : ".")));

        bool accepted = false;
        var dialog = new Dialog
        {
            Title = "Import Terrain Definition",
            Resizable = false,
            Padding = new Padding(UiMetrics.DialogPadding),
            MinimumSize = new Size(UiMetrics.Chs(48), 0),
        };
        var importButton = UiControls.Button("Import", (_, _) => { accepted = true; dialog.Close(); }, "Add it as a new terrain.", UiButtonRole.Toolbar);
        var cancelButton = UiControls.Button("Cancel", (_, _) => dialog.Close(), role: UiButtonRole.Toolbar);
        dialog.DefaultButton = importButton;
        dialog.AbortButton = cancelButton;
        dialog.Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.DialogSpacing,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(body, HorizontalAlignment.Stretch),
                new StackLayoutItem(
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = UiMetrics.SpaceMedium,
                        Items = { null, importButton, cancelButton },
                    },
                    HorizontalAlignment.Stretch),
            },
        };

        // Step 80: below the selector, same left edge, a small gap.
        try
        {
            PointF anchor = _terrainSelector.PointToScreen(new PointF(0, _terrainSelector.Height + UiMetrics.SpaceSmall));
            dialog.Location = new Point((int)anchor.X, (int)anchor.Y);
        }
        catch (Exception)
        {
            // Not on screen (a floating panel mid-layout): let the dialog centre itself.
        }

        global::Rhino.UI.EtoExtensions.UseRhinoStyle(dialog);
        dialog.ShowModal(this);
        return accepted;
    }
}
