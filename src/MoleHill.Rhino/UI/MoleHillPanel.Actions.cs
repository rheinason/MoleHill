using Eto.Forms;
using Rhino;
using Rhino.UI;

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

    private void BakeLayerPaths(IEnumerable<string?> assignedLayerPaths)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        var layerPaths = assignedLayerPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (layerPaths.Count == 0)
            layerPaths = _controller.GetSelectedLayerPaths(doc).ToList();

        if (layerPaths.Count == 0)
        {
            RhinoApp.WriteLine("MoleHill: assign or highlight source layers before baking layer styles.");
            return;
        }

        var result = _controller.EnsureBakedLayersForSourceLayers(doc, layerPaths);
        RhinoApp.WriteLine(
            $"MoleHill: baked layer styles updated ({result.CreatedCount} created, {result.RefreshedCount} refreshed, {result.SkippedCount} skipped).");
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

    private void SetActionButtonsEnabled(bool enabled)
    {
        _dupButton.Enabled             = enabled;
        _deleteButton.Enabled          = enabled;
        _rebuildButton.Enabled         = enabled;
        _resetBuildButton.Enabled      = enabled;
        _bakeButton.Enabled            = enabled;
        _visibilityButton.Enabled      = enabled;
        _lockButton.Enabled            = enabled;
        _toleranceStepper.Enabled      = enabled;
    }
}
