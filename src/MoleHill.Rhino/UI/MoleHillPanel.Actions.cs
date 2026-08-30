using Eto.Forms;
using Rhino;
using Rhino.UI;
using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.UI;

// Terrain-level panel actions: create/copy/delete/convert/bake/rebuild/reset and bake tracking.
public sealed partial class MoleHillPanel
{
    private void OnNewTerrain(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
            _controller.CreateTerrain(doc, seedFromSelection: true);
    }

    private void OnDuplicateTerrain(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.DuplicateTerrain(doc, terrain.TerrainId);
    }

    private void OnDeleteTerrain(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.DeleteTerrain(doc, terrain.TerrainId);
    }

    private void OnConvertTerrain(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.ConvertToRhino(doc, terrain.TerrainId);
    }

    private void OnBakeTerrain(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.BakeTerrain(doc, terrain.TerrainId);
    }

    private void BakeOutputLayers()
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        var result = _controller.EnsureTerrainOutputLayers(doc, terrain.TerrainId);
        RhinoApp.WriteLine(
            $"MoleHill: output layers updated ({result.CreatedCount} created, {result.RefreshedCount} already present, {result.SkippedCount} skipped).");
    }

    private void OnUntrackSelectedBakes(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.UntrackSelectedBakedObjects(doc, terrain.TerrainId);
        RefreshUi();
    }

    private void OnUntrackAllBakes(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.UntrackAllBakedObjects(doc, terrain.TerrainId);
        RefreshUi();
    }

    private void OnRebuildTerrain(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.RebuildTerrain(doc, terrain.TerrainId);
    }

    private void OnResetTerrainBuild(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        var result = MessageBox.Show(
            RhinoEtoApp.MainWindowForDocument(doc),
            "Force reset clears queued rebuilds and cancels the running build for the selected terrain. In-flight preview state will be discarded.",
            "Force Reset Build",
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No);
        if (result != DialogResult.Yes)
            return;

        _controller.ForceResetTerrainBuild(doc, terrain.TerrainId);
    }

    private void OnResetTerrainData(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        var result = MessageBox.Show(
            RhinoEtoApp.MainWindowForDocument(doc),
            "The terrain data stored in this document could not be read (truncated, or saved by a " +
            "newer plugin version) and is being kept untouched so it isn't lost. Resetting discards it " +
            "permanently and starts this document with no terrains.",
            "Reset Unreadable Terrain Data",
            MessageBoxButtons.YesNo,
            MessageBoxType.Warning,
            MessageBoxDefaultButton.No);
        if (result != DialogResult.Yes)
            return;

        _controller.ResetTerrainDataAfterFailedLoad(doc);
        RefreshUi();
    }

    // Native Button.Enabled = false makes WinForms substitute its own greyscale-plus-emboss "disabled"
    // rendering over our custom bitmap — that's why these icons looked washed out and inconsistent next
    // to Add (which is rarely disabled) rather than evenly muted like Eye/Lock's own state icons. Every
    // handler behind these buttons already null-guards against "no selected terrain," so keep them
    // clickable and communicate unavailability through our own muted icon variant instead.
    private void SetActionButtonsEnabled(bool enabled)
    {
        SetButtonIcon(_dupButton, PanelButtonIcon.Duplicate, muted: !enabled);
        SetButtonIcon(_deleteButton, PanelButtonIcon.Delete, muted: !enabled);
        SetButtonIcon(_rebuildButton, PanelButtonIcon.Rebuild, muted: !enabled);
        SetButtonIcon(_resetBuildButton, PanelButtonIcon.ResetBuild, muted: !enabled);
        SetButtonIcon(_bakeButton, PanelButtonIcon.Bake, muted: !enabled);
        _toleranceStepper.Enabled = enabled;
    }
}
