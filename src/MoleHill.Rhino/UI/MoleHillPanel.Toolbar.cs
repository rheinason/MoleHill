using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace MoleHill.Rhino.UI;

// Top toolbar: the editable terrain selector with New/Copy/Delete, and the Rebuild/Reset/Live and
// Bake/visibility/lock row.
public sealed partial class MoleHillPanel : Panel
{
    /// <summary>The terrain selector, Live toggle, visibility and lock buttons: their handlers and help.</summary>
    private void WireToolbarControls()
    {
        ApplyHelp(_terrainSelector, "Rename the active terrain, or open the list to select another terrain. Renames commit when you press Enter or leave the field.");
        StyleComboBox(_terrainSelector);
        BindTerrainSelector();

        _liveUpdate.CheckedChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            MutateSelectedTerrain(terrain => terrain.LiveUpdateEnabled = _liveUpdate.Checked == true, scheduleRebuild: false);
        };

        _visibilityButton.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
            if (doc == null || terrain == null)
                return;

            _controller.SetTerrainVisible(doc, terrain.TerrainId, !terrain.IsVisible);
        };

        _lockButton.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
            if (doc == null || terrain == null)
                return;

            _controller.SetTerrainLocked(doc, terrain.TerrainId, !terrain.IsLocked);
        };
        ApplyHelp(_liveUpdate, "Automatically rebuild when referenced Rhino geometry or layers change.");
        ApplyHelp(_visibilityButton, "Hide or show all generated terrain outputs.");
        ApplyHelp(_lockButton, "Lock or unlock MoleHill-managed live document outputs. Source geometry and previously baked objects are unaffected.");
    }

    /// <summary>The two-row terrain toolbar: identity and New/Copy/Delete, then Rebuild/Live and Bake/visibility/lock.</summary>
    private Control BuildTerrainToolbar()
    {
        _newButton = MakeIconButton(PanelButtonIcon.Add, OnNewTerrain, "Create a new terrain");
        _dupButton = MakeIconButton(PanelButtonIcon.Duplicate, OnDuplicateTerrain, "Duplicate selected terrain");
        _deleteButton = MakeIconButton(PanelButtonIcon.Delete, OnDeleteTerrain, "Delete selected terrain");
        _rebuildButton = MakeIconButton(PanelButtonIcon.Rebuild, OnRebuildTerrain, "Force rebuild terrain now");
        _resetBuildButton = MakeIconButton(PanelButtonIcon.ResetBuild, OnResetTerrainBuild, "Cancel the current worker, clear queued rebuilds, and drop cached preview state.");
        _bakeButton = MakeIconButton(PanelButtonIcon.Bake, OnBakeTerrain, "Bake the terrain to document objects");
        _resetTerrainDataButton = MakeInlineButton("Reset Terrain Data", OnResetTerrainData,
            "The stored terrain data in this document is unreadable and is being preserved untouched. Click to discard it.");
        _resetTerrainDataButton.Visible = false;
        UiControls.Apply(_visibilityButton, UiButtonRole.Icon);
        UiControls.Apply(_lockButton, UiButtonRole.Icon);
        UiControls.Apply(_untrackSelectedBakesButton, UiButtonRole.Inline);
        UiControls.Apply(_untrackAllBakesButton, UiButtonRole.Inline);
        // Matches the icon buttons' now-uniform CompactControlHeight (UiButtonRole.Icon dropped its
        // taller IconToolbar variant) — at the old ControlHeight these sat a few pixels taller than the
        // icon buttons beside them in the same row, throwing off vertical alignment.
        _liveUpdate.Height = UiMetrics.CompactControlHeight;

        _terrainSelector.Height = UiMetrics.CompactControlHeight;

        // ── Toolbar (two rows) ────────────────────────────────────────
        // Row 1: editable terrain selector (primary) | New / Copy / Delete (actions, wrap below
        // if they don't fit). Row 2: all terrain actions stay on one uninterrupted toolbar line.
        var terrainLabel = UiControls.Label("Terrain", UiLabelRole.Meta);
        var terrainIdentity = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                terrainLabel,
                new StackLayoutItem(_terrainSelector, expand: true)
            }
        };
        var identityRow = new AdaptivePrimaryActionRow(
            terrainIdentity,
            UiMetrics.SpaceMedium,
            _newButton,
            _dupButton,
            _deleteButton);
        var identityGroup = new DynamicLayout
        {
            BackgroundColor = UiTheme.ToolbarBackground,
            Padding = new Padding(UiMetrics.SpaceLarge, UiMetrics.SpaceMedium)
        };
        identityGroup.Add(identityRow, xscale: true, yscale: false);

        // Rebuild/Reset Build/Live pack left; Bake/visibility/lock stay on that same line and land flush
        // against the right edge — the same column as row 1's New/Copy/Delete controls — instead of
        // wrapping independently. Rebuild/Reset Build/Bake are icon buttons now (fixed width), so unlike
        // the old text buttons this cluster no longer needs an overflow menu or text abbreviation to fit
        // even the narrowest supported panel (UiMetrics.CompactPanelTarget) — only the "Live" caption
        // still needs to shrink away.
        var actionsPrimaryCluster = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { _rebuildButton, _resetBuildButton, _liveUpdate, _resetTerrainDataButton }
        };

        var actionsRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(actionsPrimaryCluster, expand: true),
                _bakeButton,
                _visibilityButton,
                _lockButton
            }
        };
        // Matches identityGroup's padding so row 2's right-hand icons land in exactly the same column as
        // row 1's — without this, row 1's extra 8px inset (from identityGroup's own Padding) shifts it
        // relative to row 2's bare StackLayoutItem.
        var actionsGroup = new DynamicLayout
        {
            Padding = new Padding(UiMetrics.SpaceLarge, 0)
        };
        actionsGroup.Add(actionsRow, xscale: true, yscale: false);

        // Shed the least-essential text first as the panel narrows: drop the "Terrain" caption, then drop
        // the "Live" caption entirely (the checkbox alone still reads fine with its tooltip). Measured off
        // the outer row (identityRow/actionsRow), not the inner primary cluster — an expand:true child's
        // own reported Width can lag its actual arranged size.
        const int rowSpacing = UiMetrics.SpaceMedium * 5;
        int trailingActionsWidth = UiMetrics.IconButtonWidth * 3 + rowSpacing;
        int LiveTextNeeded() =>
            UiMetrics.IconButtonWidth * 2 +
            UiMetrics.Chs("Live".Length + 4) +
            trailingActionsWidth;

        void UpdateToolbarTextDensity()
        {
            terrainLabel.Visible = identityRow.Width >= UiMetrics.Chs(66);

            bool showLiveText = actionsRow.Width <= 0 || actionsRow.Width >= LiveTextNeeded();
            _liveUpdate.Text = showLiveText ? "Live" : string.Empty;
        }

        identityRow.SizeChanged += (_, _) => UpdateToolbarTextDensity();
        actionsRow.SizeChanged += (_, _) => UpdateToolbarTextDensity();
        identityRow.Shown += (_, _) => UpdateToolbarTextDensity();

        var toolbar = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            Padding = new Padding(
                UiMetrics.SectionHorizontalPadding,
                UiMetrics.SectionTopPadding,
                UiMetrics.SectionHorizontalPadding,
                UiMetrics.SectionBottomPadding),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(identityGroup, HorizontalAlignment.Stretch),
                new StackLayoutItem(actionsGroup, HorizontalAlignment.Stretch)
            }
        };

        return toolbar;
    }

    private void OnTerrainSelectorChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingTerrainSelector || _isRefreshing)
            return;

        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        if (!Guid.TryParse(_terrainSelector.SelectedKey, out Guid terrainId))
            return;

        var selectedTerrain = _controller.GetSelectedTerrain(doc);
        if (selectedTerrain?.TerrainId == terrainId)
            return;

        _controller.SetSelectedTerrain(doc, terrainId);
        RefreshUi();
    }

    private void BindTerrainSelector()
    {
        void CommitName()
        {
            if (_isRefreshing || _isUpdatingTerrainSelector)
                return;

            var doc = RhinoDoc.ActiveDoc;
            var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
            if (doc == null || terrain == null)
                return;

            string name = (_terrainSelector.Text ?? string.Empty).Trim();
            if (string.Equals(name, terrain.Name, StringComparison.Ordinal))
                return;

            _controller.MutateTerrain(doc, terrain.TerrainId, item => item.Name = name, scheduleRebuild: false);
            RefreshUi();
        }

        _terrainSelector.SelectedIndexChanged += OnTerrainSelectorChanged;
        _terrainSelector.LostFocus += (_, _) => CommitName();
        _terrainSelector.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter)
                return;

            CommitName();
            e.Handled = true;
        };
    }
}
