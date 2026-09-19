using System.Globalization;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;
using MoleHill.Core.Scattering;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;

namespace MoleHill.Rhino.UI;

[System.Runtime.InteropServices.Guid("E8A65B83-74A6-4325-B66D-D7005A4A8257")]
public sealed partial class MoleHillPanel : Panel
{

    private readonly TerrainController _controller = TerrainController.Instance;
    private readonly ComboBox _terrainSelector = new() { AutoComplete = true };
    private readonly CheckBox _liveUpdate = new() { Text = "Live" };
    private readonly TextArea _statusTextArea = new() { ReadOnly = true, Wrap = true, Height = 180 };
    private readonly Label _layerTemplateLabel = new() { VerticalAlignment = VerticalAlignment.Center, Wrap = WrapMode.Word };
    private readonly Panel _terrainColorSwatch = new() { Width = 18, Height = 18 };
    private readonly Label _terrainColorLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Label _statusHintLabel   = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericStepper _toleranceStepper = new();
    private readonly NumericStepper _terrainOpacityStepper = new();
    private readonly Slider _terrainOpacitySlider = new() { MinValue = 0, MaxValue = 100, Width = UiMetrics.SliderMin };
    private readonly CheckBox _showWiresCheck = new() { Text = "Show Wires" };

    /// <summary>Preview line weight in tenths, so 10 is the 1.0 default. Eto sliders are integer-valued.</summary>
    private readonly Slider _previewLineWeightSlider = new() { MinValue = 3, MaxValue = 40, Width = UiMetrics.SliderMin };

    private readonly Label _previewLineWeightValue = new() { TextColor = UiTheme.MutedText, Width = 34 };
    private readonly CheckBox _showSlowBuildWarningCheck = new() { Text = "Warn Before Slow Builds" };
    private readonly CheckBox _replacePreviousBakesCheck = new() { Text = "Replace Previously Baked" };
    private readonly Button _untrackSelectedBakesButton = new() { Text = "Untrack Selected" };
    private readonly Button _untrackAllBakesButton = new() { Text = "Untrack All" };
    private bool _settingsExpanded = true;
    private bool _statusExpanded = false;
    private bool _isUpdatingOpacityControls;
    private Panel? _settingsContent;
    private Panel? _statusContent;
    private readonly Button _visibilityButton = new();
    private readonly Button _lockButton = new();
    private Button _dupButton = new();
    private Button _newButton = new();
    private Button _deleteButton = new();
    private Button _rebuildButton = new();
    private Button _resetBuildButton = new();
    private Button _bakeButton = new();
    private Button _resetTerrainDataButton = new();
    private bool _isUpdatingTerrainSelector;
    private readonly HashSet<Guid> _collapsedModifiers = new();
    private readonly HashSet<Guid> _collapsedObjects = new();
    private readonly HashSet<Guid> _collapsedZones = new();
    private readonly StackLayout _modifierStack = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly StackLayout _objectsStack = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly StackLayout _zonesStack = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly StackLayout _analysisStack = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly StackLayout _annotationStack = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly HashSet<Guid> _collapsedAnalyses = new();
    private readonly HashSet<Guid> _expandedColorRamps = new();
    private readonly Dictionary<Guid, int> _selectedColorRampStops = new();
    private readonly HashSet<Guid> _expandedGradePathAdvancedSettings = new();
    private readonly Dictionary<Guid, Panel>    _modifierCardMap      = new();
    private readonly Dictionary<Guid, Panel>    _modifierSepMap       = new();
    private readonly Dictionary<Guid, Panel>    _modifierStripMap     = new();
    private readonly Dictionary<Guid, Color>    _modifierStripColors  = new();
    private readonly Dictionary<Guid, Panel>    _analysisCardMap      = new();
    private readonly Dictionary<Guid, Panel>    _analysisSepMap       = new();
    private readonly Dictionary<Guid, Panel>    _analysisStripMap     = new();
    private readonly Dictionary<Guid, Color>    _analysisStripColors  = new();
    private readonly Dictionary<Guid, Panel>    _annotationCardMap    = new();
    private readonly Dictionary<Guid, Panel>    _annotationSepMap     = new();
    private readonly Dictionary<Guid, Panel>    _annotationStripMap   = new();
    private readonly Dictionary<Guid, Color>    _annotationStripColors = new();
    private readonly Dictionary<Guid, Panel>    _zoneCardMap          = new();
    private readonly Dictionary<Guid, Panel>    _zoneSepMap           = new();
    private Guid? _dragOverModifierId;
    private Guid? _dragOverAnalysisId;
    private Guid? _dragOverAnnotationId;
    private Guid? _dragOverZoneId;
    private const int HeaderActionHeight = UiMetrics.CompactControlHeight;
    private const int LayerPickerMinHeight = 160;
    private const int LayerPickerMargin = 6;
    private const int LayerPickerRowHeight = 28;
    private readonly EventHandler _stateChangedHandler;
    private bool _isRefreshing;
    private bool _isPanelLoaded;
    private bool _isStateChangedSubscribed;
    private int _deferredControllerRefreshDepth;
    private bool _hasDeferredControllerRefresh;
    /// <summary>A refresh is already queued; further state changes need not queue another.</summary>
    private bool _controllerRefreshPosted;
    private int _selectedTabIndex;

    /// <summary>
    /// Per-tab "this layout no longer matches the model" flags. A state change marks every tab dirty but
    /// only rebuilds the one on screen; the rest are built when they are next shown. Rebuilding all five
    /// on every mutation was four fifths wasted work, and card rebuilds are the expensive part of a
    /// refresh.
    /// </summary>
    private readonly bool[] _tabLayoutDirty = new bool[5];

    private Scrollable[]? _tabScrollables;

    /// <summary>The terrain the layouts were last built for, so a tab shown later builds the right thing.</summary>
    private TerrainDefinition? _lastRefreshedTerrain;
    private Panel? _tabContentPanel;
    private Label? _zonesEyeButton;
    private Label? _analysisEyeButton;
    private readonly Dictionary<int, Panel> _tabChipMap = new();
    private readonly Dictionary<int, Label> _tabChipLabelMap = new();

    private enum LayerPickerMode
    {
        SingleSelect,
        MultiSelect
    }

    private sealed record LayerPickerEntry(string Path, string DisplayText, Color DotColor);

    public MoleHillPanel()
    {
        _stateChangedHandler = HandleControllerStateChanged;

        ApplyHelp(_terrainSelector, "Rename the active terrain, or open the list to select another terrain. Renames commit when you press Enter or leave the field.");
        StyleComboBox(_terrainSelector);
        BindTerrainSelector();

        _liveUpdate.CheckedChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            MutateSelectedTerrain(terrain => terrain.LiveUpdateEnabled = _liveUpdate.Checked == true, scheduleRebuild: false);
        };

        _toleranceStepper.DecimalPlaces = 3;
        _toleranceStepper.Increment = 0.1;
        _toleranceStepper.MinValue = 0;
        ApplyHelp(_toleranceStepper, "Smallest terrain detail to preserve automatically. Smaller values keep more detail; larger values simplify and merge nearby geometry more aggressively.");
        void CommitTolerance()
        {
            if (_isRefreshing)
                return;

            var doc = RhinoDoc.ActiveDoc;
            var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
            if (terrain == null)
                return;

            double value = _toleranceStepper.Value;
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(terrain.GlobalTolerance, value))
                return;

            MutateSelectedTerrain(t => t.GlobalTolerance = value, scheduleRebuild: true);
        }

        var toleranceTimer = new UITimer { Interval = UiTiming.ToleranceCommitSeconds };
        toleranceTimer.Elapsed += (_, _) =>
        {
            toleranceTimer.Stop();
            CommitTolerance();
        };
        _toleranceStepper.ValueChanged += (_, _) =>
        {
            if (_isRefreshing) return;
            toleranceTimer.Stop();
            toleranceTimer.Start();
        };
        _toleranceStepper.LostFocus += (_, _) =>
        {
            toleranceTimer.Stop();
            CommitTolerance();
        };

        _terrainOpacityStepper.DecimalPlaces = 0;
        _terrainOpacityStepper.Increment = 5;
        _terrainOpacityStepper.MinValue = 0;
        _terrainOpacityStepper.MaxValue = 100;
        ApplyHelp(_terrainOpacityStepper, "Terrain opacity used for the preview and the baked terrain mesh.");
        ApplyHelp(_terrainOpacitySlider, "Drag to adjust terrain opacity for preview and bake.");
        ApplyHelp(_terrainColorSwatch, "Click to pick the terrain display color.");
        _terrainOpacityStepper.GotFocus += (_, _) => BeginControllerRefreshDeferral();
        _terrainColorSwatch.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                OnPickTerrainColor(_, EventArgs.Empty);
        };
        _terrainOpacityStepper.ValueChanged += (_, _) =>
        {
            if (_isRefreshing || _isUpdatingOpacityControls)
                return;

            int opacityPercent = (int)Math.Round(_terrainOpacityStepper.Value);
            SetTerrainOpacityControls(opacityPercent);
            ApplyTerrainOpacity(opacityPercent);
        };
        _terrainOpacityStepper.LostFocus += (_, _) =>
        {
            if (!_isRefreshing)
            {
                int opacityPercent = (int)Math.Round(_terrainOpacityStepper.Value);
                SetTerrainOpacityControls(opacityPercent);
                ApplyTerrainOpacity(opacityPercent);
            }
            EndControllerRefreshDeferral();
        };
        _terrainOpacityStepper.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter || _isRefreshing)
                return;

            int opacityPercent = (int)Math.Round(_terrainOpacityStepper.Value);
            SetTerrainOpacityControls(opacityPercent);
            ApplyTerrainOpacity(opacityPercent);
            EndControllerRefreshDeferral();
            e.Handled = true;
        };
        _terrainOpacitySlider.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                BeginControllerRefreshDeferral();
        };
        _terrainOpacitySlider.MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                EndControllerRefreshDeferral();
        };
        _terrainOpacitySlider.LostFocus += (_, _) => EndControllerRefreshDeferral();
        _terrainOpacitySlider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing || _isUpdatingOpacityControls)
                return;

            int opacityPercent = _terrainOpacitySlider.Value;
            SetTerrainOpacityControls(opacityPercent);
            ApplyTerrainOpacity(opacityPercent);
        };
        ApplyHelp(
            _previewLineWeightSlider,
            "Thickness of every line this terrain previews - contours, waterflow, sections, annotation. " +
            "Relative to each output's drawing weight, so the hierarchy is preserved. Display only: baked " +
            "geometry keeps the print width of its layer.");
        _previewLineWeightSlider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            double weight = _previewLineWeightSlider.Value / 10.0;
            _previewLineWeightValue.Text = weight.ToString("0.0", CultureInfo.CurrentCulture) + "x";
            MutateSelectedTerrainLive(terrain => terrain.PreviewLineWeight = weight);
        };
        _previewLineWeightSlider.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                BeginControllerRefreshDeferral();
        };
        _previewLineWeightSlider.MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                EndControllerRefreshDeferral();
        };
        _previewLineWeightSlider.LostFocus += (_, _) => EndControllerRefreshDeferral();
        ApplyHelp(_showWiresCheck, "Show or hide MoleHill terrain mesh wires in preview and generated terrain meshes.");
        _showWiresCheck.CheckedChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            MutateSelectedTerrain(terrain => terrain.ShowMeshWires = _showWiresCheck.Checked == true, scheduleRebuild: false);
            var doc = RhinoDoc.ActiveDoc;
            var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
            if (doc != null && terrain != null)
                _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
        };
        ApplyHelp(_showSlowBuildWarningCheck, "Warn before preview or exact rebuild when recent timings or mesh size suggest this terrain may be slow to process.");
        _showSlowBuildWarningCheck.CheckedChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            MutateSelectedTerrain(terrain => terrain.ShowSlowBuildWarning = _showSlowBuildWarningCheck.Checked != false, scheduleRebuild: false);
        };
        ApplyHelp(_replacePreviousBakesCheck, "When baking, delete this terrain's previously baked document objects before adding the new bake set.");
        _replacePreviousBakesCheck.CheckedChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            MutateSelectedTerrain(terrain => terrain.ReplacePreviouslyBaked = _replacePreviousBakesCheck.Checked == true, scheduleRebuild: false);
        };
        ApplyHelp(_untrackSelectedBakesButton, "Remove the selected baked objects from this terrain's tracked bake set so future replace-bakes leave them alone.");
        _untrackSelectedBakesButton.Click += OnUntrackSelectedBakes;
        ApplyHelp(_untrackAllBakesButton, "Forget all baked objects currently tracked by this terrain without deleting them.");
        _untrackAllBakesButton.Click += OnUntrackAllBakes;

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

        SubscribeControllerStateChanged();
        LoadComplete += OnPanelLoadComplete;
        UnLoad += OnPanelUnLoad;
        RhinoApp.AppSettingsChanged += OnAppSettingsChanged;

        Content = BuildContent();
        RefreshUi();
    }

    private Control BuildContent()
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

        // ── Settings card (collapsible, expanded by default) ─────────
        var settingsDescription = UiControls.Label("Document defaults, layers, and terrain display", UiLabelRole.Meta);
        var settingsHeader = new SectionHeader(
            "Terrain Settings",
            settingsDescription,
            _settingsExpanded,
            expanded =>
            {
                _settingsExpanded = expanded;
                _settingsContent!.Visible = expanded;
            });
        settingsHeader.SizeChanged += (_, _) => settingsDescription.Visible = settingsHeader.Width <= 0 || settingsHeader.Width >= UiMetrics.HeaderStatusBreak;

        // Output routing and appearance both live in the layer template now, so the panel names the
        // template and opens the editor rather than carrying a layer picker per destination.
        var layerTemplateEditButton = MakeInlineButton(
            "Edit…",
            (_, _) => OnEditLayerTemplate(),
            "Edit the layer template: which layer each kind of output goes to, and how it looks.");
        var layerTemplateApplyButton = MakeInlineButton(
            "Apply",
            (_, _) => OnApplyLayerTemplate(),
            "Create this template's layers in the document. Existing layers are left as they are.");
        var layerTemplateControls = CreateResponsivePrimaryActionRow(
            _layerTemplateLabel,
            UiMetrics.SpaceSmall,
            layerTemplateEditButton,
            layerTemplateApplyButton);
        var layerTemplateRow = new PropertyRow(
            CreateHelpLabel(
                "Output Layers",
                "The layer template this terrain routes and styles its output through. The document "
                    + "keeps its own copy, so it looks the same wherever it is opened.",
                0),
            layerTemplateControls,
            expandWidget: true);

        var slopeUnitRow = CreateDropDownEditor(
            "Slope Units",
            SlopeUnitPreference.Choices
                .Select(unit => (AnalysisFormatting.GetSlopeUnitKey(unit), SlopeUnitLabel(unit)))
                .ToList(),
            AnalysisFormatting.GetSlopeUnitKey(SlopeUnitPreference.Current),
            OnSlopeUnitChanged,
            "The unit every slope field is shown in — grading batters, the stair daylight slope, the "
                + "scatter slope filter. Whatever is selected here, a slope field still accepts any unit "
                + "typed into it (25%, 1:3, 50‰, 14°) and converts. This is a personal display "
                + "preference: it is not saved into the document and never changes the terrain.");

        var toleranceRow = new PropertyRow(
            CreateHelpLabel("Detail Size", "Smallest terrain detail to preserve automatically. Smaller values keep more detail; larger values simplify and merge nearby geometry more aggressively.", 0),
            _toleranceStepper);
        var opacityLabel = CreateHelpLabel("Opacity", "Terrain opacity used for preview and bake.", UiMetrics.ShortLabel);
        var resetTerrainColorButton = MakeInlineButton("Reset", (_, _) => ResetTerrainColor(), "Restore the default terrain display color.");
        var terrainColorPrimary = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceSmall,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                _terrainColorSwatch,
                opacityLabel,
                new StackLayoutItem(_terrainOpacitySlider, expand: true)
            }
        };
        // The stepper + reset button drop to their own line before the slider gets squeezed to nothing.
        var terrainColorControls = new AdaptivePrimaryActionRow(terrainColorPrimary, UiMetrics.SpaceSmall, _terrainOpacityStepper, resetTerrainColorButton);
        var terrainColorRow = new PropertyRow(
            CreateHelpLabel("Terrain Color", "Base display color for the terrain preview and baked terrain. Click the swatch to change. Opacity affects this terrain mesh only.", 0),
            terrainColorControls,
            expandWidget: true);
        var terrainDisplayRow = new PropertyRow(
            new Panel(),
            new AdaptiveControlGroup(UiMetrics.SpaceSmall, _showWiresCheck, _showSlowBuildWarningCheck),
            expandWidget: true);
        var previewLineWeightRow = new PropertyRow(
            CreateHelpLabel(
                "Preview Line Weight",
                "On-screen only: multiplies the thickness of previewed lines so a busy plan stays "
                    + "readable on a dense display. Everything else previews exactly as it bakes, so "
                    + "any value other than 1.0x makes the preview deliberately heavier than the print.",
                0),
            new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceMedium,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items =
                {
                    new StackLayoutItem(_previewLineWeightSlider, expand: true),
                    _previewLineWeightValue
                }
            },
            expandWidget: true);
        var bakeTrackingControls = new AdaptiveControlGroup(UiMetrics.SpaceSmall, _replacePreviousBakesCheck, _untrackSelectedBakesButton, _untrackAllBakesButton);
        var bakeTrackingRow = new PropertyRow(
            CreateHelpLabel("Tracking", "Replace previous bake sets automatically, or untrack baked objects you want to keep.", 0),
            bakeTrackingControls,
            expandWidget: true);
        var settingsInner = new StackLayout
        {
            Orientation = Orientation.Vertical, Spacing = UiMetrics.SpaceSmall, Padding = new Padding(UiMetrics.CardHorizontalPadding, UiMetrics.SpaceLarge),
            Items =
            {
                new StackLayoutItem(terrainColorRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(terrainDisplayRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(previewLineWeightRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(toleranceRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(slopeUnitRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(bakeTrackingRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(layerTemplateRow, HorizontalAlignment.Stretch)
            }
        };

        _settingsContent = new Panel { Content = settingsInner, Visible = _settingsExpanded, BackgroundColor = UiTheme.CardBackground };

        var settingsCardBody = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Items =
            {
                new StackLayoutItem(settingsHeader, HorizontalAlignment.Stretch),
                new StackLayoutItem(_settingsContent, HorizontalAlignment.Stretch)
            }
        };
        // Same left accent strip + outer padding every stack card uses, so this reads as one more card
        // in the stack rather than a plain toolbar block. Neutral gray (ZoneStripColor) since it isn't a
        // typed card with its own accent color.
        var settingsStrip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = UiTheme.ZoneStripColor };
        var settingsCard = WrapCardControl(settingsCardBody, settingsStrip, UiTheme.CardBackground);

        // ── Status card (collapsible, collapsed by default) ───────────
        var statusHeader = new SectionHeader(
            "Status",
            _statusHintLabel,
            _statusExpanded,
            expanded =>
            {
                _statusExpanded = expanded;
                _statusContent!.Visible = expanded;
                _statusHintLabel.Visible = !expanded;
            });

        StyleTextArea(_statusTextArea);
        var copyStatusButton = MakeInlineButton("Copy Log", (_, _) => CopyStatusLog(), "Copy the full build log to the clipboard.");
        var copyCaseButton = MakeInlineButton("Copy Case", (_, _) => CopyCaseBundle(), "Export a repro case bundle and copy a runnable core xUnit test source when one can be generated.");
        _statusContent = new Panel
        {
            Content = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = UiMetrics.SpaceMedium,
                Padding = new Padding(UiMetrics.CardHorizontalPadding, UiMetrics.SpaceMedium),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(_statusTextArea, HorizontalAlignment.Stretch),
                    CreateResponsiveControlGroup(UiMetrics.SpaceSmall, copyStatusButton, copyCaseButton)
                }
            },
            Visible = _statusExpanded,
            BackgroundColor = UiTheme.CardBackground
        };

        var statusCard = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Padding = new Padding(UiMetrics.SpaceLarge, UiMetrics.SpaceXSmall),
            Items =
            {
                new StackLayoutItem(statusHeader, HorizontalAlignment.Stretch),
                new StackLayoutItem(_statusContent, HorizontalAlignment.Stretch)
            }
        };

        var top = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(toolbar, HorizontalAlignment.Stretch),
                new StackLayoutItem(settingsCard, HorizontalAlignment.Stretch),
                new StackLayoutItem(statusCard, HorizontalAlignment.Stretch)
            }
        };

        // ── Custom tab strip with eye toggles on Zones and Analysis ──────
        var tabScrollables = new[]
        {
            BuildScrollable(_modifierStack),
            BuildScrollable(_objectsStack),
            BuildScrollable(_zonesStack),
            BuildScrollable(_analysisStack),
            BuildScrollable(_annotationStack)
        };

        _tabScrollables = tabScrollables;
        _tabContentPanel = new Panel { Content = tabScrollables[Math.Clamp(_selectedTabIndex, 0, tabScrollables.Length - 1)] };

        void SelectTab(int index)
        {
            _selectedTabIndex = Math.Clamp(index, 0, tabScrollables.Length - 1);
            _tabContentPanel.Content = tabScrollables[_selectedTabIndex];
            RebuildVisibleTabLayout();
        }

        _tabChipMap.Clear();
        _tabChipLabelMap.Clear();

        void UpdateTabSelectionStyles()
        {
            foreach (var (tabIndex, panel) in _tabChipMap)
                panel.BackgroundColor = tabIndex == _selectedTabIndex ? UiTheme.ListSelectionBackground : UiTheme.ToolbarBackground;

            foreach (var (tabIndex, label) in _tabChipLabelMap)
                label.TextColor = tabIndex == _selectedTabIndex ? UiTheme.PrimaryText : UiTheme.MutedText;
        }

        Label MakeTabToggleLabel(string toolTip, Action onClick)
        {
            var label = UiControls.Label(string.Empty);
            label.TextAlignment = TextAlignment.Center;
            label.Width = UiMetrics.Chs(2);
            ApplyHelp(label, toolTip);
            label.MouseDown += (_, e) =>
            {
                if (e.Buttons != MouseButtons.Primary)
                    return;

                onClick();
            };
            return label;
        }

        Control MakeTabHeader(string text, string iconName, int tabIndex, Label? toggleLabel = null)
        {
            var textLabel = UiControls.Label(text);
            var mainRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceSmall,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            var icon = PanelIcons.Load(iconName);
            ImageView? iconView = null;
            if (icon != null)
            {
                iconView = new ImageView
                {
                    Image = icon,
                    Width = UiMetrics.IconSize,
                    Height = UiMetrics.IconSize,
                    Size = new Size(UiMetrics.IconSize, UiMetrics.IconSize)
                };
                mainRow.Items.Add(iconView);
            }

            mainRow.Items.Add(textLabel);
            var mainPanel = new Panel
            {
                Padding = toggleLabel == null
                    ? new Padding(UiMetrics.SpaceLarge, 5)
                    : new Padding(UiMetrics.SpaceLarge, 5, UiMetrics.SpaceMedium, 5),
                Content = mainRow
            };

            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(mainPanel, expand: true)
                }
            };

            if (toggleLabel != null)
            {
                var divider = new Panel
                {
                    Width = 1,
                    BackgroundColor = UiTheme.MutedText
                };
                var togglePanel = new Panel
                {
                    Padding = new Padding(UiMetrics.SpaceMedium, 5, UiMetrics.SpaceLarge, 5),
                    Content = toggleLabel
                };

                row.Items.Add(divider);
                row.Items.Add(togglePanel);
            }

            var inner = new Panel
            {
                Content = row
            };
            var outer = new Panel
            {
                BackgroundColor = UiTheme.HeaderBackground,
                Padding = new Padding(1),
                Content = inner
            };

            void SelectThisTab()
            {
                SelectTab(tabIndex);
                UpdateTabSelectionStyles();
            }

            mainPanel.MouseDown += (_, e) =>
            {
                if (e.Buttons == MouseButtons.Primary)
                    SelectThisTab();
            };
            mainRow.MouseDown += (_, e) =>
            {
                if (e.Buttons == MouseButtons.Primary)
                    SelectThisTab();
            };
            textLabel.MouseDown += (_, e) =>
            {
                if (e.Buttons == MouseButtons.Primary)
                    SelectThisTab();
            };
            if (iconView != null)
            {
                iconView.MouseDown += (_, e) =>
                {
                    if (e.Buttons == MouseButtons.Primary)
                        SelectThisTab();
                };
            }

            _tabChipMap[tabIndex] = inner;
            _tabChipLabelMap[tabIndex] = textLabel;

            return outer;
        }

        Label MakeZonesViewButton()
        {
            var label = MakeTabToggleLabel("Toggle between terrain mesh and zone meshes.", () =>
            {
                var doc = RhinoDoc.ActiveDoc;
                var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
                if (doc == null || terrain == null)
                    return;

                bool showZonesOnly = terrain.ShowZoneMeshes && !terrain.ShowTerrainMesh;
                MutateSelectedTerrain(t =>
                {
                    t.ShowTerrainMesh = showZonesOnly;
                    t.ShowZoneMeshes = !showZonesOnly;
                }, scheduleRebuild: false);
                _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
                RefreshUi();
            });
            return label;
        }

        Label MakeAnalysisVisibilityButton()
        {
            var label = MakeTabToggleLabel("Toggle analysis colors and analysis-owned outputs.", () =>
            {
                var doc = RhinoDoc.ActiveDoc;
                var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
                if (doc == null || terrain == null)
                    return;

                bool showAnalysis = !terrain.ShowAnalysisOutputs;
                MutateSelectedTerrain(t => t.ShowAnalysisOutputs = showAnalysis, scheduleRebuild: false);
                RefreshTerrainPreview(terrain.TerrainId);
                RefreshUi();
            });
            return label;
        }

        _zonesEyeButton = MakeZonesViewButton();
        _analysisEyeButton = MakeAnalysisVisibilityButton();

        var tabStrip = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceXSmall,
            Padding = new Padding(UiMetrics.SpaceSmall, UiMetrics.SpaceXSmall, UiMetrics.SpaceSmall, 0),
            VerticalContentAlignment = VerticalAlignment.Bottom,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(MakeTabHeader("Modifiers", "TabModifiers", 0), expand: true),
                new StackLayoutItem(MakeTabHeader("Objects", "TabObjects", 1), expand: true),
                new StackLayoutItem(MakeTabHeader("Zones", "TabZones", 2, _zonesEyeButton), expand: true),
                new StackLayoutItem(MakeTabHeader("Analysis", "TabAnalysis", 3, _analysisEyeButton), expand: true),
                new StackLayoutItem(MakeTabHeader("Annotation", "TabAnnotation", 4), expand: true)
            }
        };
        UpdateTabSelectionStyles();

        // Icons-only once the panel is too narrow for five full labels (Blender-style: shrink to fit,
        // never wrap/clip). Threshold is per-tab share of the strip width against a "short label" budget.
        void UpdateTabTextVisibility()
        {
            int tabCount = _tabChipLabelMap.Count;
            if (tabCount == 0)
                return;

            bool showText = tabStrip.Width / tabCount >= UiMetrics.Chs(9);
            foreach (var label in _tabChipLabelMap.Values)
                label.Visible = showText;
        }

        tabStrip.SizeChanged += (_, _) => UpdateTabTextVisibility();
        tabStrip.Shown += (_, _) => UpdateTabTextVisibility();

        var tabsContainer = new DynamicLayout();
        tabsContainer.Add(tabStrip, yscale: false);
        tabsContainer.Add(_tabContentPanel, yscale: true);

        var layout = new DynamicLayout();
        layout.Add(top, yscale: false);
        layout.Add(tabsContainer, yscale: true);
        return layout;
    }

    private static Scrollable BuildScrollable(Control content)
    {
        var scrollable = new Scrollable
        {
            Content = content,
            Border = BorderType.None,
            ExpandContentWidth = false,
            ExpandContentHeight = false
        };
        UiControls.DisableHorizontalScrolling(scrollable);

        // ExpandContentWidth alone leaves a persistent few-px horizontal scrollbar once the vertical
        // scrollbar appears (the WPF backend measures the expand width before reserving the vertical
        // scrollbar's own width). Pin content width to the scrollable's actual client area instead —
        // ClientSize already excludes a visible vertical scrollbar, so this is the width cards should
        // really lay out against.
        void SyncContentWidth()
        {
            int width = scrollable.ClientSize.Width;
            if (width > 0 && content.Width != width)
                content.Width = width;
        }

        scrollable.SizeChanged += (_, _) => SyncContentWidth();
        scrollable.Shown += (_, _) => SyncContentWidth();

        return scrollable;
    }

    private void CaptureSelectedTabIndex()
    {
        // Tab index is now tracked directly in _selectedTabIndex via the custom tab strip.
    }

    private void SubscribeControllerStateChanged()
    {
        if (_isStateChangedSubscribed)
            return;

        _controller.StateChanged += _stateChangedHandler;
        _isStateChangedSubscribed = true;
    }

    private void UnsubscribeControllerStateChanged()
    {
        if (!_isStateChangedSubscribed)
            return;

        _controller.StateChanged -= _stateChangedHandler;
        _isStateChangedSubscribed = false;
    }

    /// <summary>
    /// Coalesces controller state changes into one refresh per trip through the message loop.
    ///
    /// One terrain edit raises <c>StateChanged</c> several times - scheduling the rebuild, starting it,
    /// applying it - and each used to post its own <see cref="RefreshUi"/>. That matters far beyond the
    /// panel, because those refreshes share a queue with the finished build's completion callback:
    /// measured in an interactive Rhino on 2026-09-19, a single edit spent **287 ms in two panel
    /// refreshes** inside the 263 ms the completed terrain sat waiting to be published. The panel was
    /// the reason the terrain was late.
    ///
    /// The flag clears before the refresh runs, so a state change raised *during* a refresh still gets
    /// one of its own rather than being swallowed.
    /// </summary>
    private void HandleControllerStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !_isPanelLoaded || _controllerRefreshPosted)
            return;

        _controllerRefreshPosted = true;
        Application.Instance?.AsyncInvoke(() =>
        {
            _controllerRefreshPosted = false;
            if (IsDisposed || !_isPanelLoaded)
                return;

            if (_deferredControllerRefreshDepth > 0)
            {
                _hasDeferredControllerRefresh = true;
                return;
            }

            RefreshUi();
        });
    }

    private void OnPanelLoadComplete(object? sender, EventArgs e)
    {
        _isPanelLoaded = true;
        SubscribeControllerStateChanged();

        if (!IsDisposed)
        {
            Application.Instance?.AsyncInvoke(() =>
            {
                if (IsDisposed)
                    return;

                Content = BuildContent();
                RefreshUi();
            });
        }
    }

    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !_isPanelLoaded)
            return;

        Application.Instance?.AsyncInvoke(() =>
        {
            if (IsDisposed)
                return;

            // Rebuild the full content tree so static surfaces (toolbar, settings
            // card) pick up the new theme colors alongside the dynamic card stacks.
            UiMetrics.Invalidate();
            _terrainColorLabel.TextColor = UiTheme.MutedText;
            _statusHintLabel.TextColor   = UiTheme.MutedText;
            CaptureSelectedTabIndex();
            Content = BuildContent();
            RefreshUi();
        });
    }

    private void OnPanelUnLoad(object? sender, EventArgs e)
    {
        _isPanelLoaded = false;
        UnsubscribeControllerStateChanged();
        RhinoApp.AppSettingsChanged -= OnAppSettingsChanged;
    }

    private void BeginControllerRefreshDeferral()
    {
        if (_deferredControllerRefreshDepth == 0 && RhinoDoc.ActiveDoc is { } doc)
            _controller.BeginTerrainEditGesture(doc);
        _deferredControllerRefreshDepth++;
    }

    private void EndControllerRefreshDeferral()
    {
        if (_deferredControllerRefreshDepth <= 0)
            return;

        _deferredControllerRefreshDepth--;
        if (_deferredControllerRefreshDepth == 0 && RhinoDoc.ActiveDoc is { } doc)
            _controller.EndTerrainEditGesture(doc);
        if (_deferredControllerRefreshDepth != 0 || !_hasDeferredControllerRefresh || IsDisposed || !_isPanelLoaded)
            return;

        _hasDeferredControllerRefresh = false;
        RefreshUi();
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

    private void ApplyTerrainOpacity(int opacityPercent)
    {
        MutateSelectedTerrainLive(
            terrain => terrain.TerrainColorArgb = WithOpacityPercent(terrain.TerrainColorArgb, opacityPercent));
    }

    private void SetTerrainOpacityControls(int opacityPercent)
    {
        _isUpdatingOpacityControls = true;
        try
        {
            _terrainOpacityStepper.Value = opacityPercent;
            _terrainOpacitySlider.Value = opacityPercent;
        }
        finally
        {
            _isUpdatingOpacityControls = false;
        }
    }

    private void OnPickTerrainColor(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        var colorDialog = new ColorDialog
        {
            Color = ToEtoColor(System.Drawing.Color.FromArgb(terrain.TerrainColorArgb))
        };

        if (colorDialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok)
            return;

        int opacityPercent = GetOpacityPercent(terrain.TerrainColorArgb);
        MutateSelectedTerrain(item => item.TerrainColorArgb = WithOpacityPercent(ToArgb(colorDialog.Color), opacityPercent), scheduleRebuild: false);
        _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
    }

    private void ResetTerrainColor()
    {
        MutateSelectedTerrain(terrain => terrain.TerrainColorArgb = TerrainDefinition.DefaultTerrainColorArgb, scheduleRebuild: false);
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc != null && terrain != null)
            _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
    }

    /// <summary>
    /// Switches the unit every slope field reads and writes in. Nothing about the terrain changes — the
    /// definitions still store degrees — so this saves no document and schedules no rebuild; it only
    /// relabels and reformats the cards, which a plain refresh already does by marking every tab stale.
    /// </summary>
    private void OnSlopeUnitChanged(string key)
    {
        SlopeAnalyzer.SlopeUnit unit = AnalysisFormatting.ParseSlopeUnit(key);
        if (unit == SlopeUnitPreference.Current)
            return;

        SlopeUnitPreference.Current = unit;
        RefreshUi();
    }

    /// <summary>Dropdown text for a slope unit: the name plus the symbol the fields will show.</summary>
    private static string SlopeUnitLabel(SlopeAnalyzer.SlopeUnit unit)
    {
        return unit == SlopeAnalyzer.SlopeUnit.Ratio
            ? "Ratio (1:3)"
            : $"{SlopeInput.Name(unit)} ({SlopeInput.Suffix(unit)})";
    }

    private void RefreshUi()
    {
        if (IsDisposed)
            return;

        // Timed because this is the largest piece of UI-thread work MoleHill schedules per edit, and a
        // finished build's completion callback queues behind it. See TerrainUiThreadProbe.
        long refreshStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            RefreshUiCore();
        }
        finally
        {
            Services.TerrainUiThreadProbe.RecordPanelRefresh(
                System.Diagnostics.Stopwatch.GetTimestamp() - refreshStart);
        }
    }

    private void RefreshUiCore()
    {
        // Keep shared label instances in sync with the current theme.
        _terrainColorLabel.TextColor = UiTheme.MutedText;
        _statusHintLabel.TextColor   = UiTheme.MutedText;

        var doc = RhinoDoc.ActiveDoc;
        _isRefreshing = true;
        try
        {
            if (doc == null)
            {
                _newButton.Enabled = false;
                _terrainSelector.Text = string.Empty;
                _liveUpdate.Checked = false;
                SetStatusText("No active Rhino document.");
                _layerTemplateLabel.Text = "-";
                _terrainColorSwatch.BackgroundColor = Color.FromArgb(80, 80, 80);
                _terrainColorLabel.Text = "-";
                _statusHintLabel.Text = string.Empty;
                SetTerrainOpacityControls(100);
                _showWiresCheck.Checked = false;
                SetPreviewLineWeightControls(1.0);
                _showSlowBuildWarningCheck.Checked = true;
                _replacePreviousBakesCheck.Checked = false;
                _untrackSelectedBakesButton.Enabled = false;
                _untrackAllBakesButton.Enabled = false;
                _toleranceStepper.Value = 0;
                _isUpdatingTerrainSelector = true;
                _terrainSelector.Items.Clear();
                _terrainSelector.SelectedIndex = -1;
                _isUpdatingTerrainSelector = false;
                SetButtonIcon(_visibilityButton, PanelButtonIcon.HideOff, muted: true);
                _visibilityButton.ToolTip = "Terrain visible. Click to hide.";
                SetButtonIcon(_lockButton, PanelButtonIcon.Unlock, muted: true);
                _lockButton.ToolTip = "Terrain unlocked. Click to lock.";
                SetActionButtonsEnabled(false);
                _terrainSelector.Enabled = false;
                _modifierStack.Items.Clear();
                _objectsStack.Items.Clear();
                _zonesStack.Items.Clear();
                _analysisStack.Items.Clear();
                _annotationStack.Items.Clear();
                _resetTerrainDataButton.Visible = false;
                return;
            }

            var terrains = _controller.GetTerrains(doc).ToList();
            bool hasModelUnits = MoleHill.Shared.ModelUnitContext.FromDocument(doc).IsSupported;
            _newButton.Enabled = hasModelUnits;
            _resetTerrainDataButton.Visible = _controller.IsTerrainDataUnreadable(doc);
            if (_resetTerrainDataButton.Visible)
                SetStatusText("Terrain data in this document could not be read and is being preserved untouched. " +
                    "Use \"Reset Terrain Data\" above to discard it, or reopen this document with a compatible MoleHill version.");

            var selectedTerrain = _controller.GetSelectedTerrain(doc);
            if (selectedTerrain == null && terrains.Count > 0)
            {
                _controller.SetSelectedTerrain(doc, terrains[0].TerrainId);
                selectedTerrain = terrains[0];
            }

            _isUpdatingTerrainSelector = true;
            _terrainSelector.Items.Clear();
            int selectedPickerIndex = 0;
            for (int ti = 0; ti < terrains.Count; ti++)
            {
                _terrainSelector.Items.Add(new ListItem
                {
                    Text = terrains[ti].Name,
                    Key = terrains[ti].TerrainId.ToString("D")
                });
                if (selectedTerrain != null && terrains[ti].TerrainId == selectedTerrain.TerrainId)
                    selectedPickerIndex = ti;
            }
            _terrainSelector.SelectedIndex = terrains.Count > 0 ? selectedPickerIndex : -1;
            _terrainSelector.Text = selectedTerrain?.Name ?? string.Empty;
            _isUpdatingTerrainSelector = false;
            _liveUpdate.Checked = selectedTerrain?.LiveUpdateEnabled ?? false;
            RefreshLayerTemplateLabel(doc, selectedTerrain);
            int terrainColorArgb = selectedTerrain?.TerrainColorArgb ?? TerrainDefinition.DefaultTerrainColorArgb;
            var terrainColor = ToEtoColor(System.Drawing.Color.FromArgb(terrainColorArgb));
            _terrainColorSwatch.BackgroundColor = terrainColor;
            _terrainColorLabel.Text = DescribeTerrainColor(terrainColorArgb);
            SetTerrainOpacityControls(GetOpacityPercent(terrainColorArgb));
            _showWiresCheck.Checked = selectedTerrain?.ShowMeshWires ?? false;
            SetPreviewLineWeightControls(selectedTerrain?.PreviewLineWeight ?? 1.0);
            _showSlowBuildWarningCheck.Checked = selectedTerrain?.ShowSlowBuildWarning ?? true;
            _replacePreviousBakesCheck.Checked = selectedTerrain?.ReplacePreviouslyBaked ?? false;
            _toleranceStepper.Value = selectedTerrain?.GlobalTolerance ?? 0;
            if (hasModelUnits)
            {
                SetStatusText(
                    selectedTerrain?.LastBuildMessage ?? "Create a terrain to start.",
                    selectedTerrain?.LastStructuredDiagnostics);
            }
            else
            {
                SetStatusText(ModelUnitGuard.RequiredMessage);
            }
            bool terrainVisible = selectedTerrain?.IsVisible != false;
            SetButtonIcon(_visibilityButton, terrainVisible ? PanelButtonIcon.HideOff : PanelButtonIcon.HideOn, muted: false);
            _visibilityButton.ToolTip = terrainVisible
                ? "Terrain visible. Click to hide."
                : "Terrain hidden. Click to show.";
            bool terrainLocked = selectedTerrain?.IsLocked == true;
            SetButtonIcon(_lockButton, terrainLocked ? PanelButtonIcon.Lock : PanelButtonIcon.Unlock, muted: !terrainLocked);
            _lockButton.ToolTip = terrainLocked
                ? "MoleHill live outputs locked. Click to unlock; source and baked objects are unaffected."
                : "MoleHill live outputs unlocked. Click to lock; source and baked objects are unaffected.";
            bool hasTerrain = selectedTerrain != null;
            SetActionButtonsEnabled(hasTerrain && hasModelUnits);
            bool hasTrackedBakes = selectedTerrain != null && selectedTerrain.BakedObjectIds.Count > 0;
            _untrackSelectedBakesButton.Enabled = hasTerrain && hasTrackedBakes;
            _untrackAllBakesButton.Enabled = hasTerrain && hasTrackedBakes;
            _terrainSelector.Enabled = hasTerrain;

            UpdateZonesTabButton(_zonesEyeButton, selectedTerrain);
            UpdateAnalysisTabButton(_analysisEyeButton, selectedTerrain);

            // Every tab's layout is now stale, but only the visible one is worth building now.
            _lastRefreshedTerrain = selectedTerrain;
            for (int tab = 0; tab < _tabLayoutDirty.Length; tab++)
                _tabLayoutDirty[tab] = true;

            RebuildVisibleTabLayout();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// Builds the visible tab's card stack if it is stale, keeping the scroll position.
    ///
    /// A rebuild replaces the whole stack, and the scrollable then snaps to the top — so editing anything
    /// below the fold threw the user back to the start of the list. The position is restored after the
    /// content is in place, clamped by the scrollable itself if the new content is shorter.
    /// </summary>
    /// <summary>
    /// Timed from the inside rather than at a call site. The first attempt wrapped the tab-selection
    /// caller and reported 0 ms against a 94 ms refresh, which isolated nothing - there are two callers
    /// and the refresh path is the other one.
    /// </summary>
    private void RebuildVisibleTabLayout()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            RebuildVisibleTabLayoutCore();
        }
        finally
        {
            Services.TerrainUiThreadProbe.RecordTabLayout(
                System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }

    private void RebuildVisibleTabLayoutCore()
    {
        if (_tabScrollables == null)
            return;

        int tab = Math.Clamp(_selectedTabIndex, 0, _tabLayoutDirty.Length - 1);
        if (!_tabLayoutDirty[tab])
            return;

        _tabLayoutDirty[tab] = false;

        Scrollable scrollable = _tabScrollables[tab];
        Point scroll = scrollable.ScrollPosition;

        bool wasRefreshing = _isRefreshing;
        _isRefreshing = true;
        try
        {
            switch (tab)
            {
                case 0: RebuildModifierLayout(_lastRefreshedTerrain); break;
                case 1: RebuildObjectsLayout(_lastRefreshedTerrain); break;
                case 2: RebuildZonesLayout(_lastRefreshedTerrain); break;
                case 3: RebuildAnalysisLayout(_lastRefreshedTerrain); break;
                default: RebuildAnnotationLayout(_lastRefreshedTerrain); break;
            }
        }
        finally
        {
            _isRefreshing = wasRefreshing;
        }

        if (scroll.Y > 0)
            Application.Instance.AsyncInvoke(() => RestoreScroll(scrollable, scroll));
    }

    /// <summary>Restores a scroll offset after layout has settled; the control may have shrunk.</summary>
    private void RestoreScroll(Scrollable scrollable, Point scroll)
    {
        if (IsDisposed || scrollable.Content == null)
            return;

        try
        {
            scrollable.ScrollPosition = new Point(0, scroll.Y);
        }
        catch
        {
            // A shorter stack can reject the offset outright; landing at the top is the correct fallback.
        }
    }

    private void RebuildModifierLayout(TerrainDefinition? terrain)
    {
        _modifierCardMap.Clear();
        _modifierSepMap.Clear();
        _modifierStripMap.Clear();
        _modifierStripColors.Clear();
        _modifierStack.Items.Clear();
        _modifierStack.Items.Add(new StackLayoutItem(BuildAddModifierBar(terrain), HorizontalAlignment.Stretch));

        if (terrain == null)
            return;

        var terrainId = terrain.TerrainId;
        foreach (var modifier in Enumerable.Reverse(terrain.Modifiers))
        {
            var modifierId = modifier.Id;

            var innerSep = new Panel { BackgroundColor = Colors.Transparent };
            var outerSep = new Panel { Height = 8, Padding = new Padding(0, 2), Content = innerSep };
            ApplyHelp(outerSep, "Drop here to reorder modifiers.");
            _modifierSepMap[modifierId] = innerSep;
            WireModifierSepDragDrop(outerSep, innerSep, terrainId, modifierId);
            _modifierStack.Items.Add(new StackLayoutItem(outerSep, HorizontalAlignment.Stretch));

            var box = CreateModifierCard(terrain, modifier);
            _modifierCardMap[modifierId] = box;
            var kind = GetModifierKind(modifier);
            var typeColor = ModifierTypeColor(kind);
            bool isPinnedBaseTriangulate = modifier is TriangulateModifierDefinition &&
                                           terrain.Modifiers.Count > 0 &&
                                           terrain.Modifiers[0].Id == modifierId;
            var strip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = typeColor };
            _modifierStripMap[modifierId] = strip;
            _modifierStripColors[modifierId] = typeColor;
            var wrapper = WrapCardControl(box, strip, isPinnedBaseTriangulate ? UiTheme.BaseCardBackground : UiTheme.CardBackground);
            WireModifierCardDragDrop(wrapper, terrainId, modifierId);
            _modifierStack.Items.Add(new StackLayoutItem(wrapper, HorizontalAlignment.Stretch));
        }

        var tailInner = new Panel { BackgroundColor = Colors.Transparent };
        var tailOuter = new Panel { Height = 8, Padding = new Padding(0, 2), Content = tailInner };
        ApplyHelp(tailOuter, "Drop here to reorder modifiers.");
        _modifierSepMap[Guid.Empty] = tailInner;
        WireModifierSepDragDrop(tailOuter, tailInner, terrainId, Guid.Empty);
        _modifierStack.Items.Add(new StackLayoutItem(tailOuter, HorizontalAlignment.Stretch));
    }

    private void RebuildObjectsLayout(TerrainDefinition? terrain)
    {
        _objectsStack.Items.Clear();
        if (terrain == null)
            return;

        _objectsStack.Items.Add(new StackLayoutItem(BuildObjectAddButtons(terrain), HorizontalAlignment.Stretch));
        if (terrain.Objects.Count == 0)
        {
            _objectsStack.Items.Add(new StackLayoutItem(new Panel
            {
                Padding = new Padding(8),
                Content = new Label
                {
                    Text = "No object definitions yet. Add one to project or orient Rhino objects onto the terrain while keeping their source layers.",
                    TextColor = UiTheme.MutedText,
                    Wrap = WrapMode.Word
                }
            }, HorizontalAlignment.Stretch));
            return;
        }

        foreach (var definition in terrain.Objects)
        {
            var box = CreateObjectCard(terrain, definition);
            var strip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = TerrainObjectTypeColor(GetTerrainObjectKind(definition)) };
            var wrapper = WrapCardControl(box, strip, UiTheme.CardBackground);
            _objectsStack.Items.Add(new StackLayoutItem(wrapper, HorizontalAlignment.Stretch));
        }
    }

    private void RebuildAnalysisLayout(TerrainDefinition? terrain)
    {
        _analysisCardMap.Clear();
        _analysisSepMap.Clear();
        _analysisStripMap.Clear();
        _analysisStripColors.Clear();
        _analysisStack.Items.Clear();

        if (terrain == null)
            return;

        _analysisStack.Items.Add(new StackLayoutItem(BuildAnalysisToolbar(terrain), HorizontalAlignment.Stretch));

        var visualAnalyses = terrain.Analyses;
        if (visualAnalyses.Count == 0)
        {
            _analysisStack.Items.Add(new StackLayoutItem(new Panel
            {
                Padding = new Padding(8),
                Content = new Label
                {
                    Text = "No analyses yet. Add one to inspect slope, elevation, cut/fill, or earthworks.",
                    TextColor = UiTheme.MutedText,
                    Wrap = WrapMode.Word
                }
            }, HorizontalAlignment.Stretch));
            return;
        }

        bool hasActiveAnalysis = false;
        var terrainId = terrain.TerrainId;
        foreach (var analysisItem in visualAnalyses)
        {
            bool isActive = !hasActiveAnalysis &&
                            analysisItem.IsEnabled &&
                            TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(analysisItem);
            if (isActive)
                hasActiveAnalysis = true;

            var analysisId = analysisItem.Id;
            var innerSep = new Panel { BackgroundColor = Colors.Transparent };
            var outerSep = new Panel { Height = 8, Padding = new Padding(0, 2), Content = innerSep };
            ApplyHelp(outerSep, "Drop here to reorder analyses.");
            _analysisSepMap[analysisId] = innerSep;
            WireAnalysisSepDragDrop(outerSep, innerSep, terrainId, analysisId);
            _analysisStack.Items.Add(new StackLayoutItem(outerSep, HorizontalAlignment.Stretch));

            var box = CreateAnalysisCard(terrain, analysisItem, isActive);
            _analysisCardMap[analysisId] = box;
            var kind = GetAnalysisKind(analysisItem);
            var typeColor = AnalysisTypeColor(kind);
            var strip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = typeColor };
            _analysisStripMap[analysisId] = strip;
            _analysisStripColors[analysisId] = typeColor;
            var wrapper = WrapCardControl(box, strip, UiTheme.CardBackground);
            WireAnalysisCardDragDrop(wrapper, terrainId, analysisId);
            _analysisStack.Items.Add(new StackLayoutItem(wrapper, HorizontalAlignment.Stretch));
        }

        var tailInner = new Panel { BackgroundColor = Colors.Transparent };
        var tailOuter = new Panel { Height = 8, Padding = new Padding(0, 2), Content = tailInner };
        ApplyHelp(tailOuter, "Drop here to reorder analyses.");
        _analysisSepMap[Guid.Empty] = tailInner;
        WireAnalysisSepDragDrop(tailOuter, tailInner, terrainId, Guid.Empty);
        _analysisStack.Items.Add(new StackLayoutItem(tailOuter, HorizontalAlignment.Stretch));
    }

    private void RebuildAnnotationLayout(TerrainDefinition? terrain)
    {
        _annotationCardMap.Clear();
        _annotationSepMap.Clear();
        _annotationStripMap.Clear();
        _annotationStripColors.Clear();
        _annotationStack.Items.Clear();

        if (terrain == null)
            return;

        _annotationStack.Items.Add(new StackLayoutItem(BuildAnnotationToolbar(terrain), HorizontalAlignment.Stretch));

        var annotationItems = terrain.Annotations;
        if (annotationItems.Count == 0)
        {
            _annotationStack.Items.Add(new StackLayoutItem(new Panel
            {
                Padding = new Padding(8),
                Content = new Label
                {
                    Text = "No annotations yet. Add contours, elevation labels, or slope labels.",
                    TextColor = UiTheme.MutedText,
                    Wrap = WrapMode.Word
                }
            }, HorizontalAlignment.Stretch));
            return;
        }

        var terrainId = terrain.TerrainId;
        foreach (var analysisItem in annotationItems)
        {
            var analysisId = analysisItem.Id;
            var innerSep = new Panel { BackgroundColor = Colors.Transparent };
            var outerSep = new Panel { Height = 8, Padding = new Padding(0, 2), Content = innerSep };
            ApplyHelp(outerSep, "Drop here to reorder annotations.");
            _annotationSepMap[analysisId] = innerSep;
            WireAnnotationSepDragDrop(outerSep, innerSep, terrainId, analysisId);
            _annotationStack.Items.Add(new StackLayoutItem(outerSep, HorizontalAlignment.Stretch));

            var box = CreateAnnotationCard(terrain, analysisItem);
            _annotationCardMap[analysisId] = box;
            var kind = GetAnnotationKind(analysisItem);
            var typeColor = AnnotationTypeColor(kind);
            var strip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = typeColor };
            _annotationStripMap[analysisId] = strip;
            _annotationStripColors[analysisId] = typeColor;
            var wrapper = WrapCardControl(box, strip, UiTheme.CardBackground);
            WireAnnotationCardDragDrop(wrapper, terrainId, analysisId);
            _annotationStack.Items.Add(new StackLayoutItem(wrapper, HorizontalAlignment.Stretch));
        }

        var tailInner = new Panel { BackgroundColor = Colors.Transparent };
        var tailOuter = new Panel { Height = 8, Padding = new Padding(0, 2), Content = tailInner };
        ApplyHelp(tailOuter, "Drop here to reorder annotations.");
        _annotationSepMap[Guid.Empty] = tailInner;
        WireAnnotationSepDragDrop(tailOuter, tailInner, terrainId, Guid.Empty);
        _annotationStack.Items.Add(new StackLayoutItem(tailOuter, HorizontalAlignment.Stretch));
    }

    private Control CreateSectionToolbar(string title, Button primaryButton, string? helperText = null, Control? trailingControl = null)
    {
        var section = new StackLayout
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
                UiControls.Label(title, UiLabelRole.Section)
            }
        };

        bool hasHelperText = !string.IsNullOrWhiteSpace(helperText);
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceLarge,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                primaryButton
            }
        };

        if (hasHelperText)
        {
            row.Items.Add(new StackLayoutItem(UiControls.Label(helperText!, UiLabelRole.Meta, WrapMode.Word), expand: true));
        }
        else
        {
            row.Items.Add(new StackLayoutItem(new Panel(), expand: true));
        }

        if (trailingControl != null)
            row.Items.Add(new StackLayoutItem(trailingControl));

        section.Items.Add(new StackLayoutItem(row, HorizontalAlignment.Stretch));
        return section;
    }

    private static Control CreateResponsiveControlGroup(int spacing, params Control[] controls) =>
        new AdaptiveControlGroup(spacing, controls);

    private Control CreateResponsivePrimaryActionRow(Control primaryControl, int spacing, params Control[] actionControls)
    {
        return new AdaptivePrimaryActionRow(primaryControl, spacing, actionControls);
    }

    private Control BuildAddModifierBar(TerrainDefinition? terrain)
    {
        var addButton = MakeToolbarButton("Add Modifier", (_, _) => { }, "Add a modifier above the base geometry");
        if (terrain != null)
        {
            var menu = new ContextMenu();
            foreach (var descriptor in TerrainTypeRegistry.Modifiers
                         .Where(d => d.CanCreateFromMenu)
                         .OrderBy(d => d.SortOrder))
            {
                var item = new ButtonMenuItem
                {
                    Text = descriptor.DisplayName,
                    Image = PanelIcons.Load(descriptor.IconName)
                };
                var capturedKind = descriptor.Kind;
                var capturedTerrainId = terrain.TerrainId;
                item.Click += (_, _) =>
                {
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc == null)
                        return;

                    _controller.AddModifier(doc, capturedTerrainId, capturedKind);
                    RebuildModifierLayout(_controller.GetSelectedTerrain(doc));
                };
                menu.Items.Add(item);
            }

            addButton.Click += (_, _) => menu.Show(addButton);
        }
        else
        {
            addButton.Enabled = false;
        }

        return CreateSectionToolbar(
            "MODIFIER STACK",
            addButton);
    }

    private Control BuildAnalysisToolbar(TerrainDefinition terrain)
    {
        var addButton = MakeToolbarButton("Add Analysis", (_, _) => { }, "Add an analysis card");
        var menu = new ContextMenu();
        foreach (var descriptor in AnalysisTypeRegistry.Analyses.OrderBy(item => item.SortOrder))
        {
            var item = new ButtonMenuItem { Text = descriptor.MenuLabel };
            var capturedKind = descriptor.Kind;
            item.Click += (_, _) => AddAnalysis(capturedKind);
            menu.Items.Add(item);
        }

        addButton.Click += (_, _) => menu.Show(addButton);
        return CreateSectionToolbar("ANALYSIS", addButton);
    }

    private Control BuildAnnotationToolbar(TerrainDefinition terrain)
    {
        var addButton = MakeToolbarButton("Add Annotation", (_, _) => { }, "Add an annotation card");
        var menu = new ContextMenu();
        foreach (var descriptor in AnnotationTypeRegistry.Annotations.OrderBy(item => item.SortOrder))
        {
            var item = new ButtonMenuItem { Text = descriptor.MenuLabel };
            var capturedKind = descriptor.Kind;
            item.Click += (_, _) => AddAnnotation(capturedKind);
            menu.Items.Add(item);
        }
        addButton.Click += (_, _) => menu.Show(addButton);
        return CreateSectionToolbar("ANNOTATION", addButton);
    }
    private static string EllipsizeText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars || maxChars <= 3)
            return text;

        return text[..(maxChars - 3)] + "...";
    }

    private Control CreateOptionalColorEditor(
        string label,
        int? colorArgb,
        Action<int?> onCommit,
        string help,
        int? fallbackColorArgb = null,
        string defaultText = "(by layer)")
    {
        var swatch = new Panel
        {
            Width = 18,
            Height = 18,
            BackgroundColor = ResolveOptionalColorSwatch(colorArgb, fallbackColorArgb)
        };
        ApplyHelp(swatch, help);

        string assignedText = colorArgb.HasValue ? DescribeSolidColor(colorArgb.Value) : defaultText;
        var assignedLabel = new Label
        {
            Text = EllipsizeText(assignedText, 24),
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.None
        };
        ApplyHelp(assignedLabel, $"{help}\n{assignedText}");

        void PickColor()
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            int initialArgb = colorArgb ?? fallbackColorArgb ?? unchecked((int)0xFF808080);
            var colorDialog = new ColorDialog
            {
                Color = ToEtoColor(System.Drawing.Color.FromArgb(initialArgb))
            };

            if (colorDialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok)
                return;

            onCommit(ToArgb(colorDialog.Color));
        }

        swatch.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                PickColor();
        };

        var pickButton = MakeInlineButton("Pick", (_, _) => PickColor(), "Choose an explicit color for this output.");
        var clearButton = MakeInlineButton("Clear", (_, _) => onCommit(null), "Clear the explicit color and use the layer color instead.");
        var summaryRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                swatch,
                new StackLayoutItem(assignedLabel, expand: true)
            }
        };
        var buttonRow = CreateResponsiveControlGroup(4, pickButton, clearButton);
        var editor = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(summaryRow, expand: true),
                buttonRow
            }
        };
        return new PropertyRow(CreateHelpLabel(label, help, 0), editor, expandWidget: true);
    }

    private void BindCommittedText(TextBox textBox, Func<string> getCurrentValue, Action<string> onCommit, bool trim = true)
    {
        void Commit()
        {
            if (_isRefreshing)
                return;

            string text = textBox.Text ?? string.Empty;
            if (trim)
                text = text.Trim();

            if (string.Equals(text, getCurrentValue(), StringComparison.Ordinal))
                return;

            onCommit(text);
        }

        textBox.LostFocus += (_, _) => Commit();
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
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

    private static string GetLeafLayerName(string layerPath) => AnalysisFormatting.GetLeafLayerName(layerPath);

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

    private static Button MakeButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null)
    {
        return UiControls.Button(text, onClick, toolTip);
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

    private static string GetNumericHelp(string label)
    {
        return label switch
        {
            "Detail Size" => "Changes are applied after a short pause. Smaller values keep more terrain detail; larger values simplify and merge nearby geometry more aggressively.",
            "Edge Length" => "Changes are applied after a short pause. Smaller values make denser triangles; larger values make coarser meshes.",
            "Max Area" => "Changes are applied after a short pause. Smaller values refine the mesh; larger values keep bigger faces.",
            "Min Angle" => "Changes are applied after a short pause. Around 20-30 is moderate; very high values can overconstrain the triangulation.",
            "Iterations" => "Changes are applied after a short pause. 1 is light smoothing; 2-4 is moderate; higher values flatten more detail.",
            "Strength" => "Changes are applied after a short pause. Around 0.2 is gentle, 0.5 is strong, and 1.0 is extreme.",
            "Fixity" => "Changes are applied after a short pause. 1.0 locks breaklines hard, 0.5 softens them, 0.0 ignores them.",
            "Slope Angle" => "Changes are applied after a short pause. Lower angles are flatter and spread farther; higher angles are steeper and tighter.",
            "Max Distance" => "Changes are applied after a short pause. 0 means unlimited; smaller values constrain the grading reach.",
            "Width" => "Changes are applied after a short pause. This is the controlled path width before side grading starts.",
            "Symbol Scale" => "Changes are applied after a short pause. 1.0 is default size; below 1.0 is smaller; above 1.0 is larger.",
            _ => $"{label}. Changes are applied after a short pause."
        };
    }

    private static Control CreateAnalysisGroup(string title, IEnumerable<(string Label, string Value)> rows)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 4), Padding = new Padding(6, 4) };
        foreach (var (label, value) in rows)
            layout.AddSeparateRow(new Label { Text = label, Width = UiMetrics.ShortLabel }, new Label { Text = value }, null);

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(CreateSectionRule(title), HorizontalAlignment.Stretch),
                new StackLayoutItem(layout, HorizontalAlignment.Stretch)
            }
        };
    }

    private static string FormatVolume(double value)
    {
        string prefix = value < 0 ? "-" : string.Empty;
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        if (!unitContext.IsSupported)
            unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
        return $"{prefix}{unitContext.FormatVolume(Math.Abs(value))}";
    }

    private static void UpdateZonesTabButton(Label? button, TerrainDefinition? terrain)
    {
        if (button == null)
            return;

        bool showZonesOnly = terrain?.ShowZoneMeshes == true && terrain.ShowTerrainMesh != true;
        button.Text = showZonesOnly ? "\u25A6" : "\u25A0";
        button.TextColor = terrain == null ? UiTheme.MutedText : UiTheme.PrimaryText;
        button.ToolTip = showZonesOnly
            ? "Showing zone meshes. Click to show the terrain mesh."
            : "Showing the terrain mesh. Click to show zone meshes.";
    }

    private static void UpdateAnalysisTabButton(Label? button, TerrainDefinition? terrain)
    {
        if (button == null)
            return;

        bool isVisible = terrain?.ShowAnalysisOutputs != false;
        button.Text = isVisible ? "\u25CF" : "\u25CB";
        button.TextColor = isVisible ? UiTheme.PrimaryText : UiTheme.MutedText;
        button.ToolTip = isVisible
            ? "Analysis preview is visible. Click to hide analysis colors and outputs."
            : "Analysis preview is hidden. Click to show analysis colors and outputs.";
    }

    private void ApplyHelp(Control control, string help)
    {
        control.ToolTip = help;
    }

    private void MoveModifier(Guid terrainId, Guid modifierId, int direction)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
            _controller.MoveModifier(doc, terrainId, modifierId, direction);
    }

    private void MoveAnalysis(Guid terrainId, Guid analysisId, int direction)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Analyses.FindIndex(item => item.Id == analysisId);
            if (index < 0)
                return;

            int targetIndex = Math.Clamp(index + direction, 0, terrain.Analyses.Count - 1);
            if (targetIndex == index)
                return;

            var analysis = terrain.Analyses[index];
            terrain.Analyses.RemoveAt(index);
            terrain.Analyses.Insert(targetIndex, analysis);
        }, scheduleRebuild: false);
    }

    private void RemoveAnalysis(Guid terrainId, Guid analysisId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            terrain.Analyses.RemoveAll(item => item.Id == analysisId);
        }, scheduleRebuild: false);
        RefreshUi();
    }

    private void AddAnalysis(string kind)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        MutateSelectedTerrain(terrain =>
        {
            AnalysisDefinition? analysis = AnalysisTypeRegistry.Create(
                kind,
                MoleHill.Shared.ModelUnitContext.FromDocument(doc));
            if (analysis != null)
                terrain.Analyses.Insert(0, analysis);
        }, scheduleRebuild: false);

        var terrain = _controller.GetSelectedTerrain(doc);
        if (terrain != null)
        {
            RefreshUi();
            RefreshTerrainPreview(terrain.TerrainId);
        }
    }

    private void MutateSelectedTerrain(
        Action<TerrainDefinition> mutator,
        bool scheduleRebuild = true,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.MutateTerrain(
            doc, terrain.TerrainId, mutator, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    /// <summary>
    /// A display setting being dragged: recolour the viewport, but do not serialize the document or
    /// rebuild the panel on every tick.
    ///
    /// Both are ruinously expensive to do per slider step — the save writes the whole terrain state as
    /// JSON into the .3dm, and the StateChanged it raises rebuilds every card in the panel. That is why
    /// the line-weight and opacity sliders felt like they were dragging through treacle. The save is
    /// debounced instead, so the value still persists a moment after the drag stops.
    /// </summary>
    private void MutateSelectedTerrainLive(Action<TerrainDefinition> mutator)
    {
        MutateSelectedTerrain(
            mutator,
            scheduleRebuild: false,
            deferDocumentSave: true,
            suppressImmediateUiRefresh: true);

        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc != null && terrain != null)
            _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
    }

    private void MutateModifier(
        Guid terrainId,
        Guid modifierId,
        Action<ModifierDefinition> mutator,
        bool scheduleRebuild = true,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var modifier = terrain.Modifiers.FirstOrDefault(item => item.Id == modifierId);
            if (modifier != null)
                mutator(modifier);
        }, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    private void MutateObjectDefinition(
        Guid terrainId,
        Guid definitionId,
        Action<TerrainObjectDefinition> mutator,
        bool scheduleRebuild = true,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var definition = terrain.Objects.FirstOrDefault(item => item.Id == definitionId);
            if (definition != null)
                mutator(definition);
        }, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    private void MutateAnalysis(
        Guid terrainId,
        Guid analysisId,
        Action<AnalysisDefinition> mutator,
        bool scheduleRebuild = false,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var analysis = terrain.Analyses.FirstOrDefault(item => item.Id == analysisId);
            if (analysis != null)
                mutator(analysis);
        }, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    private void MutateAndRefreshAnalysis(Guid terrainId, Guid analysisId, Action<AnalysisDefinition> mutator)
    {
        MutateAnalysis(terrainId, analysisId, mutator, scheduleRebuild: false);
        RefreshTerrainPreview(terrainId);
    }

    /// <summary>
    /// The mid-gesture version: recolour the preview but do not save the document or raise StateChanged.
    ///
    /// A plain analysis mutation saves immediately, and that save raises StateChanged, which rebuilds the
    /// card. Mid-drag that destroys the control the user is dragging — the gesture dies on its first
    /// mouse-move and the edit looks like it did nothing. Ramp handles and scrub fields therefore commit
    /// through here while the mouse is down and call <see cref="MutateAndRefreshAnalysis"/> once on
    /// release, the same live-scrub split the slider rows use.
    /// </summary>
    private void MutateAndRefreshAnalysisLive(Guid terrainId, Guid analysisId, Action<AnalysisDefinition> mutator)
    {
        MutateAnalysis(
            terrainId,
            analysisId,
            mutator,
            scheduleRebuild: false,
            deferDocumentSave: true,
            suppressImmediateUiRefresh: true);
        RefreshTerrainPreview(terrainId);
    }

    private void RefreshTerrainPreview(Guid terrainId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
            _controller.RefreshTerrainDisplay(doc, terrainId);
    }

    /// <summary>Sets the slider and its readout without re-entering the ValueChanged handler.</summary>
    private void SetPreviewLineWeightControls(double weight)
    {
        double clamped = Math.Clamp(weight > 0.0 ? weight : 1.0, 0.3, 4.0);
        _previewLineWeightSlider.Value = (int)Math.Round(clamped * 10.0);
        _previewLineWeightValue.Text = clamped.ToString("0.0", CultureInfo.CurrentCulture) + "x";
    }

    private static TerrainAnalysisSummary? GetAnalysisSummary(TerrainDefinition terrain, Guid analysisId)
    {
        return terrain.LastAnalysisResults.FirstOrDefault(item => item.AnalysisId == analysisId);
    }

    private static string GetAnalysisTypeLabel(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.TypeLabel ?? "Analysis";

    private static string GetTerrainObjectTypeLabel(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.DisplayName ?? "Objects";

    private static string GetTerrainObjectKind(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.Kind ?? string.Empty;

    private static Color TerrainObjectTypeColor(string kind)
    {
        int argb = ObjectTypeRegistry.ForKind(kind)?.AccentArgb ?? unchecked((int)0xFF787878);
        return Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
    }

    private static string GetTerrainObjectIconLabel(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.IconLabel ?? "O";

    private static string? GetTerrainObjectIconName(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.IconName;

    private static string GetTerrainObjectSubtitle(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.Subtitle ?? "Terrain objects";

    private static string GetTerrainObjectCollapsedSummary(TerrainObjectDefinition definition)
    {
        var parts = new List<string>
        {
            $"{CountReferences(definition.Sources)} refs",
            GetTerrainObjectTypeLabel(definition)
        };

        if (definition.RandomRotationMinDegrees > 1e-6 ||
            definition.RandomRotationMaxDegrees > definition.RandomRotationMinDegrees + 1e-6)
        {
            parts.Add($"Rot {definition.RandomRotationMinDegrees:G4}-{definition.RandomRotationMaxDegrees:G4}");
        }

        if (Math.Abs(definition.RandomScaleMin - 1.0) > 1e-6 ||
            Math.Abs(definition.RandomScaleMax - 1.0) > 1e-6)
        {
            parts.Add($"Scale {definition.RandomScaleMin:G4}-{definition.RandomScaleMax:G4}");
        }

        if (Math.Abs(definition.ZOffset) > 1e-6)
            parts.Add($"Z {definition.ZOffset:G4}");

        return string.Join(" | ", parts);
    }

    private static string GetAnalysisKind(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.Kind ?? string.Empty;


    private static Color AnalysisTypeColor(string kind)
    {
        int argb = AnalysisTypeRegistry.ForKind(kind)?.AccentArgb ?? unchecked((int)0xFF787878);
        return Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
    }

    private static string GetAnalysisIconLabel(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.IconLabel ?? "A";

    private static string? GetAnalysisIconName(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.IconName;

    private static string GetAnalysisSubtitle(AnalysisDefinition analysis, bool isActive)
    {
        var descriptor = AnalysisTypeRegistry.ForType(analysis.GetType());
        if (descriptor == null)
            return "Analysis";

        return isActive && descriptor.ActiveSubtitle != null ? descriptor.ActiveSubtitle : descriptor.Subtitle;
    }

    private static string GetAnalysisCollapsedSummary(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, analysis.Id);
        return analysis switch
        {
            EarthworkAnalysisDefinition earthwork => summary != null
                ? $"Net {FormatVolume(summary.NetVolume)} | {(summary.EarthworkIsEstimated ? "Estimated" : "Exact")}"
                : $"{CountReferences(earthwork.Reference)} refs | {CountReferences(earthwork.Boundary)} bounds",
            SlopeAnalysisDefinition slope => $"{FormatSlopeValue(slope.RangeLow, slope.Unit)} to {(slope.RangeHigh > slope.RangeLow ? FormatSlopeValue(slope.RangeHigh, slope.Unit) : "Auto")}",
            ElevationAnalysisDefinition elevation => $"{elevation.RangeLow:G4} to {(elevation.RangeHigh > elevation.RangeLow ? elevation.RangeHigh.ToString("G4") : "Auto")}",
            CutFillAnalysisDefinition cutFill => summary != null
                ? $"{summary.CutVolume:F2} / {summary.FillVolume:F2} / {summary.NetVolume:F2}"
                : $"{CountReferences(cutFill.Reference)} refs | {CountReferences(cutFill.Boundary)} bounds",
            WaterflowAnalysisDefinition waterflow => summary != null
                ? $"{summary.GeneratedOutputCount} paths | {summary.WaterflowBoundaryCount} boundary"
                : $"{CountReferences(waterflow.Sources)} refs | downhill paths",
            _ => string.Empty
        };
    }

    private void DuplicateAnalysis(Guid terrainId, Guid analysisId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
        {
            _controller.DuplicateAnalysis(doc, terrainId, analysisId);
            RefreshUi();
        }
    }

    private Control CreateSlopeUnitEditor(Guid terrainId, SlopeAnalysisDefinition slope)
    {
        var options = new[]
        {
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Percent), "Percent"),
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Promille), "Promille"),
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Ratio), "Ratio"),
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Degrees), "Degrees")
        };

        return CreateDropDownEditor(
            "Units",
            options,
            GetSlopeUnitKey(slope.Unit),
            value =>
            {
                var nextUnit = ParseSlopeUnit(value);
                if (nextUnit == slope.Unit)
                    return;

                double rangeLow = ConvertSlopeValue(slope.RangeLow, slope.Unit, nextUnit);
                double rangeHigh = ConvertSlopeValue(slope.RangeHigh, slope.Unit, nextUnit);
                double interval = ConvertSlopeValue(slope.ColorInterval, slope.Unit, nextUnit);
                MutateAndRefreshAnalysis(terrainId, slope.Id, item =>
                {
                    if (item is not SlopeAnalysisDefinition target)
                        return;

                    target.Unit = nextUnit;
                    target.RangeLow = rangeLow;
                    target.RangeHigh = rangeHigh;
                    target.ColorInterval = interval;
                });
            },
            "Show slope values as percent, promille, rise/run ratio, or degrees.");
    }

    private static string GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit unit) => AnalysisFormatting.GetSlopeUnitKey(unit);

    private static SlopeAnalyzer.SlopeUnit ParseSlopeUnit(string key) => AnalysisFormatting.ParseSlopeUnit(key);

    private static string GetSlopeUnitSuffixLabel(SlopeAnalyzer.SlopeUnit unit) => AnalysisFormatting.GetSlopeUnitSuffixLabel(unit);

    private static string FormatSlopeSummaryValue(double percentValue, SlopeAnalyzer.SlopeUnit unit) =>
        AnalysisFormatting.FormatSlopeSummaryValue(percentValue, unit);

    private static string FormatSlopeValue(double value, SlopeAnalyzer.SlopeUnit unit) =>
        AnalysisFormatting.FormatSlopeValue(value, unit);

    private static string FormatAnalysisValue(double value, string? format)
    {
        string effectiveFormat = string.IsNullOrWhiteSpace(format) ? "G4" : format;
        try
        {
            return value.ToString(effectiveFormat, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            return value.ToString("G4", CultureInfo.CurrentCulture);
        }
    }

    private static double ConvertSlopeValue(double value, SlopeAnalyzer.SlopeUnit fromUnit, SlopeAnalyzer.SlopeUnit toUnit) =>
        AnalysisFormatting.ConvertSlopeValue(value, fromUnit, toUnit);

    private static int CountReferences(SourceReferenceSet sourceSet)
    {
        return sourceSet.ObjectIds.Count + sourceSet.LayerPaths.Count;
    }

    // ── Drag-and-drop: analysis cards ────────────────────────────────────

    private void WireAnalysisCardDragDrop(Control box, Guid terrainId, Guid analysisId)
    {
        box.AllowDrop = true;
        var cardHighlight = Color.FromArgb(100, 120, 200, 255);

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverAnalysisId.HasValue && _analysisStripMap.TryGetValue(_dragOverAnalysisId.Value, out var prevStrip))
                if (_analysisStripColors.TryGetValue(_dragOverAnalysisId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverAnalysisId = analysisId;
            if (_analysisStripMap.TryGetValue(analysisId, out var strip))
                strip.BackgroundColor = cardHighlight;
            ClearAllSepHighlights(_analysisSepMap);
            if (_analysisSepMap.TryGetValue(analysisId, out var sep))
                sep.BackgroundColor = UiTheme.SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverAnalysisId != analysisId)
                return;

            if (_analysisStripMap.TryGetValue(analysisId, out var strip))
                if (_analysisStripColors.TryGetValue(analysisId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverAnalysisId = null;
            ClearAllSepHighlights(_analysisSepMap);
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            var idStr = e.Data.GetString("analysis-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            if (_analysisStripMap.TryGetValue(analysisId, out var strip))
                if (_analysisStripColors.TryGetValue(analysisId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverAnalysisId = null;
            ClearAllSepHighlights(_analysisSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveAnalysisToDisplaySeparator(t, sourceId, analysisId), scheduleRebuild: false);
            RefreshTerrainPreview(terrainId);
        };
    }

    private void WireAnalysisSepDragDrop(Panel outerSep, Panel innerSep, Guid terrainId, Guid insertBeforeId)
    {
        outerSep.AllowDrop = true;

        outerSep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverAnalysisId.HasValue && _analysisStripMap.TryGetValue(_dragOverAnalysisId.Value, out var prevStrip))
                if (_analysisStripColors.TryGetValue(_dragOverAnalysisId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverAnalysisId = null;
            ClearAllSepHighlights(_analysisSepMap);
            innerSep.BackgroundColor = UiTheme.SepHighlight;
        };

        outerSep.DragLeave += (_, _) => innerSep.BackgroundColor = Colors.Transparent;

        outerSep.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            var idStr = e.Data.GetString("analysis-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            innerSep.BackgroundColor = Colors.Transparent;
            ClearAllSepHighlights(_analysisSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveAnalysisToDisplaySeparator(t, sourceId, insertBeforeId), scheduleRebuild: false);
            RefreshTerrainPreview(terrainId);
        };
    }

    private void WireAnnotationCardDragDrop(Control box, Guid terrainId, Guid analysisId)
    {
        box.AllowDrop = true;
        var cardHighlight = Color.FromArgb(100, 120, 200, 255);

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverAnnotationId.HasValue && _annotationStripMap.TryGetValue(_dragOverAnnotationId.Value, out var prevStrip))
                if (_annotationStripColors.TryGetValue(_dragOverAnnotationId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverAnnotationId = analysisId;
            if (_annotationStripMap.TryGetValue(analysisId, out var strip))
                strip.BackgroundColor = cardHighlight;
            ClearAllSepHighlights(_annotationSepMap);
            if (_annotationSepMap.TryGetValue(analysisId, out var sep))
                sep.BackgroundColor = UiTheme.SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverAnnotationId != analysisId)
                return;

            if (_annotationStripMap.TryGetValue(analysisId, out var strip))
                if (_annotationStripColors.TryGetValue(analysisId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverAnnotationId = null;
            ClearAllSepHighlights(_annotationSepMap);
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            var idStr = e.Data.GetString("analysis-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            if (_annotationStripMap.TryGetValue(analysisId, out var strip))
                if (_annotationStripColors.TryGetValue(analysisId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverAnnotationId = null;
            ClearAllSepHighlights(_annotationSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveAnalysisToDisplaySeparator(t, sourceId, analysisId), scheduleRebuild: false);
            RefreshTerrainPreview(terrainId);
        };
    }

    private void WireAnnotationSepDragDrop(Panel outerSep, Panel innerSep, Guid terrainId, Guid insertBeforeId)
    {
        outerSep.AllowDrop = true;

        outerSep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverAnnotationId.HasValue && _annotationStripMap.TryGetValue(_dragOverAnnotationId.Value, out var prevStrip))
                if (_annotationStripColors.TryGetValue(_dragOverAnnotationId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverAnnotationId = null;
            ClearAllSepHighlights(_annotationSepMap);
            innerSep.BackgroundColor = UiTheme.SepHighlight;
        };

        outerSep.DragLeave += (_, _) => innerSep.BackgroundColor = Colors.Transparent;

        outerSep.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            var idStr = e.Data.GetString("analysis-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            innerSep.BackgroundColor = Colors.Transparent;
            ClearAllSepHighlights(_annotationSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveAnalysisToDisplaySeparator(t, sourceId, insertBeforeId), scheduleRebuild: false);
            RefreshTerrainPreview(terrainId);
        };
    }

    private static void MoveAnalysisToDisplaySeparator(TerrainDefinition terrain, Guid sourceId, Guid insertBeforeId)
    {
        if (sourceId == insertBeforeId)
            return;

        int fromIdx = terrain.Analyses.FindIndex(analysis => analysis.Id == sourceId);
        if (fromIdx < 0)
            return;

        var item = terrain.Analyses[fromIdx];
        terrain.Analyses.RemoveAt(fromIdx);

        if (insertBeforeId == Guid.Empty)
        {
            terrain.Analyses.Add(item);
            return;
        }

        int insertIdx = terrain.Analyses.FindIndex(analysis => analysis.Id == insertBeforeId);
        terrain.Analyses.Insert(insertIdx >= 0 ? insertIdx : terrain.Analyses.Count, item);
    }

    // ── Drag-and-drop: modifier cards ────────────────────────────────────

    private void WireModifierCardDragDrop(Control box, Guid terrainId, Guid modifierId)
    {
        box.AllowDrop = true;
        var cardHighlight = Color.FromArgb(100, 120, 200, 255);

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverModifierId.HasValue && _modifierStripMap.TryGetValue(_dragOverModifierId.Value, out var prevStrip))
                if (_modifierStripColors.TryGetValue(_dragOverModifierId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverModifierId = modifierId;
            if (_modifierStripMap.TryGetValue(modifierId, out var strip))
                strip.BackgroundColor = cardHighlight;
            ClearAllSepHighlights(_modifierSepMap);
            if (_modifierSepMap.TryGetValue(modifierId, out var sep))
                sep.BackgroundColor = UiTheme.SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverModifierId == modifierId)
            {
                if (_modifierStripMap.TryGetValue(modifierId, out var strip))
                    if (_modifierStripColors.TryGetValue(modifierId, out var origColor))
                        strip.BackgroundColor = origColor;
                _dragOverModifierId = null;
                ClearAllSepHighlights(_modifierSepMap);
            }
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            var idStr = e.Data.GetString("modifier-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            if (_modifierStripMap.TryGetValue(modifierId, out var strip))
                if (_modifierStripColors.TryGetValue(modifierId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverModifierId = null;
            ClearAllSepHighlights(_modifierSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveModifierToDisplaySeparator(t, sourceId, modifierId));
        };
    }

    private void WireModifierSepDragDrop(Panel outerSep, Panel innerSep, Guid terrainId, Guid insertBeforeId)
    {
        outerSep.AllowDrop = true;

        outerSep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverModifierId.HasValue && _modifierStripMap.TryGetValue(_dragOverModifierId.Value, out var prevStrip))
                if (_modifierStripColors.TryGetValue(_dragOverModifierId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverModifierId = null;
            ClearAllSepHighlights(_modifierSepMap);
            innerSep.BackgroundColor = UiTheme.SepHighlight;
        };

        outerSep.DragLeave += (_, _) => innerSep.BackgroundColor = Colors.Transparent;

        outerSep.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            var idStr = e.Data.GetString("modifier-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            innerSep.BackgroundColor = Colors.Transparent;
            ClearAllSepHighlights(_modifierSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveModifierToDisplaySeparator(t, sourceId, insertBeforeId));
        };
    }

    private static void MoveModifierToDisplaySeparator(TerrainDefinition terrain, Guid sourceId, Guid insertAboveId)
    {
        if (sourceId == insertAboveId)
            return;

        int fromIdx = terrain.Modifiers.FindIndex(modifier => modifier.Id == sourceId);
        if (fromIdx < 0)
            return;

        var item = terrain.Modifiers[fromIdx];
        terrain.Modifiers.RemoveAt(fromIdx);

        if (insertAboveId == Guid.Empty)
        {
            // Tail separator = visual bottom. Display order is reversed, so model index 0 is the bottom card.
            terrain.Modifiers.Insert(0, item);
            return;
        }

        int anchorIdx = terrain.Modifiers.FindIndex(modifier => modifier.Id == insertAboveId);
        int insertIdx = anchorIdx >= 0
            ? Math.Min(anchorIdx + 1, terrain.Modifiers.Count)
            : terrain.Modifiers.Count;
        terrain.Modifiers.Insert(insertIdx, item);
    }

    private static void ClearAllSepHighlights(Dictionary<Guid, Panel> sepMap)
    {
        foreach (var sep in sepMap.Values)
            sep.BackgroundColor = Colors.Transparent;
    }

    private static Color ModifierTypeColor(string kind) => kind switch
    {
        "triangulate"    => Color.FromArgb(25, 118, 210),
        "add-geometry"   => Color.FromArgb(2, 136, 209),
        "remesh"         => Color.FromArgb(56, 142, 60),
        "smooth"         => Color.FromArgb(123, 31, 162),
        "project-to"     => Color.FromArgb(0, 121, 140),
        "retaining-wall" => Color.FromArgb(230, 74, 25),
        "simplify"       => Color.FromArgb(0, 137, 123),
        "grade-pad"      => Color.FromArgb(245, 124, 0),
        "grade-path"     => Color.FromArgb(93, 64, 55),
        "in-situ-stair"  => Color.FromArgb(0, 121, 107),
        _                => Color.FromArgb(120, 120, 120)
    };

    private static string GetModifierKind(ModifierDefinition modifier) =>
        TerrainTypeRegistry.ForModifierType(modifier.GetType())?.Kind ?? string.Empty;

    private static string GetModifierTypeLabel(ModifierDefinition modifier) =>
        TerrainTypeRegistry.ForModifierType(modifier.GetType())?.DisplayName ?? "Modifier";

    private static string GetModifierIconName(string kind) =>
        TerrainTypeRegistry.ForModifierKind(kind)?.IconName ?? "ModTriangulate";

    private static string GetModifierSubtitle(ModifierDefinition modifier, bool isPinnedBaseTriangulate)
    {
        if (isPinnedBaseTriangulate && modifier is TriangulateModifierDefinition)
            return "Base geometry";

        return TerrainTypeRegistry.ForModifierType(modifier.GetType())?.Subtitle ?? "Modifier";
    }

    private static string GetCollapsedSummary(ModifierDefinition modifier)
    {
        switch (modifier)
        {
            case TriangulateModifierDefinition t:
                int dem = t.DemSurface.ObjectIds.Count + t.DemSurface.LayerPaths.Count;
                int pts = t.Points.ObjectIds.Count + t.Points.LayerPaths.Count;
                int bkl = t.Breaklines.ObjectIds.Count + t.Breaklines.LayerPaths.Count;
                int ctr = t.Contours.ObjectIds.Count + t.Contours.LayerPaths.Count;
                return $"{dem} DEM | {pts} pts | {bkl} breaks | {ctr} contours";
            case AddGeometryModifierDefinition a:
                int addPts = a.Points.ObjectIds.Count + a.Points.LayerPaths.Count;
                int addBkl = a.Breaklines.ObjectIds.Count + a.Breaklines.LayerPaths.Count;
                int addCtr = a.Contours.ObjectIds.Count + a.Contours.LayerPaths.Count;
                return $"{addPts} pts | {addBkl} breaks | {addCtr} contours";
            case RemeshModifierDefinition r:
                var parts = new System.Collections.Generic.List<string>();
                parts.Add(r.EdgeLength > 0 ? $"Edge: {r.EdgeLength:G4}" : "Edge: auto");
                if (r.CreaseAngle > 0) parts.Add($"Crease: {r.CreaseAngle:G4} deg");
                return string.Join(" | ", parts);
            case SimplifyModifierDefinition simplify:
                return simplify.Mode switch
                {
                    SimplifyModifierDefinition.TargetVertexCountMode => $"At most {simplify.TargetVertexCount:N0} vertices",
                    SimplifyModifierDefinition.RetainPercentageMode => $"Retain {simplify.RetainPercentage:G4}%",
                    _ => $"Max dz: {simplify.MaximumDeviation:G4}"
                };
            case SmoothModifierDefinition s:
                return $"{s.Iterations} iter | Str {s.Strength:G3}";
            case ProjectToModifierDefinition projectTo:
                string target = projectTo.TargetMesh.HasReferences
                    ? "mesh"
                    : projectTo.TargetTerrainId.HasValue ? "terrain" : "no target";
                int projectBoundaries = projectTo.Boundaries.ObjectIds.Count + projectTo.Boundaries.LayerPaths.Count;
                return $"{target} | {projectBoundaries} boundaries | Str {projectTo.Strength:G3}";
            case GradePadModifierDefinition p:
                int bounds = p.Boundaries.ObjectIds.Count + p.Boundaries.LayerPaths.Count;
                return $"{bounds} boundaries | Daylight {p.SlopeAngle:G4} deg";
            case GradePathModifierDefinition path:
                int paths = path.Paths.ObjectIds.Count + path.Paths.LayerPaths.Count;
                if (!path.UseVariableWidth)
                    return $"{paths} paths | W={path.Width:G4}";
                int edges = path.WidthEdges.ObjectIds.Count + path.WidthEdges.LayerPaths.Count;
                return $"{paths} paths | variable, {edges} width edges | W={path.Width:G4} fallback";
            case GradeLineModifierDefinition line:
                int lineCount = line.Lines.ObjectIds.Count + line.Lines.LayerPaths.Count;
                return line.UseAsymmetricSides
                    ? $"{lineCount} lines | asymmetric sides"
                    : $"{lineCount} lines | Fill {line.SlopeAngle:G4} deg";
            case RetainingWallModifierDefinition w:
                int curves = w.WallCurves.ObjectIds.Count + w.WallCurves.LayerPaths.Count;
                return w.GradesTerrain
                    ? $"{curves} curves | grading {(w.UseAsymmetricSides ? "asymmetric" : $"{w.SlopeAngle:G4} deg")}"
                    : $"{curves} curves";
            case InSituStairModifierDefinition stair:
                int refs = stair.ReferenceSurface.ObjectIds.Count + stair.ReferenceSurface.LayerPaths.Count;
                return !string.IsNullOrWhiteSpace(stair.ComputedTreadDepthSummary)
                    ? $"{stair.ComputedSurfaceCount ?? refs} surf | Tread {stair.ComputedTreadDepthSummary}"
                    : $"{refs} refs | Riser {stair.RiserHeight:G4}";
            default:
                return string.Empty;
        }
    }

    private static Drawable CreateDragHandle()
    {
        var handle = new Drawable { Width = 16, Height = 22, Cursor = Cursors.Move };
        handle.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var ctl = SystemColors.ControlText;
            var dot = new Color(ctl.R, ctl.G, ctl.B, 0.6f);
            float[] xs = { 5f, 10f };
            float[] ys = { 6f, 11f, 16f };
            foreach (var x in xs)
                foreach (var y in ys)
                    g.FillEllipse(dot, x - 1.5f, y - 1.5f, 3f, 3f);
        };
        return handle;
    }

    private void ShowTerrainPickerMenu(Button anchor)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        var terrains = _controller.GetTerrains(doc).ToList();
        bool dataUnreadable = _controller.IsTerrainDataUnreadable(doc);
        if (terrains.Count == 0 && !dataUnreadable)
            return;

        var menu = new ContextMenu();
        foreach (var terrain in terrains)
        {
            var item = new ButtonMenuItem { Text = terrain.Name };
            var capturedId = terrain.TerrainId;
            item.Click += (_, _) =>
            {
                _controller.SetSelectedTerrain(doc, capturedId);
                RefreshUi();
            };
            menu.Items.Add(item);
        }
        if (doc != null && _controller.GetSelectedTerrain(doc) != null)
        {
            menu.Items.AddSeparator();
            var bakeItem = new ButtonMenuItem { Text = "Bake to document" };
            bakeItem.Click += OnBakeTerrain;
            menu.Items.Add(bakeItem);
            var detachItem = new ButtonMenuItem { Text = "Detach from sources" };
            detachItem.Click += OnConvertTerrain;
            menu.Items.Add(detachItem);
        }
        if (dataUnreadable)
        {
            RhinoDoc capturedDoc = doc!;
            menu.Items.AddSeparator();
            var resetItem = new ButtonMenuItem { Text = "Reset terrain data (unreadable)" };
            resetItem.Click += (_, _) =>
            {
                _controller.ResetTerrainDataAfterFailedLoad(capturedDoc);
                RefreshUi();
            };
            menu.Items.Add(resetItem);
        }
        menu.Show(anchor);
    }

    private void ShowSingleLayerPickerPopover(Button anchor, Action<string?> onPick)
    {
        ShowLayerPickerPopover(
            anchor,
            Array.Empty<string>(),
            path => onPick(path),
            onRemoveLayer: null,
            onClear: () => onPick(null),
            preferredSize: new Size(320, 360),
            clearToolTip: "Clear the explicit layer assignment.");
    }

    private Button MakeLayerPickerButton(Action<string?> onPick, string toolTip = "Browse layers")
    {
        Button? btn = null;
        btn = MakeInlineButton("Pick", (_, _) => ShowSingleLayerPickerPopover(btn!, onPick), toolTip);
        btn.ToolTip = toolTip;
        return btn;
    }

    private static int GetOpacityPercent(int argb)
    {
        int alpha = System.Drawing.Color.FromArgb(argb).A;
        return (int)Math.Round((alpha / 255.0) * 100.0);
    }

    private static int WithOpacityPercent(int argb, int opacityPercent)
    {
        var color = System.Drawing.Color.FromArgb(argb);
        int alpha = (int)Math.Round(Math.Clamp(opacityPercent, 0, 100) / 100.0 * 255.0);
        return System.Drawing.Color.FromArgb(alpha, color.R, color.G, color.B).ToArgb();
    }

    private static int ToArgb(Color color)
    {
        int alpha = (int)Math.Round(Math.Clamp(color.A, 0f, 1f) * 255.0);
        int red = (int)Math.Round(Math.Clamp(color.R, 0f, 1f) * 255.0);
        int green = (int)Math.Round(Math.Clamp(color.G, 0f, 1f) * 255.0);
        int blue = (int)Math.Round(Math.Clamp(color.B, 0f, 1f) * 255.0);
        return System.Drawing.Color.FromArgb(alpha, red, green, blue).ToArgb();
    }

    private static Color ResolveOptionalColorSwatch(int? colorArgb, int? fallbackColorArgb)
    {
        int argb = colorArgb ?? fallbackColorArgb ?? unchecked((int)0xFF808080);
        return ToEtoColor(System.Drawing.Color.FromArgb(argb));
    }

    private static string DescribeSolidColor(int argb)
    {
        var color = System.Drawing.Color.FromArgb(argb);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static int? ResolveLayerColorArgb(string? layerPath) => AnalysisFormatting.ResolveLayerColorArgb(layerPath);

    private static string DescribeTerrainColor(int argb)
    {
        var color = System.Drawing.Color.FromArgb(argb);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2} · {GetOpacityPercent(argb)}%";
    }

    private static Color ToEtoColor(System.Drawing.Color c) => Color.FromArgb(c.R, c.G, c.B, c.A);
}
