using System.Globalization;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;

namespace MoleHill.Rhino.UI;

[System.Runtime.InteropServices.Guid("E8A65B83-74A6-4325-B66D-D7005A4A8257")]
public sealed class MoleHillPanel : Panel
{
    private static readonly (string Label, string Kind)[] ModifierKinds =
    {
        ("Add Geometry", "add-geometry"),
        ("Remesh", "remesh"),
        ("Smooth", "smooth"),
        ("Retaining Wall", "retaining-wall"),
        ("Grade Pad", "grade-pad"),
        ("Grade Path", "grade-path"),
        ("In-Situ Stair", "in-situ-stair")
    };

    private static readonly (string Label, string Kind)[] AnalysisKinds =
    {
        ("Earthworks", "earthwork"),
        ("Slope", "slope"),
        ("Elevation", "elevation"),
        ("Cut / Fill", "cut-fill"),
    };

    private static readonly (string Label, string Kind)[] AnnotationKinds =
    {
        ("Contours", "contour"),
        ("Curve Elevation Labels", "curve-elevation-label"),
        ("Curve Slope Labels", "curve-slope-label"),
        ("Projected Elevation Labels", "projected-elevation-label"),
        ("Point Slope Labels", "point-slope-label"),
        ("Terrain Section", "terrain-section"),
        ("Cross-Sections at Stations", "cross-section-station"),
        ("Section Along Curve", "longitudinal-section"),
    };

    private readonly TerrainController _controller = TerrainController.Instance;
    private readonly TextBox _terrainName = new();
    private readonly CheckBox _liveUpdate = new() { Text = "Live" };
    private readonly TextArea _statusTextArea = new() { ReadOnly = true, Wrap = true, Height = 180 };
    private readonly Label _terrainLayerLabel    = new() { VerticalAlignment = VerticalAlignment.Center, Wrap = WrapMode.Word };
    private readonly Label _auxLayerLabel        = new() { VerticalAlignment = VerticalAlignment.Center, Wrap = WrapMode.Word };
    private readonly Label _annotationLayerLabel = new() { VerticalAlignment = VerticalAlignment.Center, Wrap = WrapMode.Word };
    private readonly Panel _terrainColorSwatch = new() { Width = 18, Height = 18 };
    private readonly Label _terrainColorLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Label _statusHintLabel   = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericStepper _toleranceStepper = new();
    private readonly NumericStepper _terrainOpacityStepper = new();
    private readonly Slider _terrainOpacitySlider = new() { MinValue = 0, MaxValue = 100, Width = 120 };
    private readonly CheckBox _showWiresCheck = new() { Text = "Show Wires" };
    private readonly CheckBox _showSlowBuildWarningCheck = new() { Text = "Warn Before Slow Builds" };
    private readonly CheckBox _replacePreviousBakesCheck = new() { Text = "Replace Previously Baked" };
    private readonly Button _untrackSelectedBakesButton = new() { Text = "Untrack Selected", Height = HeaderActionHeight };
    private readonly Button _untrackAllBakesButton = new() { Text = "Untrack All", Height = HeaderActionHeight };
    private bool _settingsExpanded = true;
    private bool _statusExpanded = false;
    private bool _isUpdatingOpacityControls;
    private Panel? _settingsContent;
    private Panel? _statusContent;
    private Button? _settingsChevron;
    private Button? _statusChevron;
    private readonly Button _visibilityButton = new() { Width = 42 };
    private readonly Button _lockButton = new() { Width = 42 };
    private Button _dupButton = new();
    private Button _deleteButton = new();
    private Button _rebuildButton = new();
    private Button _resetBuildButton = new();
    private Button _bakeButton = new();
    private readonly DropDown _terrainPickerDropDown = new();
    private bool _isUpdatingTerrainPicker;
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
    private readonly StackLayout _markerStack = new()
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
    private const int PropertyLabelWidth = 84;
    private const int NumericLabelWidth = 96;
    private const int HeaderActionHeight = 22;
    private const int StackedSourceEditorWidth = 390;
    private const int StackedFormRowWidth = 350;
    private const int WrappedModifierHeaderWidth = 390;
    private const int CompactCardHeaderWidth = 360;
    private const int LayerPickerMinHeight = 160;
    private const int LayerPickerMargin = 6;
    private const int LayerPickerRowHeight = 28;
    private readonly EventHandler _stateChangedHandler;
    private bool _isRefreshing;
    private bool _isPanelLoaded;
    private bool _isStateChangedSubscribed;
    private int _responsiveLayoutKey = -1;
    private int _deferredControllerRefreshDepth;
    private bool _hasDeferredControllerRefresh;
    private int _selectedTabIndex;
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

    private sealed class SharedCardShellOptions
    {
        public required Control Handle { get; init; }
        public required Control CollapseControl { get; init; }
        public required Control IconPlate { get; init; }
        public required Control EnabledControl { get; init; }
        public required Control TitleBlock { get; init; }
        public required Action<bool> ToggleCollapsed { get; init; }
        public required bool Collapsed { get; init; }
        public Color CardBackground { get; init; } = UiTheme.CardBackground;
        public Color HeaderBackground { get; init; } = UiTheme.HeaderBackground;
        public IReadOnlyList<Control> StatusControls { get; init; } = Array.Empty<Control>();
        public IReadOnlyList<Control> ActionControls { get; init; } = Array.Empty<Control>();
        public Control? Body { get; init; }
    }

    public MoleHillPanel()
    {
        _stateChangedHandler = HandleControllerStateChanged;

        ApplyHelp(_terrainName, "Terrain name. Commits when you press Enter or leave the field.");
        StyleTextBox(_terrainName);
        BindCommittedText(
            _terrainName,
            () =>
            {
                var doc = RhinoDoc.ActiveDoc;
                return doc == null ? string.Empty : _controller.GetSelectedTerrain(doc)?.Name ?? string.Empty;
            },
            text => MutateSelectedTerrain(terrain => terrain.Name = text, scheduleRebuild: false));

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

        var toleranceTimer = new UITimer { Interval = 0.25 };
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
            if (_isRefreshing)
                return;

            int opacityPercent = (int)Math.Round(_terrainOpacityStepper.Value);
            SetTerrainOpacityControls(opacityPercent);
            ApplyTerrainOpacity(opacityPercent);
        };
        _terrainOpacitySlider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing || _isUpdatingOpacityControls)
                return;

            int opacityPercent = _terrainOpacitySlider.Value;
            SetTerrainOpacityControls(opacityPercent);
            ApplyTerrainOpacity(opacityPercent);
        };
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
        ApplyHelp(_lockButton, "Lock or unlock all generated terrain outputs.");

        SubscribeControllerStateChanged();
        SizeChanged += HandlePanelSizeChanged;
        LoadComplete += OnPanelLoadComplete;
        UnLoad += OnPanelUnLoad;
        RhinoApp.AppSettingsChanged += OnAppSettingsChanged;

        _responsiveLayoutKey = GetResponsiveLayoutKey();
        Content = BuildContent();
        RefreshUi();
    }

    private Control BuildContent()
    {
        bool stackFormRows = UseStackedFormRows();

        var newButton = MakeToolbarButton("New", OnNewTerrain, "Create a new terrain", width: 46);
        _dupButton = MakeToolbarButton("Copy", OnDuplicateTerrain, "Duplicate selected terrain", width: 50);
        _deleteButton = MakeToolbarButton("Del", OnDeleteTerrain, "Delete selected terrain", width: 38);
        _rebuildButton = MakeToolbarButton("Rebuild", OnRebuildTerrain, "Force rebuild terrain now", width: 62);
        _resetBuildButton = MakeToolbarButton("Reset Build", OnResetTerrainBuild, "Cancel the current worker, clear queued rebuilds, and drop cached preview state.", width: 82);
        _bakeButton = MakeToolbarButton("Bake", OnBakeTerrain, "Bake the terrain to document objects", width: 46);
        _visibilityButton.ToolTip = "Toggle terrain visibility";
        _lockButton.ToolTip = "Lock terrain to prevent accidental edits";
        _visibilityButton.Width = 58;
        _lockButton.Width = 58;
        _visibilityButton.Height = 26;
        _lockButton.Height = 26;
        _liveUpdate.Height = 26;

        _terrainPickerDropDown.ToolTip = "Select the active terrain";
        _terrainPickerDropDown.Height = 26;
        _isUpdatingTerrainPicker = false;
        _terrainPickerDropDown.SelectedIndexChanged -= OnTerrainPickerDropDownChanged;
        _terrainPickerDropDown.SelectedIndexChanged += OnTerrainPickerDropDownChanged;

        // ── Toolbar (two rows) ────────────────────────────────────────
        // Row 1: Terrain label | name | New / Copy / Del | picker dropdown
        var identityRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "Terrain", TextColor = UiTheme.MutedText, VerticalAlignment = VerticalAlignment.Center },
                new StackLayoutItem(_terrainName, expand: true),
                CreateToolbarGroup(newButton, _dupButton, _deleteButton),
                new StackLayoutItem(_terrainPickerDropDown, HorizontalAlignment.Right)
            }
        };
        var identityGroup = new Panel
        {
            BackgroundColor = UiTheme.ToolbarBackground,
            Padding = new Padding(8, 6, 8, 6),
            Content = identityRow
        };

        // Row 2: Rebuild / Reset / Live | spacer | Bake | Shown / Locked
        var actionsRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                CreateToolbarGroup(_rebuildButton, _resetBuildButton, _liveUpdate),
                new StackLayoutItem(new Panel(), expand: true),
                _bakeButton,
                CreateToolbarGroup(_visibilityButton, _lockButton)
            }
        };

        var toolbar = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Padding(8, 8, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(identityGroup, HorizontalAlignment.Stretch),
                new StackLayoutItem(actionsRow, HorizontalAlignment.Stretch)
            }
        };

        // ── Settings card (collapsible, expanded by default) ─────────
        _settingsChevron = MakeMiniButton("▼", (_, _) =>
        {
            _settingsExpanded = !_settingsExpanded;
            _settingsChevron!.Text = _settingsExpanded ? "▼" : "▶";
            _settingsContent!.Visible = _settingsExpanded;
        }, "Collapse terrain settings", width: 24);

        var settingsHeader = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Padding(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            BackgroundColor = UiTheme.HeaderBackground,
            Items =
            {
                _settingsChevron,
                new Label { Text = "Terrain Settings", Font = new Font(SystemFont.Bold), VerticalAlignment = VerticalAlignment.Center },
                new StackLayoutItem(new Label
                {
                    Text = "Document defaults, layers, and terrain display",
                    TextColor = UiTheme.MutedText,
                    VerticalAlignment = VerticalAlignment.Center
                }, expand: true)
            }
        };

        var terrainLayerUseCurrentButton = MakeCompactButton("Use Current", OnAssignTerrainLayer, "Assign the current Rhino layer.");
        var terrainLayerBrowseButton = MakeLayerPickerButton(path => MutateSelectedTerrain(t => t.TerrainLayerPath = path, scheduleRebuild: false), "Browse and pick the terrain layer");
        var terrainLayerDefaultButton = MakeCompactButton("Default", (_, _) =>
        {
            MutateSelectedTerrain(t => t.TerrainLayerPath = null, scheduleRebuild: false);
            RefreshUi();
        }, "Use the default MoleHill terrain layer.");
        var terrainLayerControls = CreateResponsivePrimaryActionRow(
            _terrainLayerLabel,
            4,
            terrainLayerUseCurrentButton,
            terrainLayerBrowseButton,
            terrainLayerDefaultButton);
        var bakeLayerStylesButton = MakeCompactButton("Bake Layers", (_, _) =>
                BakeLayerPaths(Array.Empty<string>()),
            "Create or refresh baked output layers for highlighted source layers.");
        var bakeLayerStylesControls = CreateResponsivePrimaryActionRow(
            new Label
            {
                Text = "Highlighted source layers",
                TextColor = UiTheme.MutedText,
                VerticalAlignment = VerticalAlignment.Center
            },
            4,
            bakeLayerStylesButton);
        var bakeLayerStylesRow = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Bake Layers", "Create or refresh baked output layers for highlighted source layers.", 0),
                    new StackLayoutItem(bakeLayerStylesControls, HorizontalAlignment.Stretch)
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(0, 1),
                Items =
                {
                    CreateHelpLabel("Bake Layers", "Create or refresh baked output layers for highlighted source layers.", PropertyLabelWidth),
                    new StackLayoutItem(bakeLayerStylesControls, expand: true)
                }
            };
        var terrainLayerRow = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Terrain Layer", "Output layer for the main terrain mesh.", 0),
                    new StackLayoutItem(terrainLayerControls, HorizontalAlignment.Stretch)
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(0, 1),
                Items =
                {
                    CreateHelpLabel("Terrain Layer", "Output layer for the main terrain mesh.", PropertyLabelWidth),
                    new StackLayoutItem(terrainLayerControls, expand: true)
                }
            };
        var auxLayerUseCurrentButton = MakeCompactButton("Use Current", OnAssignAuxLayer, "Assign the current Rhino layer for retaining walls and other auxiliary outputs.");
        var auxLayerBrowseButton = MakeLayerPickerButton(path => MutateSelectedTerrain(t => t.AuxiliaryLayerPath = path, scheduleRebuild: true), "Browse and pick the walls / auxiliary layer");
        var auxLayerClearButton = MakeCompactButton("Clear", (_, _) =>
        {
            MutateSelectedTerrain(t => t.AuxiliaryLayerPath = null, scheduleRebuild: true);
            RefreshUi();
        }, "Clear the walls / auxiliary layer assignment.");
        var auxLayerControls = CreateResponsivePrimaryActionRow(
            _auxLayerLabel,
            4,
            auxLayerUseCurrentButton,
            auxLayerBrowseButton,
            auxLayerClearButton);
        var auxLayerRow = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Walls / Aux", "Output layer for retaining walls, stair solids, and other auxiliary geometry.", 0),
                    new StackLayoutItem(auxLayerControls, HorizontalAlignment.Stretch)
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(0, 1),
                Items =
                {
                    CreateHelpLabel("Walls / Aux", "Output layer for retaining walls, stair solids, and other auxiliary geometry.", PropertyLabelWidth),
                    new StackLayoutItem(auxLayerControls, expand: true)
                }
            };
        var annotationLayerUseCurrentButton = MakeCompactButton("Use Current", OnAssignAnnotationLayer, "Assign the current Rhino layer for annotation outputs.");
        var annotationLayerBrowseButton = MakeLayerPickerButton(path => MutateSelectedTerrain(t => t.AnnotationLayerPath = path, scheduleRebuild: false), "Browse and pick the annotation layer");
        var annotationLayerClearButton = MakeCompactButton("Clear", (_, _) =>
        {
            MutateSelectedTerrain(t => t.AnnotationLayerPath = null, scheduleRebuild: false);
            RefreshUi();
        }, "Clear the annotation layer assignment.");
        var annotationLayerControls = CreateResponsivePrimaryActionRow(
            _annotationLayerLabel,
            4,
            annotationLayerUseCurrentButton,
            annotationLayerBrowseButton,
            annotationLayerClearButton);
        var annotationLayerRow = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Annotation", "Default output layer for contours, elevation labels, and slope labels.", 0),
                    new StackLayoutItem(annotationLayerControls, HorizontalAlignment.Stretch)
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(0, 1),
                Items =
                {
                    CreateHelpLabel("Annotation", "Default output layer for contours, elevation labels, and slope labels.", PropertyLabelWidth),
                    new StackLayoutItem(annotationLayerControls, expand: true)
                }
            };
        var toleranceRow = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Detail Size", "Smallest terrain detail to preserve automatically. Smaller values keep more detail; larger values simplify and merge nearby geometry more aggressively.", 0),
                    _toleranceStepper
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(0, 1),
                Items = { CreateHelpLabel("Detail Size", "Smallest terrain detail to preserve automatically. Smaller values keep more detail; larger values simplify and merge nearby geometry more aggressively.", PropertyLabelWidth), _toleranceStepper }
            };
        var opacityLabel = CreateHelpLabel("Opacity", "Terrain opacity used for preview and bake.", stackFormRows ? 0 : 52);
        var resetTerrainColorButton = MakeCompactButton("Reset", (_, _) => ResetTerrainColor(), "Restore the default terrain display color.");
        var terrainColorControls = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Items =
                        {
                            _terrainColorSwatch,
                            opacityLabel,
                            new StackLayoutItem(_terrainOpacitySlider, expand: true)
                        }
                    }, HorizontalAlignment.Stretch),
                    new StackLayoutItem(CreateResponsiveControlGroup(4, _terrainOpacityStepper, resetTerrainColorButton), HorizontalAlignment.Stretch)
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    _terrainColorSwatch,
                    opacityLabel,
                    new StackLayoutItem(_terrainOpacitySlider, expand: true),
                    _terrainOpacityStepper,
                    resetTerrainColorButton
                }
            };
        var terrainColorRow = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Terrain Color", "Base display color for the terrain preview and baked terrain. Click the swatch to change. Opacity affects this terrain mesh only.", 0),
                    new StackLayoutItem(terrainColorControls, HorizontalAlignment.Stretch)
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(0, 1),
                Items =
                {
                    CreateHelpLabel("Terrain Color", "Base display color for the terrain preview and baked terrain. Click the swatch to change. Opacity affects this terrain mesh only.", PropertyLabelWidth),
                    new StackLayoutItem(terrainColorControls, expand: true)
                }
            };
        var terrainDisplayRow = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    _showWiresCheck,
                    _showSlowBuildWarningCheck
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Padding(0, 1),
                Items =
                {
                    new Panel { Width = PropertyLabelWidth },
                    _showWiresCheck,
                    _showSlowBuildWarningCheck
                }
            };
        var bakeTrackingControls = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    _replacePreviousBakesCheck,
                    new StackLayoutItem(CreateResponsiveControlGroup(4, _untrackSelectedBakesButton, _untrackAllBakesButton), HorizontalAlignment.Stretch)
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    _replacePreviousBakesCheck,
                    _untrackSelectedBakesButton,
                    _untrackAllBakesButton
                }
            };
        var bakeTrackingRow = stackFormRows
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Bake Tracking", "Replace previous bake sets automatically, or untrack baked objects you want to keep.", 0),
                    new StackLayoutItem(bakeTrackingControls, expand: true)
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(0, 1),
                Items =
                {
                    CreateHelpLabel("Bake Tracking", "Replace previous bake sets automatically, or untrack baked objects you want to keep.", PropertyLabelWidth),
                    new StackLayoutItem(bakeTrackingControls, expand: true)
                }
            };
        var settingsInner = new StackLayout
        {
            Orientation = Orientation.Vertical, Spacing = 4, Padding = new Padding(10, 8, 10, 8),
            Items =
            {
                new StackLayoutItem(terrainLayerRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(bakeLayerStylesRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(auxLayerRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(annotationLayerRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(terrainColorRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(terrainDisplayRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(bakeTrackingRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(toleranceRow, HorizontalAlignment.Stretch)
            }
        };

        _settingsContent = new Panel { Content = settingsInner, Visible = _settingsExpanded, BackgroundColor = UiTheme.CardBackground };

        var settingsCard = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Padding = new Padding(8, 2),
            Items =
            {
                new StackLayoutItem(settingsHeader, HorizontalAlignment.Stretch),
                new StackLayoutItem(_settingsContent, HorizontalAlignment.Stretch)
            }
        };

        // ── Status card (collapsible, collapsed by default) ───────────
        _statusChevron = MakeMiniButton("▶", (_, _) =>
        {
            _statusExpanded = !_statusExpanded;
            _statusChevron!.Text = _statusExpanded ? "▼" : "▶";
            _statusContent!.Visible = _statusExpanded;
            _statusHintLabel.Visible = !_statusExpanded;
        }, "Show or hide build status", width: 24);

        var statusHeader = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Padding(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            BackgroundColor = UiTheme.HeaderBackground,
            Items =
            {
                _statusChevron,
                new Label { Text = "Status", Font = new Font(SystemFont.Bold), VerticalAlignment = VerticalAlignment.Center },
                new StackLayoutItem(_statusHintLabel, expand: true)
            }
        };

        StyleTextArea(_statusTextArea);
        var copyStatusButton = MakeMiniButton("Copy Log", (_, _) => CopyStatusLog(), "Copy the full build log to the clipboard.", width: 74);
        var copyCaseButton = MakeMiniButton("Copy Case", (_, _) => CopyCaseBundle(), "Export a repro case bundle and copy a runnable core xUnit test source when one can be generated.", width: 82);
        _statusContent = new Panel
        {
            Content = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 6,
                Padding = new Padding(10, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(_statusTextArea, HorizontalAlignment.Stretch),
                    CreateResponsiveControlGroup(4, copyStatusButton, copyCaseButton)
                }
            },
            Visible = _statusExpanded,
            BackgroundColor = UiTheme.CardBackground
        };

        var statusCard = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Padding = new Padding(8, 2),
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

        _tabContentPanel = new Panel { Content = tabScrollables[Math.Clamp(_selectedTabIndex, 0, tabScrollables.Length - 1)] };

        void SelectTab(int index)
        {
            _selectedTabIndex = Math.Clamp(index, 0, tabScrollables.Length - 1);
            _tabContentPanel.Content = tabScrollables[_selectedTabIndex];
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
            var label = new Label
            {
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 12
            };
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
            var textLabel = new Label
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            };
            var mainRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
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
                    Width = 16,
                    Height = 16,
                    Size = new Size(16, 16)
                };
                mainRow.Items.Add(iconView);
            }

            mainRow.Items.Add(textLabel);
            var mainPanel = new Panel
            {
                Padding = toggleLabel == null ? new Padding(8, 5) : new Padding(8, 5, 6, 5),
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
                    Padding = new Padding(6, 5, 8, 5),
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
            Spacing = 2,
            Padding = new Padding(4, 2, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Bottom,
            Items =
            {
                MakeTabHeader("Modifiers", "TabModifiers", 0),
                MakeTabHeader("Objects", "TabMarkers", 1),
                MakeTabHeader("Zones", "TabZones", 2, _zonesEyeButton),
                MakeTabHeader("Analysis", "TabAnalysis", 3, _analysisEyeButton),
                MakeTabHeader("Annotation", "TabAnnotation", 4),
                new StackLayoutItem(new Panel(), expand: true)
            }
        };
        UpdateTabSelectionStyles();

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
        return new Scrollable
        {
            Content = content,
            Border = BorderType.None,
            ExpandContentWidth = true,
            ExpandContentHeight = false
        };
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

    private void HandleControllerStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !_isPanelLoaded)
            return;

        Application.Instance?.AsyncInvoke(() =>
        {
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

                CaptureSelectedTabIndex();
                _responsiveLayoutKey = GetResponsiveLayoutKey();
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
            _terrainColorLabel.TextColor = UiTheme.MutedText;
            _statusHintLabel.TextColor   = UiTheme.MutedText;
            CaptureSelectedTabIndex();
            _responsiveLayoutKey = GetResponsiveLayoutKey();
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
        _deferredControllerRefreshDepth++;
    }

    private void EndControllerRefreshDeferral()
    {
        if (_deferredControllerRefreshDepth <= 0)
            return;

        _deferredControllerRefreshDepth--;
        if (_deferredControllerRefreshDepth != 0 || !_hasDeferredControllerRefresh || IsDisposed || !_isPanelLoaded)
            return;

        _hasDeferredControllerRefresh = false;
        RefreshUi();
    }

    private void HandlePanelSizeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !_isPanelLoaded)
            return;

        int layoutKey = GetResponsiveLayoutKey();
        if (layoutKey == _responsiveLayoutKey)
            return;

        _responsiveLayoutKey = layoutKey;
        if (_isRefreshing)
            return;

        Application.Instance?.AsyncInvoke(() =>
        {
            if (IsDisposed)
                return;

            CaptureSelectedTabIndex();
            Content = BuildContent();
            RefreshUi();
        });
    }

    private int GetResponsiveLayoutKey()
    {
        if (IsDisposed)
            return _responsiveLayoutKey >= 0 ? _responsiveLayoutKey : 0;

        int width;
        try
        {
            width = ClientSize.Width > 0 ? ClientSize.Width : Width;
        }
        catch (ObjectDisposedException)
        {
            return _responsiveLayoutKey >= 0 ? _responsiveLayoutKey : 0;
        }

        if (width <= 0)
            return 0;

        int key = 0;
        if (width < StackedSourceEditorWidth)
            key |= 1;
        if (width < WrappedModifierHeaderWidth)
            key |= 2;
        if (width < StackedFormRowWidth)
            key |= 4;
        if (width < CompactCardHeaderWidth)
            key |= 8;
        return key;
    }

    private bool UseStackedSourceEditors() => (_responsiveLayoutKey & 1) != 0;

    private bool UseStackedFormRows() => (_responsiveLayoutKey & 4) != 0;

    private bool UseCompactCardHeaders() => (_responsiveLayoutKey & 8) != 0;

    private bool UseWrappedModifierActions() => (_responsiveLayoutKey & 2) != 0;

    private void OnTerrainPickerDropDownChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingTerrainPicker || _isRefreshing)
            return;

        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        int index = _terrainPickerDropDown.SelectedIndex;
        var terrains = _controller.GetTerrains(doc).ToList();
        if (index >= 0 && index < terrains.Count)
        {
            _controller.SetSelectedTerrain(doc, terrains[index].TerrainId);
            RefreshUi();
        }
    }

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

    private void OnAssignTerrainLayer(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        MutateSelectedTerrain(terrain => terrain.TerrainLayerPath = doc.Layers.CurrentLayer?.FullPath, scheduleRebuild: false);
        RefreshUi();
    }

    private void OnAssignAuxLayer(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        MutateSelectedTerrain(terrain => terrain.AuxiliaryLayerPath = doc.Layers.CurrentLayer?.FullPath, scheduleRebuild: true);
        RefreshUi();
    }

    private void OnAssignAnnotationLayer(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        MutateSelectedTerrain(terrain => terrain.AnnotationLayerPath = doc.Layers.CurrentLayer?.FullPath, scheduleRebuild: false);
        RefreshUi();
    }

    private void ApplyTerrainOpacity(int opacityPercent)
    {
        MutateSelectedTerrain(
            terrain => terrain.TerrainColorArgb = WithOpacityPercent(terrain.TerrainColorArgb, opacityPercent),
            scheduleRebuild: false);

        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc != null && terrain != null)
            _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
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

    private void RefreshUi()
    {
        if (IsDisposed)
            return;

        // Keep shared label instances in sync with the current theme.
        _terrainColorLabel.TextColor = UiTheme.MutedText;
        _statusHintLabel.TextColor   = UiTheme.MutedText;

        var doc = RhinoDoc.ActiveDoc;
        _responsiveLayoutKey = GetResponsiveLayoutKey();
        _isRefreshing = true;
        try
        {
            if (doc == null)
            {
                _terrainName.Text = string.Empty;
                _liveUpdate.Checked = false;
                SetStatusText("No active Rhino document.");
                _terrainLayerLabel.Text = "-";
                _auxLayerLabel.Text = "-";
                _terrainColorSwatch.BackgroundColor = Color.FromArgb(80, 80, 80);
                _terrainColorLabel.Text = "-";
                _statusHintLabel.Text = string.Empty;
                SetTerrainOpacityControls(100);
                _showWiresCheck.Checked = false;
                _showSlowBuildWarningCheck.Checked = true;
                _replacePreviousBakesCheck.Checked = false;
                _untrackSelectedBakesButton.Enabled = false;
                _untrackAllBakesButton.Enabled = false;
                _toleranceStepper.Value = 0;
                _isUpdatingTerrainPicker = true;
                _terrainPickerDropDown.Items.Clear();
                _terrainPickerDropDown.SelectedIndex = -1;
                _isUpdatingTerrainPicker = false;
                _visibilityButton.Text = "Shown";
                _lockButton.Text = "Unlocked";
                SetActionButtonsEnabled(false);
                _terrainName.Enabled = false;
                _modifierStack.Items.Clear();
                _objectsStack.Items.Clear();
                _zonesStack.Items.Clear();
                _markerStack.Items.Clear();
                _analysisStack.Items.Clear();
                _annotationStack.Items.Clear();
                return;
            }

            var terrains = _controller.GetTerrains(doc).ToList();

            var selectedTerrain = _controller.GetSelectedTerrain(doc);
            if (selectedTerrain == null && terrains.Count > 0)
            {
                _controller.SetSelectedTerrain(doc, terrains[0].TerrainId);
                selectedTerrain = terrains[0];
            }

            _isUpdatingTerrainPicker = true;
            _terrainPickerDropDown.Items.Clear();
            int selectedPickerIndex = 0;
            for (int ti = 0; ti < terrains.Count; ti++)
            {
                _terrainPickerDropDown.Items.Add(terrains[ti].Name);
                if (selectedTerrain != null && terrains[ti].TerrainId == selectedTerrain.TerrainId)
                    selectedPickerIndex = ti;
            }
            _terrainPickerDropDown.SelectedIndex = terrains.Count > 0 ? selectedPickerIndex : -1;
            _terrainPickerDropDown.Enabled = terrains.Count > 1;
            _isUpdatingTerrainPicker = false;

            _terrainName.Text = selectedTerrain?.Name ?? string.Empty;
            _liveUpdate.Checked = selectedTerrain?.LiveUpdateEnabled ?? false;
            _terrainLayerLabel.Text = string.IsNullOrWhiteSpace(selectedTerrain?.TerrainLayerPath) ||
                string.Equals(selectedTerrain.TerrainLayerPath, TerrainDefinition.DefaultTerrainLayerPath, StringComparison.OrdinalIgnoreCase)
                ? TerrainDefinition.DefaultTerrainLayerPath
                : GetLeafLayerName(selectedTerrain.TerrainLayerPath);
            _auxLayerLabel.Text = string.IsNullOrWhiteSpace(selectedTerrain?.AuxiliaryLayerPath) ||
                string.Equals(selectedTerrain.AuxiliaryLayerPath, TerrainDefinition.DefaultAuxiliaryLayerPath, StringComparison.OrdinalIgnoreCase)
                ? TerrainDefinition.DefaultAuxiliaryLayerPath
                : GetLeafLayerName(selectedTerrain.AuxiliaryLayerPath);
            _annotationLayerLabel.Text = string.IsNullOrWhiteSpace(selectedTerrain?.AnnotationLayerPath) ||
                string.Equals(selectedTerrain.AnnotationLayerPath, TerrainDefinition.DefaultAnnotationLayerPath, StringComparison.OrdinalIgnoreCase)
                ? TerrainDefinition.DefaultAnnotationLayerPath
                : GetLeafLayerName(selectedTerrain.AnnotationLayerPath);
            int terrainColorArgb = selectedTerrain?.TerrainColorArgb ?? TerrainDefinition.DefaultTerrainColorArgb;
            var terrainColor = ToEtoColor(System.Drawing.Color.FromArgb(terrainColorArgb));
            _terrainColorSwatch.BackgroundColor = terrainColor;
            _terrainColorLabel.Text = DescribeTerrainColor(terrainColorArgb);
            SetTerrainOpacityControls(GetOpacityPercent(terrainColorArgb));
            _showWiresCheck.Checked = selectedTerrain?.ShowMeshWires ?? false;
            _showSlowBuildWarningCheck.Checked = selectedTerrain?.ShowSlowBuildWarning ?? true;
            _replacePreviousBakesCheck.Checked = selectedTerrain?.ReplacePreviouslyBaked ?? false;
            _toleranceStepper.Value = selectedTerrain?.GlobalTolerance ?? 0;
            SetStatusText(
                selectedTerrain?.LastBuildMessage ?? "Create a terrain to start.",
                selectedTerrain?.LastStructuredDiagnostics);
            _visibilityButton.Text = selectedTerrain?.IsVisible != false ? "Shown" : "Hidden";
            _lockButton.Text = selectedTerrain?.IsLocked == true ? "Locked" : "Unlocked";
            bool hasTerrain = selectedTerrain != null;
            SetActionButtonsEnabled(hasTerrain);
            bool hasTrackedBakes = selectedTerrain != null && selectedTerrain.BakedObjectIds.Count > 0;
            _untrackSelectedBakesButton.Enabled = hasTerrain && hasTrackedBakes;
            _untrackAllBakesButton.Enabled = hasTerrain && hasTrackedBakes;
            _terrainName.Enabled = hasTerrain;

            UpdateZonesTabButton(_zonesEyeButton, selectedTerrain);
            UpdateAnalysisTabButton(_analysisEyeButton, selectedTerrain);

            RebuildModifierLayout(selectedTerrain);
            RebuildObjectsLayout(selectedTerrain);
            RebuildZonesLayout(selectedTerrain);
            RebuildMarkerLayout(selectedTerrain);
            RebuildAnalysisLayout(selectedTerrain);
            RebuildAnnotationLayout(selectedTerrain);
        }
        finally
        {
            _isRefreshing = false;
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
            var strip = new Panel { Width = 5, BackgroundColor = typeColor };
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
            var strip = new Panel { Width = 5, BackgroundColor = TerrainObjectTypeColor(GetTerrainObjectKind(definition)) };
            var wrapper = WrapCardControl(box, strip, UiTheme.CardBackground);
            _objectsStack.Items.Add(new StackLayoutItem(wrapper, HorizontalAlignment.Stretch));
        }
    }

    private void RebuildZonesLayout(TerrainDefinition? terrain)
    {
        _zoneCardMap.Clear();
        _zoneSepMap.Clear();
        _zonesStack.Items.Clear();
        _zonesStack.Items.Add(new StackLayoutItem(BuildZonesToolbar(terrain), HorizontalAlignment.Stretch));

        if (terrain == null)
            return;

        _zonesStack.Items.Add(new StackLayoutItem(new Panel
        {
            Padding = new Padding(6, 0, 6, 4),
            Content = new Label
            {
                Text = "Later zones win. Use input Z when stacked inputs should resolve by elevation.",
                TextColor = UiTheme.MutedText,
                Wrap = WrapMode.Word
            }
        }, HorizontalAlignment.Stretch));

        var terrainId = terrain.TerrainId;
        foreach (var zone in terrain.Zones)
        {
            var zoneId = zone.ZoneId;

            var zoneInner = new Panel { BackgroundColor = Colors.Transparent };
            var zoneOuter = new Panel { Height = 8, Padding = new Padding(0, 2), Content = zoneInner };
            ApplyHelp(zoneOuter, "Drop here to reorder zones.");
            _zoneSepMap[zoneId] = zoneInner;
            WireZoneSepDragDrop(zoneOuter, zoneInner, terrainId, zoneId);
            _zonesStack.Items.Add(new StackLayoutItem(zoneOuter, HorizontalAlignment.Stretch));

            var box = CreateZoneCard(terrain, zone);
            var zoneStrip = new Panel { Width = 5, BackgroundColor = UiTheme.ZoneStripColor };
            var zoneWrapper = WrapCardControl(box, zoneStrip, UiTheme.CardBackground);
            _zoneCardMap[zoneId] = zoneWrapper;
            WireZoneCardDragDrop(zoneWrapper, terrainId, zoneId);
            _zonesStack.Items.Add(new StackLayoutItem(zoneWrapper, HorizontalAlignment.Stretch));
        }

        var tailZoneInner = new Panel { BackgroundColor = Colors.Transparent };
        var tailZoneOuter = new Panel { Height = 8, Padding = new Padding(0, 2), Content = tailZoneInner };
        ApplyHelp(tailZoneOuter, "Drop here to reorder zones.");
        _zoneSepMap[Guid.Empty] = tailZoneInner;
        WireZoneSepDragDrop(tailZoneOuter, tailZoneInner, terrainId, Guid.Empty);
        _zonesStack.Items.Add(new StackLayoutItem(tailZoneOuter, HorizontalAlignment.Stretch));
    }

    private void RebuildMarkerLayout(TerrainDefinition? terrain)
    {
        _markerStack.Items.Clear();
        if (terrain == null)
            return;

        _markerStack.Items.Add(new StackLayoutItem(BuildMarkerAddButtons(terrain), HorizontalAlignment.Stretch));
        foreach (var marker in terrain.Markers)
            _markerStack.Items.Add(new StackLayoutItem(CreateMarkerGroup(terrain, marker), HorizontalAlignment.Stretch));
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

        var visualAnalyses = terrain.Analyses.Where(a => !IsAnnotationAnalysis(a)).ToList();
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
            var strip = new Panel { Width = 5, BackgroundColor = typeColor };
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

        var annotationItems = terrain.Analyses.Where(IsAnnotationAnalysis).ToList();
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

            var box = CreateAnalysisCard(terrain, analysisItem, isActive: false);
            _annotationCardMap[analysisId] = box;
            var kind = GetAnalysisKind(analysisItem);
            var typeColor = AnalysisTypeColor(kind);
            var strip = new Panel { Width = 5, BackgroundColor = typeColor };
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
            Spacing = 4,
            Padding = new Padding(8, 8, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new Label
                {
                    Text = title,
                    TextColor = UiTheme.MutedText
                }
            }
        };

        bool hasHelperText = !string.IsNullOrWhiteSpace(helperText);
        if (UseStackedFormRows())
        {
            section.Items.Add(new StackLayoutItem(CreateLeftAlignedControlRow(primaryButton), HorizontalAlignment.Stretch));
            if (hasHelperText)
            {
                section.Items.Add(new StackLayoutItem(new Label
                {
                    Text = helperText,
                    TextColor = UiTheme.MutedText,
                    Wrap = WrapMode.Word
                }, HorizontalAlignment.Stretch));
            }

            if (trailingControl != null)
                section.Items.Add(new StackLayoutItem(trailingControl, HorizontalAlignment.Stretch));

            return section;
        }

        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                primaryButton
            }
        };

        if (hasHelperText)
        {
            row.Items.Add(new StackLayoutItem(new Label
            {
                Text = helperText,
                TextColor = UiTheme.MutedText,
                VerticalAlignment = VerticalAlignment.Center,
                Wrap = WrapMode.Word
            }, expand: true));
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

    private static StackLayout CreateLeftAlignedControlRow(Control control)
    {
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                control,
                new StackLayoutItem(new Panel(), expand: true)
            }
        };
    }

    private StackLayout CreateResponsiveControlGroup(int spacing, params Control[] controls)
    {
        bool stackVertically = UseStackedFormRows();
        var group = new StackLayout
        {
            Orientation = stackVertically ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = spacing,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        foreach (var control in controls)
        {
            if (stackVertically)
                group.Items.Add(new StackLayoutItem(control, HorizontalAlignment.Stretch));
            else
                group.Items.Add(control);
        }

        return group;
    }

    private Control CreateResponsivePrimaryActionRow(Control primaryControl, int spacing, params Control[] actionControls)
    {
        if (UseStackedFormRows())
        {
            var layout = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = spacing,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(primaryControl, HorizontalAlignment.Stretch)
                }
            };

            if (actionControls.Length > 0)
                layout.Items.Add(new StackLayoutItem(CreateResponsiveControlGroup(spacing, actionControls), HorizontalAlignment.Stretch));

            return layout;
        }

        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = spacing,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(primaryControl, expand: true)
            }
        };

        foreach (var actionControl in actionControls)
            row.Items.Add(actionControl);

        return row;
    }

    private static Label CreateCardStatusLabel(string text, Color? textColor = null)
    {
        return new Label
        {
            Text = text,
            TextColor = textColor ?? UiTheme.MutedText,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.Word
        };
    }

    private static Label CreateCardMetaLabel(string text)
    {
        return new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = UiTheme.MutedText,
            Wrap = WrapMode.Word
        };
    }

    private static Panel CreateIconPlate(Color accent, Control content)
    {
        return new Panel
        {
            BackgroundColor = new Color(accent.R, accent.G, accent.B, 0.20f),
            Padding = new Padding(6, 4),
            Content = content
        };
    }

    private static Panel WrapCardControl(Control card, Panel accentStrip, Color backgroundColor)
    {
        return new Panel
        {
            Padding = new Padding(4, 2),
            BackgroundColor = backgroundColor,
            Content = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    accentStrip,
                    new StackLayoutItem(card, expand: true)
                }
            }
        };
    }

    private Panel CreateSharedCardShell(SharedCardShellOptions options)
    {
        bool wrapActions = UseWrappedModifierActions();
        bool compactHeader = UseCompactCardHeaders();

        var header = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = compactHeader || wrapActions ? 4 : 0,
            Padding = new Padding(8, 6, 8, 6),
            BackgroundColor = options.HeaderBackground
        };

        if (compactHeader)
        {
            var compactMetaRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    options.Handle,
                    options.CollapseControl,
                    options.IconPlate,
                    options.EnabledControl,
                    new StackLayoutItem(new Panel(), expand: true)
                }
            };
            header.Items.Add(new StackLayoutItem(compactMetaRow, HorizontalAlignment.Stretch));
            header.Items.Add(new StackLayoutItem(options.TitleBlock, HorizontalAlignment.Stretch));
            if (options.StatusControls.Count > 0)
                header.Items.Add(new StackLayoutItem(CreateCardStatusRow(options.StatusControls), HorizontalAlignment.Stretch));
            if (options.ActionControls.Count > 0)
                header.Items.Add(new StackLayoutItem(CreateCardActionRow(options.ActionControls), HorizontalAlignment.Stretch));
        }
        else
        {
            var headerTopRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    options.Handle,
                    options.CollapseControl,
                    options.IconPlate,
                    options.EnabledControl,
                    new StackLayoutItem(options.TitleBlock, expand: true)
                }
            };
            foreach (var statusControl in options.StatusControls)
                headerTopRow.Items.Add(new StackLayoutItem(statusControl));

            if (options.ActionControls.Count > 0 && !wrapActions)
            {
                foreach (var actionControl in options.ActionControls)
                    headerTopRow.Items.Add(new StackLayoutItem(actionControl));
            }

            header.Items.Add(new StackLayoutItem(headerTopRow, HorizontalAlignment.Stretch));
            if (options.ActionControls.Count > 0 && wrapActions)
                header.Items.Add(new StackLayoutItem(CreateCardActionRow(options.ActionControls), HorizontalAlignment.Stretch));
        }

        header.MouseDown += (_, e) => options.ToggleCollapsed(e.Modifiers.HasFlag(Keys.Control));

        var card = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0
        };
        card.Items.Add(new StackLayoutItem(header, HorizontalAlignment.Stretch));
        if (!options.Collapsed && options.Body != null)
            card.Items.Add(new StackLayoutItem(options.Body, HorizontalAlignment.Stretch));

        return new Panel
        {
            BackgroundColor = options.CardBackground,
            Content = card
        };
    }

    private static StackLayout CreateCardStatusRow(IReadOnlyList<Control> controls)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        if (controls.Count == 0)
            return row;

        row.Items.Add(new StackLayoutItem(controls[0], expand: true));
        for (int index = 1; index < controls.Count; index++)
            row.Items.Add(new StackLayoutItem(controls[index]));

        return row;
    }

    private static StackLayout CreateCardActionRow(IReadOnlyList<Control> actions)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(new Panel(), expand: true)
            }
        };

        foreach (var action in actions)
            row.Items.Add(new StackLayoutItem(action));

        return row;
    }

    private Control BuildAddModifierBar(TerrainDefinition? terrain)
    {
        var addButton = MakeToolbarButton("Add Modifier", (_, _) => { }, "Add a modifier above the base geometry", width: 110);
        if (terrain != null)
        {
            var menu = new ContextMenu();
            foreach (var (label, kind) in ModifierKinds)
            {
                var item = new ButtonMenuItem
                {
                    Text = label,
                    Image = PanelIcons.Load(GetModifierIconName(kind))
                };
                var capturedKind = kind;
                var capturedTerrainId = terrain.TerrainId;
                item.Click += (_, _) =>
                {
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc != null)
                        _controller.AddModifier(doc, capturedTerrainId, capturedKind);
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

    private Control BuildZonesToolbar(TerrainDefinition? terrain)
    {
        var addButton = MakeToolbarButton("Add Zone", (_, _) => { }, "Add a zone definition.", width: 94);
        ApplyHelp(addButton, "Add a blank zone, or create zones from highlighted layers.");
        addButton.Enabled = terrain != null;
        if (terrain != null)
        {
            var capturedTerrainId = terrain.TerrainId;
            var menu = new ContextMenu();

            var addBlankItem = new ButtonMenuItem { Text = "Add Zone" };
            addBlankItem.Click += (_, _) =>
            {
                MutateSelectedTerrain(selected =>
                {
                    selected.Zones.Add(new CollageZoneDefinition { Name = "Zone" });
                });
            };
            menu.Items.Add(addBlankItem);

            var addFromLayersItem = new ButtonMenuItem { Text = "From Highlighted Layers" };
            addFromLayersItem.Click += (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                    return;

                var selectedLayers = _controller.GetSelectedLayerPaths(doc);
                if (selectedLayers.Count == 0)
                    return;

                MutateSelectedTerrain(selected =>
                {
                    var existing = selected.Zones
                        .SelectMany(zone => zone.Boundaries.LayerPaths)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var layerPath in selectedLayers)
                    {
                        if (!existing.Add(layerPath))
                            continue;

                        selected.Zones.Add(new CollageZoneDefinition
                        {
                            Name = GetLeafLayerName(layerPath),
                            Boundaries = new SourceReferenceSet
                            {
                                LayerPaths = new List<string> { layerPath }
                            }
                        });
                    }
                });
            };
            menu.Items.Add(addFromLayersItem);
            addButton.Click += (_, _) => menu.Show(addButton);
        }

        bool showZonesOnly = terrain?.ShowZoneMeshes == true && terrain?.ShowTerrainMesh != true;
        var terrainView = new RadioButton
        {
            Text = "Terrain",
            Checked = !showZonesOnly
        };
        var zonesView = new RadioButton(terrainView)
        {
            Text = "Zones",
            Checked = showZonesOnly
        };
        terrainView.Enabled = terrain != null;
        zonesView.Enabled = terrain != null;
        ApplyHelp(terrainView, "Show the terrain mesh and hide split zones.");
        ApplyHelp(zonesView, "Show split zones and hide the full terrain mesh.");
        terrainView.CheckedChanged += (_, _) =>
        {
            if (terrain == null || terrainView.Checked != true)
                return;

            MutateSelectedTerrain(selected =>
            {
                selected.ShowTerrainMesh = true;
                selected.ShowZoneMeshes = false;
            }, scheduleRebuild: false);
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
        };
        zonesView.CheckedChanged += (_, _) =>
        {
            if (terrain == null || zonesView.Checked != true)
                return;

            MutateSelectedTerrain(selected =>
            {
                selected.ShowTerrainMesh = false;
                selected.ShowZoneMeshes = true;
            }, scheduleRebuild: false);
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
        };

        var viewToggleRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "View", TextColor = UiTheme.MutedText, VerticalAlignment = VerticalAlignment.Center },
                terrainView,
                zonesView
            }
        };

        return CreateSectionToolbar(
            "ZONE OUTPUT",
            addButton,
            trailingControl: viewToggleRow);
    }

    private Control BuildAnalysisToolbar(TerrainDefinition terrain)
    {
        var addButton = MakeToolbarButton("Add Analysis", (_, _) => { }, "Add an analysis card", width: 110);
        var menu = new ContextMenu();
        foreach (var (label, kind) in AnalysisKinds)
        {
            var item = new ButtonMenuItem
            {
                Text = label
            };
            var capturedKind = kind;
            item.Click += (_, _) => AddAnalysis(capturedKind);
            menu.Items.Add(item);
        }

        addButton.Click += (_, _) => menu.Show(addButton);
        return CreateSectionToolbar("ANALYSIS", addButton);
    }

    private Control BuildAnnotationToolbar(TerrainDefinition terrain)
    {
        var addButton = MakeToolbarButton("Add Annotation", (_, _) => { }, "Add an annotation card", width: 120);
        var menu = new ContextMenu();
        foreach (var (label, kind) in AnnotationKinds)
        {
            var item = new ButtonMenuItem { Text = label };
            var capturedKind = kind;
            item.Click += (_, _) => AddAnalysis(capturedKind);
            menu.Items.Add(item);
        }
        addButton.Click += (_, _) => menu.Show(addButton);
        return CreateSectionToolbar("ANNOTATION", addButton);
    }

    private Panel CreateAnalysisCard(TerrainDefinition terrain, AnalysisDefinition analysis, bool isActive)
    {
        bool collapsed = _collapsedAnalyses.Contains(analysis.Id);
        var collapseLabel = new Label
        {
            Text = collapsed ? "▶" : "▼",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 14
        };

        var nameBox = new TextBox { Text = analysis.Label };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Friendly analysis name shown in the panel.");
        BindCommittedText(nameBox, () => analysis.Label, text =>
            MutateAnalysis(terrain.TerrainId, analysis.Id, item => item.Label = text, scheduleRebuild: false));

        var enabledCheck = new CheckBox
        {
            Checked = analysis.IsEnabled
        };
        ApplyHelp(enabledCheck, "Enable or disable this analysis card without deleting it.");
        enabledCheck.CheckedChanged += (_, _) =>
        {
            MutateAnalysis(terrain.TerrainId, analysis.Id, item => item.IsEnabled = enabledCheck.Checked == true, scheduleRebuild: false);
            RefreshTerrainPreview(terrain.TerrainId);
        };

        string kind = GetAnalysisKind(analysis);
        string typeLabel = GetAnalysisTypeLabel(analysis);
        bool supportsPreview = TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(analysis);
        bool producesOutput = TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(analysis);
        string statusText = isActive
            ? "Preview"
            : analysis.IsEnabled
                ? supportsPreview ? "Enabled" : producesOutput ? "Output" : "Summary"
                : "Disabled";
        var badge = CreateCardStatusLabel(statusText, isActive ? UiTheme.ActiveBadge : UiTheme.MutedText);

        var handle = CreateDragHandle();
        ApplyHelp(handle, "Drag to reorder this analysis.");
        handle.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;

            var data = new DataObject();
            data.SetString(analysis.Id.ToString(), "analysis-drag");
            handle.DoDragDrop(data, DragEffects.Move);
        };

        var accent = AnalysisTypeColor(kind);
        var iconPlate = CreateIconPlate(accent, new Label
        {
            Text = GetAnalysisIconLabel(analysis),
            VerticalAlignment = VerticalAlignment.Center
        });

        Control titleBlock = collapsed
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                Items =
                {
                    new Label
                    {
                        Text = analysis.Label,
                        Font = new Font(SystemFont.Bold),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    CreateCardMetaLabel(GetAnalysisCollapsedSummary(terrain, analysis))
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                Items =
                {
                    nameBox,
                    CreateCardMetaLabel(GetAnalysisSubtitle(analysis, isActive))
                }
            };

        var copyButton = MakeMiniButton("Copy", (_, _) =>
        {
            DuplicateAnalysis(terrain.TerrainId, analysis.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Duplicate this analysis card.", width: 46);
        var deleteButton = MakeMiniButton("Del", (_, _) =>
        {
            RemoveAnalysis(terrain.TerrainId, analysis.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Delete this analysis card.", width: 38);

        void ToggleCollapsed(bool ctrlHeld)
        {
            bool nowCollapsed = !_collapsedAnalyses.Contains(analysis.Id);
            if (ctrlHeld)
            {
                var doc2 = RhinoDoc.ActiveDoc;
                var t = doc2 == null ? null : _controller.GetSelectedTerrain(doc2);
                if (t != null)
                {
                    if (nowCollapsed)
                        foreach (var item in t.Analyses) _collapsedAnalyses.Add(item.Id);
                    else
                        _collapsedAnalyses.Clear();
                }
            }
            else
            {
                if (nowCollapsed)
                    _collapsedAnalyses.Add(analysis.Id);
                else
                    _collapsedAnalyses.Remove(analysis.Id);
            }

            var doc = RhinoDoc.ActiveDoc;
            RebuildAnalysisLayout(doc == null ? null : _controller.GetSelectedTerrain(doc));
        }

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = new Control[]
            {
                CreateCardStatusLabel(typeLabel),
                badge
            },
            ActionControls = new Control[]
            {
                copyButton,
                deleteButton
            },
            ToggleCollapsed = ToggleCollapsed,
            Collapsed = collapsed,
            Body = collapsed ? null : CreateAnalysisBody(terrain, analysis)
        });
    }

    private Control CreateAnalysisBody(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 6), Padding = new Padding(10, 8, 10, 8) };
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, analysis.Id);

        switch (analysis)
        {
            case EarthworkAnalysisDefinition earthwork:
                layout.AddRow(CreateSourceEditor("Compare To", earthwork.Reference,
                    apply => MutateAnalysis(terrain.TerrainId, earthwork.Id, item => apply(((EarthworkAnalysisDefinition)item).Reference), scheduleRebuild: true),
                    RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Boundary", earthwork.Boundary,
                    apply => MutateAnalysis(terrain.TerrainId, earthwork.Id, item => apply(((EarthworkAnalysisDefinition)item).Boundary), scheduleRebuild: true),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                if (summary != null)
                {
                    string summaryText =
                        $"Cut: {FormatVolume(summary.CutVolume)}{Environment.NewLine}" +
                        $"Fill: {FormatVolume(summary.FillVolume)}{Environment.NewLine}" +
                        $"Net: {FormatVolume(summary.NetVolume)}{Environment.NewLine}" +
                        $"Mode: {(summary.EarthworkIsEstimated ? "Estimated from terrain delta" : "Exact")}";
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        summaryText,
                        "Earthwork summary from the last terrain build. Click into the field to select and copy values."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to populate earthwork values.",
                        minHeight: 42));
                }
                break;

            case SlopeAnalysisDefinition slope:
                layout.AddRow(CreateSlopeUnitEditor(terrain.TerrainId, slope));
                layout.AddRow(CreateAnalysisPaletteEditor(
                    terrain.TerrainId,
                    slope,
                    "Color ramp used for the slope analysis preview."));
                layout.AddRow(CreateAnalysisRangeEditor(
                    $"Low {GetSlopeUnitSuffixLabel(slope.Unit)}",
                    slope.RangeLow,
                    value => MutateAndRefreshAnalysis(terrain.TerrainId, slope.Id, item => item.RangeLow = value),
                    "Values at or below this slope use the low end of the selected palette."));
                layout.AddRow(CreateAnalysisRangeEditor(
                    $"High {GetSlopeUnitSuffixLabel(slope.Unit)}",
                    slope.RangeHigh,
                    value => MutateAndRefreshAnalysis(terrain.TerrainId, slope.Id, item => item.RangeHigh = value),
                    "Values at or above this slope use the high end of the selected palette. Leave at 0 to auto-fit."));
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        $"{FormatSlopeSummaryValue(summary.SlopeMinPercent, slope.Unit)} / {FormatSlopeSummaryValue(summary.SlopeAveragePercent, slope.Unit)} / {FormatSlopeSummaryValue(summary.SlopeMaxPercent, slope.Unit)}",
                        "Current terrain slope summary from the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Mapped",
                        $"{FormatSlopeSummaryValue(summary.SlopeDisplayLowPercent, slope.Unit)} to {FormatSlopeSummaryValue(summary.SlopeDisplayHighPercent, slope.Unit)}",
                        "Actual slope range currently mapped across the selected palette."));
                }
                // Legend shows the actual mapped range from the last build
                {
                    string sLow  = summary != null
                        ? FormatSlopeValue(ConvertPercentToSlopeUnit(summary.SlopeDisplayLowPercent, slope.Unit), slope.Unit)
                        : FormatSlopeValue(slope.RangeLow, slope.Unit);
                    string sHigh = summary != null
                        ? FormatSlopeValue(ConvertPercentToSlopeUnit(summary.SlopeDisplayHighPercent, slope.Unit), slope.Unit)
                        : FormatSlopeValue(slope.RangeHigh, slope.Unit);
                    layout.AddRow(CreateSlopeLegendView(
                        SlopePreviewPaletteCatalog.Resolve(slope.PalettePreset),
                        displayLowLabel: sLow,
                        displayHighLabel: sHigh));
                }
                break;

            case ElevationAnalysisDefinition elevation:
                layout.AddRow(CreateAnalysisPaletteEditor(
                    terrain.TerrainId,
                    elevation,
                    "Color ramp used for the elevation analysis preview."));
                layout.AddRow(CreateAnalysisRangeEditor(
                    "Low Z",
                    elevation.RangeLow,
                    value => MutateAndRefreshAnalysis(terrain.TerrainId, elevation.Id, item => item.RangeLow = value),
                    "Values at or below this elevation use the low end of the selected palette. Set to 0 to auto-fit."));
                layout.AddRow(CreateAnalysisRangeEditor(
                    "High Z",
                    elevation.RangeHigh,
                    value => MutateAndRefreshAnalysis(terrain.TerrainId, elevation.Id, item => item.RangeHigh = value),
                    "Values at or above this elevation use the high end of the selected palette. Set to 0 to auto-fit."));
                if (summary != null)
                    layout.AddRow(CreateReadOnlyValueRow("Area", $"{summary.SurfaceArea:F2} sq units", "Terrain surface area from the last build."));
                {
                    // Actual low/high Z driven by either the configured range or auto-fit from last build
                    var a = summary;
                    double eLow  = (a != null && elevation.RangeLow == 0 && elevation.RangeHigh <= elevation.RangeLow)
                        ? a.ElevationMinZ : (elevation.RangeLow != 0 ? elevation.RangeLow : a?.ElevationMinZ ?? 0);
                    double eHigh = (a != null && elevation.RangeHigh <= elevation.RangeLow)
                        ? a.ElevationMaxZ : (elevation.RangeHigh > elevation.RangeLow ? elevation.RangeHigh : a?.ElevationMaxZ ?? 0);
                    layout.AddRow(CreateSlopeLegendView(
                        SlopePreviewPaletteCatalog.Resolve(elevation.PalettePreset),
                        displayLowLabel:  a != null ? $"{eLow:F1}" : "Low Z",
                        displayHighLabel: a != null ? $"{eHigh:F1}" : "High Z"));
                }
                break;

            case CutFillAnalysisDefinition cutFill:
                layout.AddRow(CreateSourceEditor("Compare To", cutFill.Reference,
                    apply => MutateAnalysis(terrain.TerrainId, cutFill.Id, item => apply(((CutFillAnalysisDefinition)item).Reference), scheduleRebuild: true),
                    RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Boundary", cutFill.Boundary,
                    apply => MutateAnalysis(terrain.TerrainId, cutFill.Id, item => apply(((CutFillAnalysisDefinition)item).Boundary), scheduleRebuild: true),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateAnalysisPaletteEditor(
                    terrain.TerrainId,
                    cutFill,
                    "Color ramp used for cut/fill analysis. Auto-fits symmetrically to the largest delta."));
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow("Cut / Fill / Net",
                        $"{summary.CutVolume:F2} / {summary.FillVolume:F2} / {summary.NetVolume:F2}",
                        "Current earthworks summary from the last build."));
                }
                {
                    var a = summary;
                    double absMax = a?.CutFillDisplayAbsMax ?? 0.0;
                    string cfLow  = a != null ? $"{-absMax:F2}" : "Cut";
                    string cfHigh = a != null ? $"+{absMax:F2}" : "Fill";
                    layout.AddRow(CreateSlopeLegendView(
                        SlopePreviewPaletteCatalog.Resolve(cutFill.PalettePreset),
                        displayLowLabel: cfLow,
                        displayHighLabel: cfHigh));
                }
                break;

            case CurveElevationLabelAnalysisDefinition curveElevation:
            {
                void MutateCurveElevation(Action<CurveElevationLabelAnalysisDefinition> apply)
                {
                    MutateAnalysis(
                        terrain.TerrainId,
                        curveElevation.Id,
                        item => apply((CurveElevationLabelAnalysisDefinition)item),
                        scheduleRebuild: true);
                }

                AddBlockAttributeAnalysisRows(
                    layout,
                    terrain,
                    curveElevation,
                    RhinoObjectType.Curve,
                    MutateCurveElevation,
                    "Curve objects or layers sampled along the terrain at regular stations for elevation labels.",
                    "Numeric format string applied to sampled elevation values, for example F2 or 0.00.",
                    extraRows: extraLayout =>
                    {
                        extraLayout.AddRow(CreateNumericEditor(
                            "Interval",
                            curveElevation.Interval,
                            value => MutateCurveElevation(item => item.Interval = Math.Max(0.01, value)),
                            decimalPlaces: 3,
                            help: "Distance along each source curve between elevation sample stations.",
                            minValue: 0.01));
                    });

                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves / Labels",
                        $"{summary.SampleSourceCount} curve(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Curve sources resolved and elevation annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatAnalysisValue(summary.SampleMinValue, curveElevation.ValueFormat)} / {FormatAnalysisValue(summary.SampleMaxValue, curveElevation.ValueFormat)}"
                            : "No samples",
                        "Terrain elevations sampled along the source curves during the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate curve elevation annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case CurveSlopeLabelAnalysisDefinition curveSlope:
            {
                void MutateCurveSlope(Action<CurveSlopeLabelAnalysisDefinition> apply)
                {
                    MutateAnalysis(
                        terrain.TerrainId,
                        curveSlope.Id,
                        item => apply((CurveSlopeLabelAnalysisDefinition)item),
                        scheduleRebuild: true);
                }

                AddBlockAttributeAnalysisRows(
                    layout,
                    terrain,
                    curveSlope,
                    RhinoObjectType.Curve,
                    MutateCurveSlope,
                    "Curve objects or layers projected to the terrain before grade is sampled.",
                    "Numeric format string applied to the sampled slope value, for example F1 or 0.0.",
                    extraRows: extraLayout =>
                    {
                        extraLayout.AddRow(CreateNumericEditor(
                            "Interval",
                            curveSlope.Interval,
                            value => MutateCurveSlope(item => item.Interval = Math.Max(0.01, value)),
                            decimalPlaces: 3,
                            help: "Distance along each source curve between sampled slope spans.",
                            minValue: 0.01));
                        extraLayout.AddRow(CreateSlopeUnitDropDown(
                            curveSlope.Unit,
                            unit => MutateCurveSlope(item => item.Unit = unit),
                            "Show terrain-projected curve slope labels as percent, promille, ratio, or degrees."));
                        extraLayout.AddRow(CreateCheckEditor(
                            "Flip Arrow",
                            curveSlope.FlipDirection,
                            value => MutateCurveSlope(item => item.FlipDirection = value),
                            "Rotate slope arrows 180 degrees to match alternate office conventions."));
                    });

                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves / Labels",
                        $"{summary.SampleSourceCount} curve(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Curve sources resolved and annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatSlopeValue(summary.SampleMinValue, curveSlope.Unit)} / {FormatSlopeValue(summary.SampleAverageValue, curveSlope.Unit)} / {FormatSlopeValue(summary.SampleMaxValue, curveSlope.Unit)}"
                            : "No samples",
                        "Terrain-projected curve slope values from the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate curve slope annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case ProjectedElevationLabelAnalysisDefinition projectedElevation:
            {
                void MutateProjectedElevation(Action<ProjectedElevationLabelAnalysisDefinition> apply)
                {
                    MutateAnalysis(
                        terrain.TerrainId,
                        projectedElevation.Id,
                        item => apply((ProjectedElevationLabelAnalysisDefinition)item),
                        scheduleRebuild: true);
                }

                AddBlockAttributeAnalysisRows(
                    layout,
                    terrain,
                    projectedElevation,
                    RhinoObjectType.Point | RhinoObjectType.Curve,
                    MutateProjectedElevation,
                    "Point objects and curve edit points projected to the terrain for elevation labels.",
                    "Numeric format string applied to projected elevation values, for example F2 or 0.00.");

                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Sources / Labels",
                        $"{summary.SampleSourceCount} source(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Point and curve sources resolved and annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatAnalysisValue(summary.SampleMinValue, projectedElevation.ValueFormat)} / {FormatAnalysisValue(summary.SampleMaxValue, projectedElevation.ValueFormat)}"
                            : "No samples",
                        "Projected terrain elevations from the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate projected elevation annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case PointSlopeLabelAnalysisDefinition pointSlope:
            {
                void MutatePointSlope(Action<PointSlopeLabelAnalysisDefinition> apply)
                {
                    MutateAnalysis(
                        terrain.TerrainId,
                        pointSlope.Id,
                        item => apply((PointSlopeLabelAnalysisDefinition)item),
                        scheduleRebuild: true);
                }

                AddBlockAttributeAnalysisRows(
                    layout,
                    terrain,
                    pointSlope,
                    RhinoObjectType.Point,
                    MutatePointSlope,
                    "Point objects or layers projected to the terrain before local slope is sampled.",
                    "Numeric format string applied to sampled terrain slope values, for example F1 or 0.0.",
                    extraRows: extraLayout =>
                    {
                        extraLayout.AddRow(CreateSlopeUnitDropDown(
                            pointSlope.Unit,
                            unit => MutatePointSlope(item => item.Unit = unit),
                            "Show terrain slope labels as percent, promille, ratio, or degrees."));
                        extraLayout.AddRow(CreateCheckEditor(
                            "Flip Arrow",
                            pointSlope.FlipDirection,
                            value => MutatePointSlope(item => item.FlipDirection = value),
                            "Rotate slope arrows 180 degrees to match alternate office conventions."));
                    });

                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Points / Labels",
                        $"{summary.SampleSourceCount} point(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Point sources resolved and annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatSlopeValue(summary.SampleMinValue, pointSlope.Unit)} / {FormatSlopeValue(summary.SampleAverageValue, pointSlope.Unit)} / {FormatSlopeValue(summary.SampleMaxValue, pointSlope.Unit)}"
                            : "No samples",
                        "Local terrain slope values sampled at the projected points."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate point slope annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case ContourAnalysisDefinition contour:
            {
                Guid capturedContourTerrainId = terrain.TerrainId;
                Guid capturedContourId = contour.Id;
                layout.AddRow(CreateNumericEditor(
                    "Interval",
                    contour.Interval,
                    value =>
                    {
                        MutateAnalysis(capturedContourTerrainId, capturedContourId, item => ((ContourAnalysisDefinition)item).Interval = Math.Max(0.01, value), scheduleRebuild: false);
                        var doc = RhinoDoc.ActiveDoc;
                        if (doc != null)
                            _controller.RebuildContourAnalysis(doc, capturedContourTerrainId, capturedContourId);
                    },
                    decimalPlaces: 3,
                    help: "Vertical spacing between generated contour levels.",
                    minValue: 0.01));
                layout.AddRow(CreateNumericEditor(
                    "Start Z",
                    contour.StartZ,
                    value =>
                    {
                        MutateAnalysis(capturedContourTerrainId, capturedContourId, item => ((ContourAnalysisDefinition)item).StartZ = value, scheduleRebuild: false);
                        var doc = RhinoDoc.ActiveDoc;
                        if (doc != null)
                            _controller.RebuildContourAnalysis(doc, capturedContourTerrainId, capturedContourId);
                    },
                    decimalPlaces: 3,
                    help: "Base elevation offset from which contour levels are stepped.",
                    minValue: null));
                layout.AddRow(CreateLayerAssignmentEditor(
                    "Output Layer",
                    contour.OutputLayerPath,
                    path =>
                    {
                        MutateAnalysis(capturedContourTerrainId, capturedContourId, item => ((ContourAnalysisDefinition)item).OutputLayerPath = path, scheduleRebuild: false);
                        var doc = RhinoDoc.ActiveDoc;
                        if (doc != null)
                            _controller.RebuildContourAnalysis(doc, capturedContourTerrainId, capturedContourId);
                    },
                    "Layer used for generated contour curves. Leave empty to use the terrain annotation layer."));
                string defaultColorText = string.IsNullOrWhiteSpace(contour.OutputLayerPath)
                    ? string.IsNullOrWhiteSpace(terrain.AnnotationLayerPath)
                        ? $"By Layer ({TerrainDefinition.DefaultAnnotationLayerPath})"
                        : $"By Layer ({GetLeafLayerName(terrain.AnnotationLayerPath!)})"
                    : $"By Layer ({GetLeafLayerName(contour.OutputLayerPath)})";
                layout.AddRow(CreateOptionalColorEditor(
                    "Color",
                    contour.ColorArgb,
                    value =>
                    {
                        MutateAnalysis(capturedContourTerrainId, capturedContourId, item => ((ContourAnalysisDefinition)item).ColorArgb = value, scheduleRebuild: false);
                        var doc = RhinoDoc.ActiveDoc;
                        if (doc != null)
                            _controller.RefreshContourColor(doc, capturedContourTerrainId, capturedContourId, value);
                    },
                    "Explicit display and bake color for generated contour curves. Clear to use the output layer color.",
                    ResolveLayerColorArgb(contour.OutputLayerPath ?? terrain.AnnotationLayerPath),
                    defaultColorText));
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves",
                        $"{summary.ContourCurveCount} curve(s) across {summary.ContourLevelCount} level(s)",
                        "Contour output generated from the last terrain build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Levels",
                        summary.ContourLevelCount > 0
                            ? $"{summary.ContourFirstLevel:G4} to {summary.ContourLastLevel:G4}"
                            : "No contour levels intersected the terrain",
                        "First and last contour elevations emitted by the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate contour curves.",
                        minHeight: 42));
                }
                break;
            }

            case TerrainSectionAnalysisDefinition terrainSection:
            {
                void MutateSection(Action<TerrainSectionAnalysisDefinition> apply) =>
                    MutateAnalysis(terrain.TerrainId, terrainSection.Id, item => apply((TerrainSectionAnalysisDefinition)item), scheduleRebuild: true);

                AddTerrainSectionCommonRows(layout, terrain, terrainSection,
                    "Curves used as cut lines through the terrain. Each curve produces one profile.");

                layout.AddRow(CreateNumericEditor(
                    "Station Tick Interval",
                    terrainSection.StationTickInterval,
                    value => MutateSection(item => item.StationTickInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Spacing between station tick marks along the profile baseline. 0 disables ticks.",
                    minValue: 0.0));
                layout.AddRow(CreateNumericEditor(
                    "Elevation Grid Interval",
                    terrainSection.ElevationGridInterval,
                    value => MutateSection(item => item.ElevationGridInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Vertical spacing of horizontal grid lines drawn on the section. 0 disables the grid.",
                    minValue: 0.0));
                layout.AddRow(CreateCheckEditor(
                    "Show Station Ticks",
                    terrainSection.ShowStationTicks,
                    value => MutateSection(item => item.ShowStationTicks = value),
                    "Draw tick marks at each station along the profile baseline."));
                layout.AddRow(CreateCheckEditor(
                    "Show Elevation Grid",
                    terrainSection.ShowElevationGrid,
                    value => MutateSection(item => item.ShowElevationGrid = value),
                    "Draw horizontal grid lines at each elevation increment."));
                layout.AddRow(CreateCheckEditor(
                    "Show Station Labels",
                    terrainSection.ShowStationLabels,
                    value => MutateSection(item => item.ShowStationLabels = value),
                    "Print station distance text below each tick."));

                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Cuts / Output",
                        $"{summary.SampleSourceCount} cut(s) -> {summary.GeneratedOutputCount} object(s)",
                        "Cut curves processed and section objects emitted by the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate the section profile.",
                        minHeight: 42));
                }
                break;
            }

            case CrossSectionStationAnalysisDefinition crossSection:
            {
                void MutateCrossSection(Action<CrossSectionStationAnalysisDefinition> apply) =>
                    MutateAnalysis(terrain.TerrainId, crossSection.Id, item => apply((CrossSectionStationAnalysisDefinition)item), scheduleRebuild: true);

                AddTerrainSectionCommonRows(layout, terrain, crossSection,
                    "Alignment curve sampled at regular stations. The first curve resolved is used.");

                layout.AddRow(CreateNumericEditor(
                    "Station Interval",
                    crossSection.StationInterval,
                    value => MutateCrossSection(item => item.StationInterval = Math.Max(0.01, value)),
                    decimalPlaces: 3,
                    help: "Distance between cross-section stations along the alignment.",
                    minValue: 0.01));
                layout.AddRow(CreateNumericEditor(
                    "Cross-Section Width",
                    crossSection.CrossSectionWidth,
                    value => MutateCrossSection(item => item.CrossSectionWidth = Math.Max(0.01, value)),
                    decimalPlaces: 3,
                    help: "Total perpendicular width of each cross-section cut, centered on the alignment.",
                    minValue: 0.01));
                layout.AddRow(CreateNumericEditor(
                    "Vertical Exaggeration",
                    crossSection.VerticalExaggeration,
                    value => MutateCrossSection(item => item.VerticalExaggeration = Math.Max(0.1, value)),
                    decimalPlaces: 3,
                    help: "Vertical scale factor applied to the unrolled cross-section profiles. 1.0 = true scale.",
                    minValue: 0.1));
                layout.AddRow(CreateNumericEditor(
                    "Grid Columns",
                    crossSection.GridColumns,
                    value => MutateCrossSection(item => item.GridColumns = Math.Max(1, (int)Math.Round(value))),
                    decimalPlaces: 0,
                    help: "Number of columns in the unrolled cross-section grid layout.",
                    minValue: 1));
                layout.AddRow(CreateNumericEditor(
                    "Grid Cell Width",
                    crossSection.GridCellWidth,
                    value => MutateCrossSection(item => item.GridCellWidth = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Override cell width for the grid layout. 0 = auto.",
                    minValue: 0.0));
                layout.AddRow(CreateNumericEditor(
                    "Grid Cell Height",
                    crossSection.GridCellHeight,
                    value => MutateCrossSection(item => item.GridCellHeight = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Override cell height for the grid layout. 0 = auto.",
                    minValue: 0.0));
                layout.AddRow(CreateCheckEditor(
                    "Cut Lines on Terrain",
                    crossSection.ShowCutLinesOnTerrain,
                    value => MutateCrossSection(item => item.ShowCutLinesOnTerrain = value),
                    "Draw the perpendicular cut polylines on the terrain at each station."));
                layout.AddRow(CreateCheckEditor(
                    "Label Stations",
                    crossSection.LabelStations,
                    value => MutateCrossSection(item => item.LabelStations = value),
                    "Print station distance text on each unrolled cross-section."));
                layout.AddRow(CreateCheckEditor(
                    "Show Elevation Grid",
                    crossSection.ShowElevationGrid,
                    value => MutateCrossSection(item => item.ShowElevationGrid = value),
                    "Draw horizontal grid lines on each unrolled cross-section."));
                layout.AddRow(CreateNumericEditor(
                    "Elevation Grid Interval",
                    crossSection.ElevationGridInterval,
                    value => MutateCrossSection(item => item.ElevationGridInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Vertical spacing of grid lines on the unrolled cross-sections. 0 disables.",
                    minValue: 0.0));

                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Alignments / Output",
                        $"{summary.SampleSourceCount} alignment(s) -> {summary.GeneratedOutputCount} object(s)",
                        "Alignment curves processed and cross-section objects emitted by the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate cross-sections.",
                        minHeight: 42));
                }
                break;
            }

            case LongitudinalSectionAnalysisDefinition longitudinal:
            {
                void MutateLongitudinal(Action<LongitudinalSectionAnalysisDefinition> apply) =>
                    MutateAnalysis(terrain.TerrainId, longitudinal.Id, item => apply((LongitudinalSectionAnalysisDefinition)item), scheduleRebuild: true);

                AddTerrainSectionCommonRows(layout, terrain, longitudinal,
                    "Curve sampled along its length. Terrain elevation is read at each sample.");

                layout.AddRow(CreateNumericEditor(
                    "Sample Interval",
                    longitudinal.SampleInterval,
                    value => MutateLongitudinal(item => item.SampleInterval = Math.Max(0.01, value)),
                    decimalPlaces: 3,
                    help: "Distance between elevation samples along the curve.",
                    minValue: 0.01));
                layout.AddRow(CreateNumericEditor(
                    "Vertical Exaggeration",
                    longitudinal.VerticalExaggeration,
                    value => MutateLongitudinal(item => item.VerticalExaggeration = Math.Max(0.1, value)),
                    decimalPlaces: 3,
                    help: "Vertical scale factor applied to the unrolled profile. 1.0 = true scale.",
                    minValue: 0.1));
                layout.AddRow(CreateCheckEditor(
                    "Show Baseline",
                    longitudinal.ShowBaseline,
                    value => MutateLongitudinal(item => item.ShowBaseline = value),
                    "Draw the horizontal baseline (zero elevation reference) under the profile."));
                layout.AddRow(CreateCheckEditor(
                    "Show Elevation Grid",
                    longitudinal.ShowElevationGrid,
                    value => MutateLongitudinal(item => item.ShowElevationGrid = value),
                    "Draw horizontal grid lines at each elevation increment."));
                layout.AddRow(CreateNumericEditor(
                    "Elevation Grid Interval",
                    longitudinal.ElevationGridInterval,
                    value => MutateLongitudinal(item => item.ElevationGridInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Vertical spacing of horizontal grid lines. 0 disables.",
                    minValue: 0.0));
                layout.AddRow(CreateCheckEditor(
                    "Show Station Labels",
                    longitudinal.ShowStationLabels,
                    value => MutateLongitudinal(item => item.ShowStationLabels = value),
                    "Print station distance text along the baseline."));
                layout.AddRow(CreateNumericEditor(
                    "Station Label Interval",
                    longitudinal.StationLabelInterval,
                    value => MutateLongitudinal(item => item.StationLabelInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Spacing between station labels. 0 = auto (~quarter of total length).",
                    minValue: 0.0));

                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves / Output",
                        $"{summary.SampleSourceCount} curve(s) -> {summary.GeneratedOutputCount} object(s)",
                        "Curves processed and longitudinal section objects emitted by the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate the longitudinal section.",
                        minHeight: 42));
                }
                break;
            }
        }

        return layout;
    }

    private void AddTerrainSectionCommonRows(
        DynamicLayout layout,
        TerrainDefinition terrain,
        TerrainSectionAnalysisDefinitionBase analysis,
        string sourceHelp)
    {
        void MutateSection(Action<TerrainSectionAnalysisDefinitionBase> apply) =>
            MutateAnalysis(terrain.TerrainId, analysis.Id, item =>
            {
                if (item is TerrainSectionAnalysisDefinitionBase section)
                    apply(section);
            }, scheduleRebuild: true);

        layout.AddRow(CreateSourceEditor(
            "Sources",
            analysis.Sources,
            apply => MutateSection(item => apply(item.Sources)),
            RhinoObjectType.Curve,
            doc => _controller.GetSelectedLayerPaths(doc),
            sourceHelp));
        layout.AddRow(CreateInsertionOriginEditor(terrain, analysis));
        layout.AddRow(CreateNumericEditor(
            "Text Height",
            analysis.TextHeight,
            value => MutateSection(item => item.TextHeight = Math.Max(0.01, value)),
            decimalPlaces: 3,
            help: "Height of station and elevation labels printed on the section.",
            minValue: 0.01));
        layout.AddRow(CreateLayerAssignmentEditor(
            "Output Layer",
            analysis.OutputLayerPath,
            path => MutateSection(item => item.OutputLayerPath = path),
            "Layer used for generated section geometry. Leave empty to use the terrain annotation layer."));
        layout.AddRow(CreateOptionalColorEditor(
            "Color",
            analysis.ColorArgb,
            value => MutateSection(item => item.ColorArgb = value),
            "Display and bake color for generated section geometry. Clear to use the output layer color.",
            ResolveLayerColorArgb(analysis.OutputLayerPath ?? terrain.AnnotationLayerPath),
            GetAnalysisOutputColorText(terrain, analysis.OutputLayerPath)));
    }

    private Control CreateInsertionOriginEditor(
        TerrainDefinition terrain,
        TerrainSectionAnalysisDefinitionBase analysis)
    {
        string text = analysis.HasInsertionPlane
            ? $"({analysis.InsertionOriginX:F2}, {analysis.InsertionOriginY:F2}, {analysis.InsertionOriginZ:F2})"
            : "(auto: offset from terrain bbox)";

        var summary = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = analysis.HasInsertionPlane ? UiTheme.PrimaryText : UiTheme.MutedText,
            Wrap = WrapMode.None
        };
        ApplyHelp(summary, "Insertion origin where the laid-out section is placed. World X/Z axes are used for direction.");

        var pickButton = MakeMiniButton("Pick", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            var gp = new RhinoGetPoint();
            gp.SetCommandPrompt("Pick section insertion origin");
            if (gp.Get() != RhinoGetResult.Point)
                return;

            RhinoPoint3d picked = gp.Point();
            MutateAnalysis(terrain.TerrainId, analysis.Id, item =>
            {
                if (item is TerrainSectionAnalysisDefinitionBase section)
                {
                    section.InsertionOriginX = picked.X;
                    section.InsertionOriginY = picked.Y;
                    section.InsertionOriginZ = picked.Z;
                    section.InsertionXAxisX = 1.0;
                    section.InsertionXAxisY = 0.0;
                    section.InsertionXAxisZ = 0.0;
                    section.InsertionYAxisX = 0.0;
                    section.InsertionYAxisY = 1.0;
                    section.InsertionYAxisZ = 0.0;
                    section.HasInsertionPlane = true;
                }
            }, scheduleRebuild: true);
            RefreshUi();
        }, "Pick the origin point where the laid-out section will be placed.", width: 46);

        var resetButton = MakeMiniButton("Auto", (_, _) =>
        {
            MutateAnalysis(terrain.TerrainId, analysis.Id, item =>
            {
                if (item is TerrainSectionAnalysisDefinitionBase section)
                    section.HasInsertionPlane = false;
            }, scheduleRebuild: true);
            RefreshUi();
        }, "Clear the insertion origin and let the section auto-position next to the terrain.", width: 46);

        var fields = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new StackLayoutItem(summary, expand: true),
                pickButton,
                resetButton
            }
        };

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Insertion", "Insertion origin where the laid-out section is placed.", 0),
                    fields
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel("Insertion", "Insertion origin where the laid-out section is placed.", NumericLabelWidth),
                new StackLayoutItem(fields, expand: true)
            }
        };
    }

    private Control CreateAnalysisPaletteEditor(Guid terrainId, AnalysisDefinition analysis, string help)
    {
        var paletteOptions = SlopePreviewPaletteCatalog.All
            .Select(item => (item.Key, item.Label))
            .ToList();
        return CreateDropDownEditor(
            "Palette",
            paletteOptions,
            analysis.PalettePreset,
            value => MutateAndRefreshAnalysis(terrainId, analysis.Id, item => item.PalettePreset = value),
            help);
    }

    private Control CreateAnalysisRangeEditor(string label, double value, Action<double> onChanged, string help)
    {
        return CreateNumericEditor(label, value, onChanged, decimalPlaces: 2, help: help);
    }

    private Panel CreateModifierCard(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        bool collapsed = _collapsedModifiers.Contains(modifier.Id);
        var collapseLabel = new Label
        {
            Text = collapsed ? "▶" : "▼",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 14
        };

        var enabledCheck = new CheckBox { Checked = modifier.IsEnabled };
        enabledCheck.CheckedChanged += (_, _) =>
            MutateModifier(terrain.TerrainId, modifier.Id, item => item.IsEnabled = enabledCheck.Checked == true);

        var capturedModifierId = modifier.Id;
        var capturedTerrainId = terrain.TerrainId;
        string kind = GetModifierKind(modifier);
        string typeLabel = GetModifierTypeLabel(modifier);
        bool isPinnedBaseTriangulate = modifier is TriangulateModifierDefinition &&
                                       terrain.Modifiers.Count > 0 &&
                                       terrain.Modifiers[0].Id == modifier.Id;

        var nameBox = new TextBox { Text = modifier.Label };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Modifier label. Press Enter or click away to rename.");
        BindCommittedText(nameBox, () => modifier.Label, text =>
            MutateModifier(capturedTerrainId, capturedModifierId, item => item.Label = text, scheduleRebuild: false));

        var handle = CreateDragHandle();
        if (isPinnedBaseTriangulate)
        {
            handle.Enabled = false;
            ApplyHelp(handle, "The base triangulate modifier stays at the bottom of the stack.");
        }
        else
        {
            ApplyHelp(handle, "Drag to reorder this modifier.");
            handle.MouseDown += (_, e) =>
            {
                if (e.Buttons != MouseButtons.Primary) return;
                var data = new DataObject();
                data.SetString(capturedModifierId.ToString(), "modifier-drag");
                handle.DoDragDrop(data, DragEffects.Move);
            };
        }

        var iconImage = PanelIcons.Load(GetModifierIconName(kind));
        Control iconControl = iconImage != null
            ? new ImageView { Image = iconImage, Size = new Size(16, 16) }
            : new Label { Text = typeLabel[..1], VerticalAlignment = VerticalAlignment.Center };
        var accent = ModifierTypeColor(kind);
        var iconPlate = CreateIconPlate(accent, iconControl);

        Control titleBlock;
        if (collapsed)
        {
            titleBlock = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                Items =
                {
                    new Label
                    {
                        Text = modifier.Label,
                        Font = new Font(SystemFont.Bold),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    CreateCardMetaLabel(GetCollapsedSummary(modifier))
                }
            };
        }
        else
        {
            titleBlock = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                Items =
                {
                    nameBox,
                    CreateCardMetaLabel(GetModifierSubtitle(modifier, isPinnedBaseTriangulate))
                }
            };
        }

        Control[] statusControls = isPinnedBaseTriangulate
            ? new Control[]
            {
                CreateCardStatusLabel(typeLabel),
                CreateCardStatusLabel("Pinned", accent)
            }
            : new Control[]
            {
                CreateCardStatusLabel(typeLabel)
            };

        Control[] actionControls = Array.Empty<Control>();
        if (!isPinnedBaseTriangulate)
        {
            var copyButton = MakeMiniButton("Copy", (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                    _controller.DuplicateModifier(doc, capturedTerrainId, capturedModifierId);
            }, "Duplicate this modifier.", width: 46);
            var deleteButton = MakeMiniButton("Del", (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                    _controller.RemoveModifier(doc, capturedTerrainId, capturedModifierId);
            }, "Delete this modifier.", width: 38);
            actionControls = new Control[] { copyButton, deleteButton };
        }

        void ToggleCollapsed(bool ctrlHeld)
        {
            bool nowCollapsed = !_collapsedModifiers.Contains(capturedModifierId);
            if (ctrlHeld)
            {
                var doc2 = RhinoDoc.ActiveDoc;
                var t = doc2 == null ? null : _controller.GetSelectedTerrain(doc2);
                if (t != null)
                {
                    if (nowCollapsed)
                        foreach (var m in t.Modifiers) _collapsedModifiers.Add(m.Id);
                    else
                        _collapsedModifiers.Clear();
                }
            }
            else
            {
                if (nowCollapsed)
                    _collapsedModifiers.Add(capturedModifierId);
                else
                    _collapsedModifiers.Remove(capturedModifierId);
            }

            var doc = RhinoDoc.ActiveDoc;
            RebuildModifierLayout(doc == null ? null : _controller.GetSelectedTerrain(doc));
        }

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = statusControls,
            ActionControls = actionControls,
            ToggleCollapsed = ToggleCollapsed,
            Collapsed = collapsed,
            Body = collapsed ? null : CreateModifierBody(terrain, modifier),
            HeaderBackground = isPinnedBaseTriangulate ? UiTheme.BaseCardBackground : UiTheme.HeaderBackground,
            CardBackground = isPinnedBaseTriangulate ? UiTheme.BaseCardBackground : UiTheme.CardBackground
        });
    }

    private Control CreateModifierBody(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 6), Padding = new Padding(10, 8, 10, 8) };

        switch (modifier)
        {
            case TriangulateModifierDefinition triangulate:
                layout.AddRow(CreateSourceEditor("Points", triangulate.Points,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((TriangulateModifierDefinition)item).Points)),
                    RhinoObjectType.Point | RhinoObjectType.PointSet,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Breaklines", triangulate.Breaklines,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((TriangulateModifierDefinition)item).Breaklines)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Contours", triangulate.Contours,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((TriangulateModifierDefinition)item).Contours)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Boundary", triangulate.Boundary,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((TriangulateModifierDefinition)item).Boundary)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                AddBoundaryPeelEditors(layout, terrain, triangulate);
                break;
            case AddGeometryModifierDefinition addGeometry:
                layout.AddRow(CreateSourceEditor("Points", addGeometry.Points,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((AddGeometryModifierDefinition)item).Points)),
                    RhinoObjectType.Point | RhinoObjectType.PointSet,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Breaklines", addGeometry.Breaklines,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((AddGeometryModifierDefinition)item).Breaklines)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Contours", addGeometry.Contours,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((AddGeometryModifierDefinition)item).Contours)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Boundary", addGeometry.Boundary,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((AddGeometryModifierDefinition)item).Boundary)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                AddBoundaryPeelEditors(layout, terrain, addGeometry);
                break;
            case RemeshModifierDefinition remesh:
                layout.AddRow(CreateSourceEditor("Constraints", remesh.Constraints,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((RemeshModifierDefinition)item).Constraints)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateNumericEditor("Edge Length", remesh.EdgeLength, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((RemeshModifierDefinition)item).EdgeLength = value),
                    help: "Target triangle edge length. Smaller values make denser meshes; larger values make coarser meshes. Leave at 0 to let Max Area drive remeshing."));
                layout.AddRow(CreateNumericEditor("Max Area", remesh.MaxArea, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((RemeshModifierDefinition)item).MaxArea = value),
                    help: "Maximum triangle area. Smaller values create finer remeshes; large values keep larger faces. Leave at 0 to disable this limit."));
                layout.AddRow(CreateNumericEditor("Min Angle", remesh.MinAngle, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((RemeshModifierDefinition)item).MinAngle = value),
                    help: "Minimum triangle angle in degrees. Around 20-30 is moderate quality; pushing high can overconstrain or fail on awkward meshes."));
                break;
            case SmoothModifierDefinition smooth:
                layout.AddRow(CreateSourceEditor("Boundaries", smooth.Boundaries,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((SmoothModifierDefinition)item).Boundaries)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Breaklines", smooth.Breaklines,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((SmoothModifierDefinition)item).Breaklines)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSliderNumericEditor("Iterations", smooth.Iterations, value =>
                    MutateModifier(
                        terrain.TerrainId,
                        modifier.Id,
                        item => ((SmoothModifierDefinition)item).Iterations = (int)Math.Round(value),
                        deferDocumentSave: true,
                        suppressImmediateUiRefresh: true),
                    softMin: 0.0,
                    softMax: 12.0,
                    decimalPlaces: 0,
                    hardMin: 0.0,
                    help: "How many Z-only smoothing passes to run. Scrub for quick changes, or type larger values directly when you need more than the slider's soft range."));
                layout.AddRow(CreateSliderNumericEditor("Strength", smooth.Strength, value =>
                    MutateModifier(
                        terrain.TerrainId,
                        modifier.Id,
                        item => ((SmoothModifierDefinition)item).Strength = value,
                        deferDocumentSave: true,
                        suppressImmediateUiRefresh: true),
                    softMin: 0.0,
                    softMax: 1.0,
                    decimalPlaces: 3,
                    hardMin: 0.0,
                    hardMax: 1.0,
                    help: "How strongly each pass moves vertex Z. Scrub within the usual 0-1 range, or type a value directly if you need something unusual."));
                layout.AddRow(CreateSliderNumericEditor("Fixity", smooth.BreaklineFixity, value =>
                    MutateModifier(
                        terrain.TerrainId,
                        modifier.Id,
                        item => ((SmoothModifierDefinition)item).BreaklineFixity = value,
                        deferDocumentSave: true,
                        suppressImmediateUiRefresh: true),
                    softMin: 0.0,
                    softMax: 1.0,
                    decimalPlaces: 3,
                    hardMin: 0.0,
                    hardMax: 1.0,
                    help: "How strongly breaklines resist smoothing. Scrub in the common range, or type a precise value directly."));
                break;
            case RetainingWallModifierDefinition walls:
                layout.AddRow(CreateSourceEditor("Wall Curves", walls.WallCurves,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((RetainingWallModifierDefinition)item).WallCurves)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateLayerAssignmentEditor(
                    "Wall Layer",
                    walls.OutputLayerPath,
                    path => MutateModifier(terrain.TerrainId, modifier.Id, item => ((RetainingWallModifierDefinition)item).OutputLayerPath = path),
                    "Layer used for retaining-wall Breps. Leave empty to use the terrain auxiliary layer."));
                layout.AddRow(CreateNumericEditor("Max Wall Width", walls.MaxWallWidth, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((RetainingWallModifierDefinition)item).MaxWallWidth = value),
                    help: "Maximum expected spacing between paired wall rails. Wall cleanup uses an automatic internal tolerance derived from wall width and terrain detail size."));
                break;
            case GradePadModifierDefinition gradePad:
                layout.AddRow(CreateSourceEditor("Boundaries", gradePad.Boundaries,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((GradePadModifierDefinition)item).Boundaries)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Lock Curves", gradePad.LockCurves,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((GradePadModifierDefinition)item).LockCurves)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateNumericEditor("Slope Angle", gradePad.SlopeAngle, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((GradePadModifierDefinition)item).SlopeAngle = value),
                    help: "Daylight tie-in slope in degrees. Boundary curve Z defines the finished pad plane; lower values are flatter and extend farther, while higher values are steeper and tighter."));
                layout.AddRow(CreateNumericEditor("Max Distance", gradePad.MaxDistance, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((GradePadModifierDefinition)item).MaxDistance = value),
                    help: "Maximum grading reach. 0 means unlimited; smaller values keep the effect close to the pad."));
                break;
            case GradePathModifierDefinition gradePath:
                layout.AddRow(CreateSourceEditor("Paths", gradePath.Paths,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((GradePathModifierDefinition)item).Paths)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    help: "Path curves accept Rhino object picks and layers. Curve Z defines the finished road elevation profile."));
                layout.AddRow(CreateNumericEditor("Width", gradePath.Width, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((GradePathModifierDefinition)item).Width = value),
                    help: "Finished path width. This is the flat or controlled-width core before side grading starts."));
                layout.AddRow(CreateNumericEditor("Slope Angle", gradePath.SlopeAngle, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((GradePathModifierDefinition)item).SlopeAngle = value),
                    help: "Side slope angle in degrees. Lower values spread the path farther; higher values make sharper shoulders."));
                layout.AddRow(CreateNumericEditor("Max Distance", gradePath.MaxDistance, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((GradePathModifierDefinition)item).MaxDistance = value),
                    help: "Maximum grading reach away from the path. 0 means unlimited; lower values constrain the shoulder length."));
                break;
            case InSituStairModifierDefinition inSituStair:
                layout.AddRow(CreateSourceEditor("Reference", inSituStair.ReferenceSurface,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((InSituStairModifierDefinition)item).ReferenceSurface)),
                    RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateNumericEditor("Riser Height", inSituStair.RiserHeight, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((InSituStairModifierDefinition)item).RiserHeight = value),
                    help: "Vertical rise per step. The stair modifier derives tread depth from the supplied walkable surface and this riser height."));
                layout.AddRow(CreateNumericEditor("Min Tread Depth", inSituStair.MinTreadDepth, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((InSituStairModifierDefinition)item).MinTreadDepth = value),
                    help: "Minimum acceptable derived tread depth. Values greater than 0 color undersized stair solids bright red; 0 disables the warning."));
                layout.AddRow(CreateNumericEditor("Slope Angle", inSituStair.SlopeAngle, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((InSituStairModifierDefinition)item).SlopeAngle = value),
                    help: "Daylight slope angle where the graded support surface blends back into surrounding terrain."));
                layout.AddRow(CreateNumericEditor("Max Distance", inSituStair.MaxDistance, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((InSituStairModifierDefinition)item).MaxDistance = value),
                    help: "Maximum grading reach away from the stair footprint. 0 means unlimited."));
                layout.AddRow(CreateCheckEditor(
                    "Show Tread Labels",
                    inSituStair.ShowTreadLabels,
                    value => MutateModifier(terrain.TerrainId, modifier.Id, item => ((InSituStairModifierDefinition)item).ShowTreadLabels = value),
                    "Show one viewport tread-depth label per interpreted stair surface."));
                layout.AddRow(CreateReadOnlyValueRow(
                    "Tread Depth",
                    inSituStair.ComputedTreadDepthSummary ?? "(build to compute)",
                    "Derived horizontal tread depth summary across the interpreted stair surfaces."));
                layout.AddRow(CreateReadOnlyValueRow(
                    "Step Count",
                    inSituStair.ComputedStepCountSummary ?? "(build to compute)",
                    "Generated tread-count summary across the interpreted stair surfaces."));
                break;
        }

        return layout;
    }

    private void AddBoundaryPeelEditors(DynamicLayout layout, TerrainDefinition terrain, GeometryInputModifierDefinition modifier)
    {
        layout.AddRow(CreateCheckEditor(
            "Peel Border",
            modifier.PeelBoundaryTriangles,
            value => MutateModifier(
                terrain.TerrainId,
                modifier.Id,
                item => ((GeometryInputModifierDefinition)item).PeelBoundaryTriangles = value),
            "Remove unwanted triangles only from the current TIN boundary. Interior faces are not candidates."));
        layout.AddRow(CreateNumericEditor(
            "Max Edge",
            modifier.MaxBoundaryEdgeLength,
            value => MutateModifier(
                terrain.TerrainId,
                modifier.Id,
                item => ((GeometryInputModifierDefinition)item).MaxBoundaryEdgeLength = value),
            help: "Boundary peeling edge threshold. 0 chooses an automatic threshold from mesh edge lengths.",
            minValue: 0));
        layout.AddRow(CreateNumericEditor(
            "Max Angle",
            modifier.MaxBoundaryAngleDegrees,
            value => MutateModifier(
                terrain.TerrainId,
                modifier.Id,
                item => ((GeometryInputModifierDefinition)item).MaxBoundaryAngleDegrees = value),
            help: "Boundary triangles with a longer-than-threshold edge and an interior angle at or above this value are peeled.",
            minValue: 0,
            maxValue: 180));
        layout.AddRow(CreateNumericEditor(
            "Slope Limit",
            modifier.MaxBoundarySlopeDegrees,
            value => MutateModifier(
                terrain.TerrainId,
                modifier.Id,
                item => ((GeometryInputModifierDefinition)item).MaxBoundarySlopeDegrees = value),
            help: "Boundary triangles with slope at or above this angle are peeled. 0 disables slope-based peeling.",
            minValue: 0,
            maxValue: 90));
    }

    private Panel CreateZoneCard(TerrainDefinition terrain, CollageZoneDefinition zone)
    {
        bool collapsed = _collapsedZones.Contains(zone.ZoneId);
        var collapseLabel = new Label
        {
            Text = collapsed ? "\u25B6" : "\u25BC",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 14
        };

        var nameBox = new TextBox { Text = zone.Name };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Friendly zone name shown in the panel and on generated output objects.");
        BindCommittedText(nameBox, () => zone.Name, text =>
            MutateZone(terrain.TerrainId, zone.ZoneId, item => item.Name = text, scheduleRebuild: false));

        var enabledCheck = new CheckBox { Checked = zone.IsEnabled };
        ApplyHelp(enabledCheck, "Disable a zone without deleting it.");
        enabledCheck.CheckedChanged += (_, _) =>
            MutateZone(terrain.TerrainId, zone.ZoneId, item => item.IsEnabled = enabledCheck.Checked == true);

        var capturedZoneId = zone.ZoneId;
        var capturedTerrainId = terrain.TerrainId;

        var handle = CreateDragHandle();
        ApplyHelp(handle, "Drag to reorder this zone. Later zones win when priorities tie.");
        handle.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;

            var data = new DataObject();
            data.SetString(capturedZoneId.ToString(), "zone-drag");
            handle.DoDragDrop(data, DragEffects.Move);
        };

        var accent = UiTheme.ZoneStripColor;
        var iconPlate = CreateIconPlate(accent, new Label
        {
            Text = "ZN",
            VerticalAlignment = VerticalAlignment.Center
        });

        Control titleBlock = collapsed
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                Items =
                {
                    new Label
                    {
                        Text = zone.Name,
                        Font = new Font(SystemFont.Bold),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    CreateCardMetaLabel(GetZoneCollapsedSummary(zone))
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                Items =
                {
                    nameBox,
                    CreateCardMetaLabel(zone.UseInputElevationForPriority
                        ? "Higher inputs win overlaps"
                        : "Later zones win ties")
                }
            };

        var badge = CreateCardStatusLabel(zone.IsEnabled ? "Enabled" : "Disabled");

        var deleteButton = MakeMiniButton("Del", (_, _) => RemoveZone(capturedTerrainId, capturedZoneId), "Delete this zone.", width: 38);
        void ToggleCollapsed(bool ctrlHeld)
        {
            bool nowCollapsed = !_collapsedZones.Contains(capturedZoneId);
            if (ctrlHeld)
            {
                var doc2 = RhinoDoc.ActiveDoc;
                var t = doc2 == null ? null : _controller.GetSelectedTerrain(doc2);
                if (t != null)
                {
                    if (nowCollapsed)
                        foreach (var item in t.Zones) _collapsedZones.Add(item.ZoneId);
                    else
                        _collapsedZones.Clear();
                }
            }
            else
            {
                if (nowCollapsed)
                    _collapsedZones.Add(capturedZoneId);
                else
                    _collapsedZones.Remove(capturedZoneId);
            }

            var doc = RhinoDoc.ActiveDoc;
            RebuildZonesLayout(doc == null ? null : _controller.GetSelectedTerrain(doc));
        }

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = new Control[]
            {
                CreateCardStatusLabel("Zone"),
                badge
            },
            ActionControls = new Control[]
            {
                deleteButton
            },
            ToggleCollapsed = ToggleCollapsed,
            Collapsed = collapsed,
            Body = collapsed ? null : CreateZoneBody(terrain, zone)
        });
    }

    private Control CreateZoneBody(TerrainDefinition terrain, CollageZoneDefinition zone)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 6), Padding = new Padding(10, 8, 10, 8) };
        var useInputElevationCheck = new CheckBox
        {
            Text = "Priority by elevation",
            Checked = zone.UseInputElevationForPriority
        };
        ApplyHelp(useInputElevationCheck, "When enabled, zones with higher source geometry win where two zones overlap. Useful when compositing objects at different elevations (e.g., a raised platform on flat terrain).");
        useInputElevationCheck.CheckedChanged += (_, _) =>
            MutateZone(terrain.TerrainId, zone.ZoneId, item => item.UseInputElevationForPriority = useInputElevationCheck.Checked == true);

        layout.AddRow(CreateSourceEditor(
            "Zone Area",
            zone.Boundaries,
            apply => MutateZone(terrain.TerrainId, zone.ZoneId, z => apply(z.Boundaries)),
            RhinoObjectType.Curve,
            doc => _controller.GetSelectedLayerPaths(doc),
            "Assign the curves and layers that define this zone's area."));
        layout.AddSeparateRow(useInputElevationCheck, null);
        return layout;
    }

    private Control BuildMarkerAddButtons(TerrainDefinition terrain)
    {
        var elevationButton = MakeButton("+ Elevation", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.AddMarker(doc, terrain.TerrainId, "elevation");
        }, "Add elevation markers. By default these place an editable block symbol plus a value label.");

        var slopeButton = MakeButton("+ Slope", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.AddMarker(doc, terrain.TerrainId, "slope");
        }, "Add slope markers. By default these place an editable block symbol plus a slope value label.");

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Padding(8, 8, 8, 4),
            Items =
            {
                new Label { Text = "MARKERS", TextColor = UiTheme.MutedText },
                UseStackedFormRows()
                    ? new StackLayout
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 4,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Items =
                        {
                            elevationButton,
                            slopeButton
                        }
                    }
                    : new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        Items =
                        {
                            elevationButton,
                            slopeButton
                        }
                    }
            }
        };
    }

    private Control BuildObjectAddButtons(TerrainDefinition terrain)
    {
        var addButton = MakeToolbarButton("Add Object", (_, _) => { }, "Add a terrain object definition.", width: 100);
        var menu = new ContextMenu();

        var projectItem = new ButtonMenuItem
        {
            Text = "Plant"
        };
        projectItem.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.AddObjectDefinition(doc, terrain.TerrainId, "lowest-point");
        };
        menu.Items.Add(projectItem);

        var surfaceItem = new ButtonMenuItem
        {
            Text = "Orient"
        };
        surfaceItem.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.AddObjectDefinition(doc, terrain.TerrainId, "surface-oriented");
        };
        menu.Items.Add(surfaceItem);
        addButton.Click += (_, _) => menu.Show(addButton);

        return CreateSectionToolbar(
            "TERRAIN OBJECTS",
            addButton);
    }

    private Panel CreateObjectCard(TerrainDefinition terrain, TerrainObjectDefinition definition)
    {
        bool collapsed = _collapsedObjects.Contains(definition.Id);
        var collapseLabel = new Label
        {
            Text = collapsed ? "\u25B6" : "\u25BC",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 14
        };

        var enabledCheck = new CheckBox { Checked = definition.IsEnabled };
        enabledCheck.CheckedChanged += (_, _) =>
            MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.IsEnabled = enabledCheck.Checked == true);

        var capturedDefinitionId = definition.Id;
        var capturedTerrainId = terrain.TerrainId;
        string kind = GetTerrainObjectKind(definition);
        string typeLabel = GetTerrainObjectTypeLabel(definition);

        var nameBox = new TextBox { Text = definition.Name };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Object definition label. Press Enter or click away to rename.");
        BindCommittedText(nameBox, () => definition.Name, text =>
            MutateObjectDefinition(capturedTerrainId, capturedDefinitionId, item => item.Name = text, scheduleRebuild: false));

        var handle = CreateDragHandle();
        handle.Enabled = false;
        handle.Cursor = Cursors.Default;
        ApplyHelp(handle, "Object definition cards use the modifier card layout. Reordering is not enabled yet.");

        var accent = TerrainObjectTypeColor(kind);
        var iconPlate = CreateIconPlate(accent, new Label
        {
            Text = GetTerrainObjectIconLabel(definition),
            VerticalAlignment = VerticalAlignment.Center
        });

        Control titleBlock = collapsed
            ? new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                Items =
                {
                    new Label
                    {
                        Text = definition.Name,
                        Font = new Font(SystemFont.Bold),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    CreateCardMetaLabel(GetTerrainObjectCollapsedSummary(definition))
                }
            }
            : new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                Items =
                {
                    nameBox,
                    CreateCardMetaLabel(GetTerrainObjectSubtitle(definition))
                }
            };

        var copyButton = MakeMiniButton("Copy", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.DuplicateObjectDefinition(doc, capturedTerrainId, capturedDefinitionId);
        }, "Duplicate this object definition.", width: 46);
        var deleteButton = MakeMiniButton("Del", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.RemoveObjectDefinition(doc, capturedTerrainId, capturedDefinitionId);
        }, "Delete this object definition and restore any currently placed objects.", width: 38);

        void ToggleCollapsed(bool ctrlHeld)
        {
            bool nowCollapsed = !_collapsedObjects.Contains(capturedDefinitionId);
            if (ctrlHeld)
            {
                var doc2 = RhinoDoc.ActiveDoc;
                var t = doc2 == null ? null : _controller.GetSelectedTerrain(doc2);
                if (t != null)
                {
                    if (nowCollapsed)
                        foreach (var item in t.Objects) _collapsedObjects.Add(item.Id);
                    else
                        _collapsedObjects.Clear();
                }
            }
            else
            {
                if (nowCollapsed)
                    _collapsedObjects.Add(capturedDefinitionId);
                else
                    _collapsedObjects.Remove(capturedDefinitionId);
            }

            var doc = RhinoDoc.ActiveDoc;
            RebuildObjectsLayout(doc == null ? null : _controller.GetSelectedTerrain(doc));
        }

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = new Control[]
            {
                CreateCardStatusLabel(typeLabel)
            },
            ActionControls = new Control[]
            {
                copyButton,
                deleteButton
            },
            ToggleCollapsed = ToggleCollapsed,
            Collapsed = collapsed,
            Body = collapsed ? null : CreateObjectBody(terrain, definition)
        });
    }

    private Control CreateObjectBody(TerrainDefinition terrain, TerrainObjectDefinition definition)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 6), Padding = new Padding(10, 8, 10, 8) };
        layout.AddRow(CreateSourceEditor("Sources", definition.Sources,
            apply => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => apply(item.Sources)),
            0,
            doc => _controller.GetSelectedLayerPaths(doc)));
        layout.AddRow(CreateSliderNumericEditor(
            "Rotate Min",
            definition.RandomRotationMinDegrees,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomRotationMinDegrees = value),
            softMin: 0.0,
            softMax: 360.0,
            decimalPlaces: 1,
            hardMin: 0.0,
            hardMax: 360.0,
            help: "Minimum random rotation in degrees. Rotation is applied per object around its placement up axis and stays stable between rebuilds."));
        layout.AddRow(CreateSliderNumericEditor(
            "Rotate Max",
            definition.RandomRotationMaxDegrees,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomRotationMaxDegrees = value),
            softMin: 0.0,
            softMax: 360.0,
            decimalPlaces: 1,
            hardMin: 0.0,
            hardMax: 360.0,
            help: "Maximum random rotation in degrees. Set min and max equal to disable rotation variation."));
        layout.AddRow(CreateSliderNumericEditor(
            "Scale Min",
            definition.RandomScaleMin,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomScaleMin = value),
            softMin: 0.25,
            softMax: 2.0,
            decimalPlaces: 3,
            hardMin: 0.01,
            help: "Minimum random uniform scale. Scaling happens around the placement anchor and stays stable between rebuilds."));
        layout.AddRow(CreateSliderNumericEditor(
            "Scale Max",
            definition.RandomScaleMax,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomScaleMax = value),
            softMin: 0.25,
            softMax: 2.0,
            decimalPlaces: 3,
            hardMin: 0.01,
            help: "Maximum random uniform scale. Set min and max to 1.0 for no scale variation."));
        layout.AddRow(CreateNumericEditor(
            "Seed",
            definition.RandomSeed,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomSeed = (int)Math.Round(value)),
            decimalPlaces: 0,
            help: "Stable random seed for this object card. Change it to reroll all matched objects."));
        layout.AddRow(CreateNumericEditor(
            "Z Offset",
            definition.ZOffset,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.ZOffset = value),
            decimalPlaces: 3,
            help: "Lift or sink placed objects. Project mode offsets in world Z; Surface mode offsets along the terrain normal.",
            minValue: null));
        layout.AddRow(CreateReadOnlyValueRow(
            "Bindings",
            $"{CountReferences(definition.Sources)} source refs",
            "Explicit picks plus watched layers drive the objects in this definition. Objects keep their original Rhino layers."));
        return layout;
    }

    private Control CreateMarkerGroup(TerrainDefinition terrain, MarkerDefinition marker)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 4), Padding = new Padding(6, 4) };
        var enabledCheck = new CheckBox { Text = "Enabled", Checked = marker.IsEnabled };
        enabledCheck.CheckedChanged += (_, _) =>
            MutateMarker(terrain.TerrainId, marker.Id, item => item.IsEnabled = enabledCheck.Checked == true);

        layout.AddSeparateRow(
            new Label { Text = marker.Name, Font = new Font(SystemFont.Bold), VerticalAlignment = VerticalAlignment.Center },
            enabledCheck,
            MakeMiniButton("Delete", (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                    _controller.RemoveMarker(doc, terrain.TerrainId, marker.Id);
            }, width: 58),
            null);

        layout.AddRow(CreateSourceEditor("Sources", marker.Sources,
            apply => MutateMarker(terrain.TerrainId, marker.Id, item => apply(item.Sources)),
            RhinoObjectType.Point | RhinoObjectType.PointSet | RhinoObjectType.Curve,
            doc => _controller.GetSelectedLayerPaths(doc)));

        switch (marker)
        {
            case ElevationMarkerDefinition elevation:
                var formatBox = new TextBox { Text = elevation.Format };
                StyleTextBox(formatBox);
                ApplyHelp(formatBox, "Numeric format string for elevation labels. Default F2 gives two decimals.");
                BindCommittedText(formatBox, () => elevation.Format, text =>
                    MutateMarker(terrain.TerrainId, marker.Id, item => ((ElevationMarkerDefinition)item).Format = text, scheduleRebuild: true), trim: false);
                layout.AddSeparateRow(new Label { Text = "Format", Width = 82 }, formatBox, null);
                break;
            case SlopeMarkerDefinition slope:
                var slopeFormatBox = new TextBox { Text = slope.Format };
                StyleTextBox(slopeFormatBox);
                ApplyHelp(slopeFormatBox, "Numeric format string for slope labels. Default F1 is usually enough.");
                BindCommittedText(slopeFormatBox, () => slope.Format, text =>
                    MutateMarker(terrain.TerrainId, marker.Id, item => ((SlopeMarkerDefinition)item).Format = text, scheduleRebuild: true), trim: false);
                var percentCheck = new CheckBox { Text = "Percent", Checked = slope.AsPercent };
                percentCheck.CheckedChanged += (_, _) => MutateMarker(terrain.TerrainId, marker.Id, item => ((SlopeMarkerDefinition)item).AsPercent = percentCheck.Checked == true, scheduleRebuild: true);
                layout.AddSeparateRow(new Label { Text = "Format", Width = 82 }, slopeFormatBox, percentCheck, null);
                break;
        }

        var blockCheck = new CheckBox { Text = "Block", Checked = marker.UseBlockInstance };
        ApplyHelp(blockCheck, "Place a marker symbol as a Rhino block instance so you can edit its graphics through the block definition.");
        blockCheck.CheckedChanged += (_, _) =>
            MutateMarker(terrain.TerrainId, marker.Id, item => item.UseBlockInstance = blockCheck.Checked == true, scheduleRebuild: true);

        var showLabelCheck = new CheckBox { Text = "Label", Checked = marker.ShowValueLabel };
        ApplyHelp(showLabelCheck, "Show the sampled elevation or slope value next to the marker symbol.");
        showLabelCheck.CheckedChanged += (_, _) =>
            MutateMarker(terrain.TerrainId, marker.Id, item => item.ShowValueLabel = showLabelCheck.Checked == true, scheduleRebuild: true);

        var blockNameBox = new TextBox { Text = marker.BlockDefinitionName ?? string.Empty };
        StyleTextBox(blockNameBox);
        ApplyHelp(blockNameBox, "Rhino block definition used for this marker set. Leave blank to use the default MoleHill marker block.");
        BindCommittedText(blockNameBox, () => marker.BlockDefinitionName ?? string.Empty, text =>
            MutateMarker(terrain.TerrainId, marker.Id, item => item.BlockDefinitionName = string.IsNullOrWhiteSpace(text) ? null : text, scheduleRebuild: true), trim: true);

        layout.AddSeparateRow(blockCheck, showLabelCheck, null);
        layout.AddSeparateRow(new Label { Text = "Block Def", Width = 82 }, blockNameBox, null);
        var scaleRow = CreateNumericEditor("Symbol Scale", marker.BlockScale, value =>
            MutateMarker(terrain.TerrainId, marker.Id, item => item.BlockScale = value),
            help: "Scale factor for the block-instance marker symbol. 1.0 is the default; below 1.0 is smaller; above 1.0 is larger.");
        scaleRow.Enabled = marker.UseBlockInstance;
        blockCheck.CheckedChanged += (_, _) => scaleRow.Enabled = blockCheck.Checked == true;
        layout.AddRow(scaleRow);

        return layout;
    }

    private Control CreateSourceEditor(
        string label,
        SourceReferenceSet sourceSet,
        Action<Action<SourceReferenceSet>> mutateSourceSet,
        RhinoObjectType objectFilter,
        Func<RhinoDoc, IEnumerable<string>> getLayerPaths,
        string? help = null)
    {
        // ── Objects pill ──────────────────────────────────────────
        var activeDoc = RhinoDoc.ActiveDoc;
        var liveObjectIds = activeDoc == null
            ? sourceSet.ObjectIds.Distinct().ToList()
            : sourceSet.ObjectIds
                .Where(id => activeDoc.Objects.FindId(id) != null)
                .Distinct()
                .ToList();

        string objText = liveObjectIds.Count == 0
            ? "Add Objects"
            : $"{liveObjectIds.Count} Object{(liveObjectIds.Count == 1 ? "" : "s")}";
        var objectsPill = MakePillButton(objText,
            $"Edit the {label.ToLowerInvariant()} object set. Press Enter to accept.");
        objectsPill.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            Application.Instance.AsyncInvoke(() =>
            {
                if (IsDisposed)
                    return;

                var selectedIds = _controller.EditSourceObjectIds(
                    doc,
                    sourceSet.ObjectIds,
                    objectFilter,
                    $"Adjust {label} selection. Press Enter to accept.");
                if (selectedIds == null)
                    return;

                mutateSourceSet(set => set.ReplaceObjects(selectedIds));
            });
        };

        var objectsRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = liveObjectIds.Count > 0 ? 0 : 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(objectsPill, expand: true) }
        };
        if (liveObjectIds.Count > 0)
        {
            objectsRow.Items.Add(MakeMiniButton("X", (_, _) =>
                    mutateSourceSet(set => set.ReplaceObjects(Array.Empty<Guid>())),
                "Clear all referenced objects.", width: 28));
        }

        // ── Layers pill ───────────────────────────────────────────
        int lc = sourceSet.LayerPaths.Count;
        string layText = lc == 0 ? "Assign Layers" : $"Layers ({lc})";
        var layersPill = MakePillButton(layText, "Manage input layers.");
        layersPill.Click += (_, _) => ShowLayerSourcePopover(
            layersPill,
            sourceSet.LayerPaths,
            path => mutateSourceSet(set => set.AddLayer(path)),
            path => mutateSourceSet(set => set.RemoveLayer(path)));

        var layersClear = MakeMiniButton("Clear", (_, _) =>
            mutateSourceSet(set => set.ReplaceLayers(Array.Empty<string>())),
            "Clear all assigned layers.", width: 48);
        var layersRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = lc > 0 ? 0 : 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(layersPill, expand: true) }
        };
        if (lc > 0)
            layersRow.Items.Add(layersClear);

        // ── Layout ────────────────────────────────────────────────
        var titleLabel = new Label { Text = label, VerticalAlignment = VerticalAlignment.Center };
        if (!UseStackedSourceEditors())
            titleLabel.Width = PropertyLabelWidth;
        ApplyHelp(titleLabel, help ?? $"{label} accepts Rhino object picks and layers.");

        var pillsLayout = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Items = { new StackLayoutItem(objectsRow, expand: true) }
                },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Items = { new StackLayoutItem(layersRow, expand: true) }
                }
            }
        };

        if (UseStackedSourceEditors())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    titleLabel,
                    new StackLayoutItem(pillsLayout, expand: true)
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 3),
            Items = { titleLabel, new StackLayoutItem(pillsLayout, expand: true) }
        };
    }

    private Control CreateNumericEditor(
        string label,
        double value,
        Action<double> onChanged,
        int decimalPlaces = 3,
        string? help = null,
        double? minValue = 0,
        double? maxValue = null)
    {
        help ??= GetNumericHelp(label);
        var stepper = new NumericStepper
        {
            Value = value,
            DecimalPlaces = decimalPlaces,
            Increment = decimalPlaces == 0 ? 1 : 0.1,
            Width = 100
        };
        if (minValue.HasValue)
            stepper.MinValue = minValue.Value;
        if (maxValue.HasValue)
            stepper.MaxValue = maxValue.Value;
        ApplyHelp(stepper, help);
        var timer = new UITimer { Interval = 0.25 };
        double committedValue = value;
        void Commit(double numericValue)
        {
            timer.Stop();
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, numericValue))
                return;

            committedValue = numericValue;
            onChanged(numericValue);
        }
        timer.Elapsed += (_, _) => Commit(stepper.Value);
        stepper.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            timer.Stop();
            timer.Start();
        };
        stepper.LostFocus += (_, _) =>
        {
            if (_isRefreshing)
                return;

            timer.Stop();
            Commit(stepper.Value);
        };
        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    stepper
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                stepper
            }
        };
    }

    private Control CreateSliderNumericEditor(
        string label,
        double value,
        Action<double> onChanged,
        double softMin,
        double softMax,
        int decimalPlaces = 3,
        double? hardMin = null,
        double? hardMax = null,
        string? help = null)
    {
        help ??= GetNumericHelp(label);
        double currentMin = softMin;
        double currentMax = softMax;
        ExpandSliderRange(value, ref currentMin, ref currentMax);

        var slider = new Slider
        {
            MinValue = 0,
            MaxValue = 1000
        };
        if (!UseStackedFormRows())
            slider.Width = 140;
        var textBox = new TextBox
        {
            Width = 88
        };
        StyleTextBox(textBox);
        var valueLabel = new Label
        {
            Width = 56,
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = UiTheme.InputText
        };
        ApplyHelp(slider, help);
        ApplyHelp(textBox, help);
        ApplyHelp(valueLabel, help);

        double committedValue = ClampSliderValue(value, hardMin, hardMax);
        double pendingValue = committedValue;
        bool syncing = false;
        bool sliderEditActive = false;
        bool textEditActive = false;
        var timer = new UITimer { Interval = 0.12 };

        void BeginSliderEdit()
        {
            if (sliderEditActive)
                return;

            sliderEditActive = true;
            BeginControllerRefreshDeferral();
        }

        void EndSliderEdit()
        {
            if (!sliderEditActive)
                return;

            sliderEditActive = false;
            EndControllerRefreshDeferral();
        }

        void BeginTextEdit()
        {
            if (textEditActive)
                return;

            textEditActive = true;
            BeginControllerRefreshDeferral();
        }

        void EndTextEdit()
        {
            if (!textEditActive)
                return;

            textEditActive = false;
            EndControllerRefreshDeferral();
        }

        void SyncControls(double numericValue, bool updateTextBox = true)
        {
            numericValue = ClampSliderValue(numericValue, hardMin, hardMax);
            syncing = true;
            ExpandSliderRange(numericValue, ref currentMin, ref currentMax);
            if (updateTextBox)
                textBox.Text = FormatSliderValue(numericValue, decimalPlaces);
            slider.Value = ToSliderValue(numericValue, currentMin, currentMax, slider.MaxValue);
            valueLabel.Text = FormatSliderValue(numericValue, decimalPlaces);
            syncing = false;
        }

        void Commit(double numericValue)
        {
            timer.Stop();
            numericValue = ClampSliderValue(numericValue, hardMin, hardMax);
            pendingValue = numericValue;
            SyncControls(numericValue);
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, numericValue))
                return;

            committedValue = numericValue;
            onChanged(numericValue);
        }

        void CommitText()
        {
            timer.Stop();
            string text = (textBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                SyncControls(committedValue);
                return;
            }

            if (!TryParseSliderNumericValue(text, out double numericValue))
            {
                SyncControls(committedValue);
                return;
            }

            Commit(numericValue);
        }

        timer.Elapsed += (_, _) => Commit(pendingValue);
        slider.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                BeginSliderEdit();
        };
        slider.MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                EndSliderEdit();
        };
        slider.LostFocus += (_, _) => EndSliderEdit();
        slider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing || syncing)
                return;

            BeginSliderEdit();
            pendingValue = FromSliderValue(slider.Value, currentMin, currentMax, slider.MaxValue);
            SyncControls(pendingValue, updateTextBox: !textEditActive);
            timer.Stop();
            timer.Start();
        };
        textBox.GotFocus += (_, _) => BeginTextEdit();
        textBox.LostFocus += (_, _) =>
        {
            CommitText();
            EndTextEdit();
        };
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter)
                return;

            CommitText();
            EndTextEdit();
            e.Handled = true;
        };

        SyncControls(committedValue);
        if (UseStackedFormRows())
        {
            var compactEditor = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(slider, HorizontalAlignment.Stretch),
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Items =
                        {
                            textBox,
                            valueLabel,
                            new StackLayoutItem(new Panel(), expand: true)
                        }
                    }
                }
            };

            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    new StackLayoutItem(compactEditor, HorizontalAlignment.Stretch)
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                slider,
                textBox,
                valueLabel
            }
        };
    }

    private static void ExpandSliderRange(double value, ref double min, ref double max)
    {
        min = Math.Min(min, value);
        max = Math.Max(max, value);
        if (max <= min)
            max = min + 1.0;

        if (value > max * 0.98)
            max = NiceNumber(Math.Max(value, max));
        if (value < min * 1.02 && value > 0)
            min = Math.Min(min, NiceNumber(value * 0.5));
    }

    private static int ToSliderValue(double value, double min, double max, int sliderMax)
    {
        if (max <= min)
            return 0;

        double t = Math.Clamp((value - min) / (max - min), 0.0, 1.0);
        return (int)Math.Round(t * sliderMax);
    }

    private static double FromSliderValue(int sliderValue, double min, double max, int sliderMax)
    {
        if (sliderMax <= 0 || max <= min)
            return min;

        double t = sliderValue / (double)sliderMax;
        return min + (max - min) * t;
    }

    private static string FormatSliderValue(double value, int decimalPlaces)
    {
        return decimalPlaces == 0
            ? ((int)Math.Round(value)).ToString(CultureInfo.CurrentCulture)
            : value.ToString($"F{decimalPlaces}", CultureInfo.CurrentCulture);
    }

    private static bool TryParseSliderNumericValue(string text, out double value)
    {
        const NumberStyles Styles = NumberStyles.Float | NumberStyles.AllowThousands;
        return double.TryParse(text, Styles, CultureInfo.CurrentCulture, out value) ||
               double.TryParse(text, Styles, CultureInfo.InvariantCulture, out value);
    }

    private static double ClampSliderValue(double value, double? hardMin, double? hardMax)
    {
        if (hardMin.HasValue)
            value = Math.Max(hardMin.Value, value);
        if (hardMax.HasValue)
            value = Math.Min(hardMax.Value, value);
        return value;
    }

    private static double NiceNumber(double value)
    {
        if (value <= 0)
            return 1.0;

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double factor in new[] { 1.0, 2.0, 5.0, 10.0 })
        {
            double candidate = magnitude * factor;
            if (candidate >= value)
                return candidate;
        }

        return magnitude * 10.0;
    }

    private Control CreateReadOnlyValueRow(string label, string value, string help)
    {
        var valueLabel = new Label
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = UiTheme.InputText,
            Wrap = WrapMode.Word
        };

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    valueLabel
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                valueLabel
            }
        };
    }

    private Control CreateSelectableSummaryEditor(string label, string value, string help, int minHeight = 110)
    {
        var textArea = new TextArea
        {
            Text = value,
            ReadOnly = true,
            Wrap = true,
            Height = minHeight
        };
        StyleTextArea(textArea);
        ApplyHelp(textArea, help);

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                new StackLayoutItem(textArea, HorizontalAlignment.Stretch)
            }
        };
    }

    private Control CreateCheckEditor(string label, bool value, Action<bool> onChanged, string help)
    {
        var checkBox = new CheckBox { Checked = value };
        ApplyHelp(checkBox, help);
        checkBox.CheckedChanged += (_, _) => onChanged(checkBox.Checked == true);

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    checkBox
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                checkBox
            }
        };
    }

    private Control CreateDropDownEditor(
        string label,
        IReadOnlyList<(string Key, string Label)> options,
        string selectedKey,
        Action<string> onChanged,
        string help)
    {
        var dropDown = new DropDown();
        if (!UseStackedFormRows())
            dropDown.Width = 160;
        foreach (var option in options)
            dropDown.Items.Add(new ListItem { Text = option.Label });

        int selectedIndex = options
            .Select((option, index) => (option, index))
            .FirstOrDefault(item => string.Equals(item.option.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            .index;
        dropDown.SelectedIndex = selectedIndex >= 0 && selectedIndex < options.Count ? selectedIndex : 0;
        ApplyHelp(dropDown, help);
        dropDown.SelectedIndexChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            int index = dropDown.SelectedIndex;
            if (index < 0 || index >= options.Count)
                return;

            onChanged(options[index].Key);
        };

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    dropDown
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                dropDown
            }
        };
    }

    private Control CreateCommittedTextEditor(
        string label,
        string value,
        Action<string> onCommit,
        string help,
        bool trim = true)
    {
        var textBox = new TextBox { Text = value };
        StyleTextBox(textBox);
        ApplyHelp(textBox, help);
        BindCommittedText(textBox, () => value, onCommit, trim: trim);

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    textBox
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                new StackLayoutItem(textBox, expand: true)
            }
        };
    }

    private Control CreateSlopeUnitDropDown(
        SlopeAnalyzer.SlopeUnit unit,
        Action<SlopeAnalyzer.SlopeUnit> onChanged,
        string help)
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
            GetSlopeUnitKey(unit),
            value => onChanged(ParseSlopeUnit(value)),
            help);
    }

    private void AddBlockAttributeAnalysisRows<TAnalysis>(
        DynamicLayout layout,
        TerrainDefinition terrain,
        TAnalysis analysis,
        RhinoObjectType objectFilter,
        Action<Action<TAnalysis>> mutate,
        string sourceHelp,
        string formatHelp,
        Action<DynamicLayout>? extraRows = null)
        where TAnalysis : BlockAttributeAnalysisDefinition
    {
        layout.AddRow(CreateSourceEditor(
            "Sources",
            analysis.Sources,
            apply => mutate(item => apply(item.Sources)),
            objectFilter,
            doc => _controller.GetSelectedLayerPaths(doc),
            sourceHelp));
        extraRows?.Invoke(layout);
        layout.AddRow(CreateCommittedTextEditor(
            "Format",
            analysis.ValueFormat,
            text => mutate(item => item.ValueFormat = text),
            formatHelp,
            trim: false));
        layout.AddRow(CreateCommittedTextEditor(
            "Prefix",
            analysis.AttributePrefix,
            text => mutate(item => item.AttributePrefix = text),
            "Text prepended to the formatted value when filling the DISPLAY block attribute.",
            trim: false));
        layout.AddRow(CreateCommittedTextEditor(
            "Suffix",
            analysis.AttributeSuffix,
            text => mutate(item => item.AttributeSuffix = text),
            "Text appended after the formatted value and unit when filling the DISPLAY block attribute.",
            trim: false));
        layout.AddRow(CreateNumericEditor(
            "Block Scale",
            analysis.BlockScale,
            value => mutate(item => item.BlockScale = value),
            help: "Scale factor for inserted annotation blocks.",
            minValue: 0.01));
        layout.AddRow(CreateLayerAssignmentEditor(
            "Output Layer",
            analysis.OutputLayerPath,
            path => mutate(item => item.OutputLayerPath = path),
            "Layer used for generated annotation instances. Leave empty to use the terrain auxiliary layer."));
        layout.AddRow(CreateOptionalColorEditor(
            "Color",
            analysis.ColorArgb,
            value => mutate(item => item.ColorArgb = value),
            "Explicit display and bake color for generated annotation blocks. Clear to use the output layer color.",
            ResolveLayerColorArgb(analysis.OutputLayerPath ?? terrain.AnnotationLayerPath),
            GetAnalysisOutputColorText(terrain, analysis.OutputLayerPath)));
    }

    private static string GetAnalysisOutputColorText(TerrainDefinition terrain, string? outputLayerPath)
    {
        if (string.IsNullOrWhiteSpace(outputLayerPath))
        {
            return string.IsNullOrWhiteSpace(terrain.AnnotationLayerPath)
                ? $"By Layer ({TerrainDefinition.DefaultAnnotationLayerPath})"
                : $"By Layer ({GetLeafLayerName(terrain.AnnotationLayerPath!)})";
        }

        return $"By Layer ({GetLeafLayerName(outputLayerPath)})";
    }

    private GroupBox CreateSlopePreviewGroup(TerrainDefinition terrain, TerrainAnalysisSummary? analysis)
    {
        var palette = SlopePreviewPaletteCatalog.Resolve(terrain.SlopePalettePreset);
        double displayLow = analysis?.SlopeDisplayLowPercent ?? terrain.SlopeColorLowPercent;
        double displayHigh = analysis?.SlopeDisplayHighPercent ?? terrain.SlopeColorHighPercent;
        if (displayHigh <= displayLow)
            displayHigh = displayLow + 1.0;

        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 4), Padding = new Padding(6, 4) };
        var paletteOptions = SlopePreviewPaletteCatalog.All
            .Select(item => (item.Key, item.Label))
            .ToList();

        layout.AddRow(CreateDropDownEditor(
            "Palette",
            paletteOptions,
            terrain.SlopePalettePreset,
            value => MutateSelectedTerrain(item => item.SlopePalettePreset = value, scheduleRebuild: true),
            "Color ramp used for the slope preview mesh and legend."));
        layout.AddRow(CreateNumericEditor(
            "Low %",
            terrain.SlopeColorLowPercent,
            value => MutateSelectedTerrain(item => item.SlopeColorLowPercent = value, scheduleRebuild: true),
            decimalPlaces: 1,
            help: "Values at or below this percent use the cool end of the selected palette."));
        layout.AddRow(CreateNumericEditor(
            "High %",
            terrain.SlopeColorHighPercent,
            value => MutateSelectedTerrain(item => item.SlopeColorHighPercent = value, scheduleRebuild: true),
            decimalPlaces: 1,
            help: "Values at or above this percent use the hot end of the palette. Leave at 0 to auto-fit the terrain."));
        layout.AddRow(CreateReadOnlyValueRow(
            "Mapped",
            $"{displayLow:F1}% to {displayHigh:F1}%",
            "Actual percent range currently mapped across the selected palette."));
        layout.AddRow(CreateSlopeLegendView(palette, displayLow, displayHigh));

        return new GroupBox
        {
            Text = "Slope Preview",
            Content = layout
        };
    }

    private Control CreateSlopeLegendView(
        SlopePreviewPalette palette,
        double displayLow = 0.0,
        double displayHigh = 0.0,
        string? displayLowLabel = null,
        string? displayHighLabel = null)
    {
        const int barHeight = 22;
        const int tickHeight = 4;
        const int numTicks = 5;

        string lowLabel  = displayLowLabel  ?? $"{displayLow:F1}%";
        string highLabel = displayHighLabel ?? $"{displayHigh:F1}%";

        // Gradient bar painted smoothly via Drawable
        bool compactLegend = UseStackedFormRows();
        var gradientBar = new Drawable
        {
            Height = barHeight,
            MinimumSize = new Size(0, barHeight)
        };
        gradientBar.Paint += (sender, e) =>
        {
            var g = e.Graphics;
            var ctrl = (Drawable)sender!;
            int w = ctrl.Width;
            if (w <= 0) return;

            const int steps = 256;
            for (int i = 0; i < steps; i++)
            {
                double t  = i / (double)(steps - 1);
                var c     = SamplePaletteColor(palette.Stops, t);
                float x0  = (float)i / steps * w;
                float x1  = (float)(i + 1) / steps * w;
                g.FillRectangle(c, x0, 0f, Math.Max(1f, x1 - x0), barHeight);
            }

            // Tick marks at evenly spaced positions
            var tickBrush = new SolidBrush(UiTheme.MutedText);
            for (int i = 0; i < numTicks; i++)
            {
                float t = i / (float)(numTicks - 1);
                float x = t * (w - 1);
                g.FillRectangle(tickBrush, x, barHeight - tickHeight, 1f, tickHeight);
            }
        };

        var paletteLabel = new Label
        {
            Text = palette.Label,
            TextColor = UiTheme.MutedText,
            TextAlignment = TextAlignment.Center,
            Wrap = WrapMode.Word
        };
        var labelsRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(new Label
                {
                    Text = lowLabel,
                    TextColor = UiTheme.MutedText,
                    Wrap = WrapMode.Word
                }, expand: true),
                new StackLayoutItem(new Label
                {
                    Text = highLabel,
                    TextColor = UiTheme.MutedText,
                    TextAlignment = TextAlignment.Right,
                    Wrap = WrapMode.Word
                }, expand: true)
            }
        };

        var legend = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = compactLegend ? 4 : 2,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Padding(0, 4, 0, 0)
        };
        if (!compactLegend)
            legend.Items.Add(new StackLayoutItem(paletteLabel, HorizontalAlignment.Stretch));
        legend.Items.Add(new StackLayoutItem(gradientBar, HorizontalAlignment.Stretch));
        legend.Items.Add(new StackLayoutItem(labelsRow, HorizontalAlignment.Stretch));
        if (compactLegend)
            legend.Items.Add(new StackLayoutItem(paletteLabel, HorizontalAlignment.Stretch));
        return legend;
    }

    private static Color SamplePaletteColor(IReadOnlyList<MoleHill.Core.Analysis.SlopeAnalyzer.ColorStop> stops, double position)
    {
        if (stops.Count == 0)
            return Color.FromArgb(0, 200, 0);

        if (position <= stops[0].Position)
            return Color.FromArgb(stops[0].R, stops[0].G, stops[0].B);

        for (int index = 1; index < stops.Count; index++)
        {
            var previous = stops[index - 1];
            var current = stops[index];
            if (position > current.Position)
                continue;

            double segment = current.Position - previous.Position;
            if (segment <= 1e-9)
                return Color.FromArgb(current.R, current.G, current.B);

            double localT = Math.Clamp((position - previous.Position) / segment, 0.0, 1.0);
            return Color.FromArgb(
                InterpolateChannel(previous.R, current.R, localT),
                InterpolateChannel(previous.G, current.G, localT),
                InterpolateChannel(previous.B, current.B, localT));
        }

        var last = stops[^1];
        return Color.FromArgb(last.R, last.G, last.B);
    }

    private static int InterpolateChannel(byte start, byte end, double t)
    {
        return (int)Math.Round(start + ((end - start) * Math.Clamp(t, 0.0, 1.0)));
    }

    private static string EllipsizeText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars || maxChars <= 3)
            return text;

        return text[..(maxChars - 3)] + "...";
    }

    private Control CreateZoneLayerEditor(TerrainDefinition terrain, CollageZoneDefinition zone)
    {
        string? layerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        string bakedLayer = TerrainBuildService.GetBakedLayerPath(layerPath) ?? "None";

        var assignedLayerLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(layerPath) ? "No layer" : GetLeafLayerName(layerPath),
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.Word
        };
        ApplyHelp(assignedLayerLabel, layerPath ?? "No input layer assigned.");
        var useCurrentButton = MakeCompactButton("Use Current", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            var selectedLayer = _controller.GetSelectedLayerPaths(doc).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(selectedLayer))
                return;

            MutateZone(terrain.TerrainId, zone.ZoneId, item =>
            {
                item.Boundaries.ObjectIds.Clear();
                item.Boundaries.ReplaceLayers(new[] { selectedLayer });
                item.Name = GetLeafLayerName(selectedLayer);
            });
        }, "Assign the first selected Rhino layer to this zone.");

        var browseButton = MakeLayerPickerButton(path =>
        {
            if (path == null) return;
            MutateZone(terrain.TerrainId, zone.ZoneId, item =>
            {
                item.Boundaries.ObjectIds.Clear();
                item.Boundaries.ReplaceLayers(new[] { path });
                item.Name = GetLeafLayerName(path);
            });
        }, "Browse and pick a layer for this zone.");

        var clearButton = MakeCompactButton("Clear", (_, _) =>
        {
            MutateZone(terrain.TerrainId, zone.ZoneId, item =>
            {
                item.Boundaries.ObjectIds.Clear();
                item.Boundaries.ReplaceLayers(Array.Empty<string>());
            });
        }, "Remove the assigned input layer.");
        var bakedLayerLabel = new Label
        {
            Text = $"Bake -> {bakedLayer}",
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = UiTheme.MutedText,
            Wrap = WrapMode.Word
        };
        ApplyHelp(bakedLayerLabel, "Generated zone meshes preview using the source layer color and bake under this output layer.");
        var buttonRow = CreateResponsiveControlGroup(4, useCurrentButton, browseButton, clearButton);

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Layer", "Zones are driven by Rhino layers. The baked output layer is generated automatically.", 0),
                    assignedLayerLabel,
                    bakedLayerLabel,
                    new StackLayoutItem(buttonRow, HorizontalAlignment.Stretch)
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                CreateHelpLabel("Layer", "Zones are driven by Rhino layers. The baked output layer is generated automatically.", PropertyLabelWidth),
                new StackLayoutItem(assignedLayerLabel, expand: true),
                useCurrentButton,
                browseButton,
                clearButton,
                bakedLayerLabel
            }
        };
    }

    private Control CreateLayerAssignmentEditor(string label, string? layerPath, Action<string?> onCommit, string help)
    {
        var assignedLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(layerPath) ? "(default)" : layerPath,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.Word
        };
        ApplyHelp(assignedLabel, help);
        var useCurrentButton = MakeCompactButton("Use Current", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            onCommit(doc?.Layers.CurrentLayer?.FullPath);
        }, "Assign Rhino's current layer.");

        var browseButton = MakeLayerPickerButton(path => onCommit(path), "Browse and pick a layer");

        var clearButton = MakeCompactButton("Clear", (_, _) => onCommit(null), "Clear the explicit layer assignment and fall back to the default.");
        var buttonRow = CreateResponsiveControlGroup(3, useCurrentButton, browseButton, clearButton);

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    assignedLabel,
                    new StackLayoutItem(buttonRow, HorizontalAlignment.Stretch)
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                CreateHelpLabel(label, help, PropertyLabelWidth),
                new StackLayoutItem(assignedLabel, expand: true),
                useCurrentButton,
                browseButton,
                clearButton
            }
        };
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

        var assignedLabel = new Label
        {
            Text = colorArgb.HasValue ? DescribeSolidColor(colorArgb.Value) : defaultText,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.Word
        };
        ApplyHelp(assignedLabel, help);

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

        var pickButton = MakeCompactButton("Pick", (_, _) => PickColor(), "Choose an explicit color for this output.");
        var clearButton = MakeCompactButton("Clear", (_, _) => onCommit(null), "Clear the explicit color and use the layer color instead.");
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
        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    new StackLayoutItem(summaryRow, HorizontalAlignment.Stretch),
                    new StackLayoutItem(buttonRow, HorizontalAlignment.Stretch)
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                CreateHelpLabel(label, help, PropertyLabelWidth),
                swatch,
                new StackLayoutItem(assignedLabel, expand: true),
                pickButton,
                clearButton
            }
        };
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

    private static string GetLeafLayerName(string layerPath)
    {
        return layerPath.Contains("::", StringComparison.Ordinal)
            ? layerPath[(layerPath.LastIndexOf("::", StringComparison.Ordinal) + 2)..]
            : layerPath;
    }

    private static void StyleTextBox(TextBox textBox)
    {
        textBox.BackgroundColor = UiTheme.InputBackground;
        textBox.TextColor = UiTheme.InputText;
    }

    private static void StyleTextArea(TextArea textArea)
    {
        textArea.BackgroundColor = UiTheme.InputBackground;
        textArea.TextColor = UiTheme.InputText;
    }

    private static Button MakeButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null)
    {
        var button = new Button { Text = text };
        if (!string.IsNullOrWhiteSpace(toolTip))
            button.ToolTip = toolTip;
        button.Click += onClick;
        return button;
    }

    private static Button MakeToolbarButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null, int width = 0)
    {
        var button = new Button { Text = text, Height = 26 };
        if (width > 0)
            button.Width = width;
        if (!string.IsNullOrWhiteSpace(toolTip))
            button.ToolTip = toolTip;
        button.Click += onClick;
        return button;
    }

    private static Button MakeIconButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null)
    {
        var button = new Button { Text = text, Width = 34, Height = HeaderActionHeight };
        if (!string.IsNullOrWhiteSpace(toolTip))
            button.ToolTip = toolTip;
        button.Click += onClick;
        return button;
    }

    private static Button MakeCompactButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null)
    {
        var button = new Button { Text = text, Height = HeaderActionHeight };
        if (!string.IsNullOrWhiteSpace(toolTip))
            button.ToolTip = toolTip;
        button.Click += onClick;
        return button;
    }

    private static Button MakeMiniButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null, int width = 46)
    {
        var button = new Button { Text = text, Width = width, Height = HeaderActionHeight };
        if (!string.IsNullOrWhiteSpace(toolTip))
            button.ToolTip = toolTip;
        button.Click += onClick;
        return button;
    }

    private static Button MakePillButton(string text, string? tooltip = null)
    {
        return new Button
        {
            Text = text,
            Height = 22,
            BackgroundColor = UiTheme.PillBackground,
            TextColor = UiTheme.InputText,
            ToolTip = tooltip ?? string.Empty
        };
    }

    private static Panel CreateToolbarGroup(params Control[] controls)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        foreach (var control in controls)
            row.Items.Add(control);

        return new Panel
        {
            BackgroundColor = UiTheme.ToolbarBackground,
            Padding = new Padding(6, 6, 6, 6),
            Content = row
        };
    }

    private Label CreateHelpLabel(string text, string help, int width)
    {
        var label = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (width > 0)
            label.Width = width;
        ApplyHelp(label, help);
        return label;
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

    private static GroupBox CreateAnalysisGroup(string title, IEnumerable<(string Label, string Value)> rows)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 4), Padding = new Padding(6, 4) };
        foreach (var (label, value) in rows)
            layout.AddSeparateRow(new Label { Text = label, Width = 82 }, new Label { Text = value }, null);

        return new GroupBox { Text = title, Content = layout };
    }

    private static string FormatVolume(double value)
    {
        string prefix = value < 0 ? "-" : string.Empty;
        return $"{prefix}{Math.Abs(value):F2} cu units";
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

    private void MoveModifier(Guid terrainId, Guid modifierId, int direction)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
            _controller.MoveModifier(doc, terrainId, modifierId, direction);
    }

    private void MoveZone(Guid terrainId, Guid zoneId, int direction)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Zones.FindIndex(zone => zone.ZoneId == zoneId);
            if (index < 0)
                return;

            int targetIndex = Math.Clamp(index + direction, 0, terrain.Zones.Count - 1);
            if (targetIndex == index)
                return;

            var zone = terrain.Zones[index];
            terrain.Zones.RemoveAt(index);
            terrain.Zones.Insert(targetIndex, zone);
        });
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

    private void RemoveZone(Guid terrainId, Guid zoneId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            terrain.Zones.RemoveAll(zone => zone.ZoneId == zoneId);
        });
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
        MutateSelectedTerrain(terrain =>
        {
            AnalysisDefinition? analysis = kind switch
            {
                "earthwork" => new EarthworkAnalysisDefinition(),
                "slope" => new SlopeAnalysisDefinition(),
                "elevation" => new ElevationAnalysisDefinition(),
                "cut-fill" => new CutFillAnalysisDefinition(),
                "contour" => new ContourAnalysisDefinition(),
                "curve-elevation-label" => new CurveElevationLabelAnalysisDefinition(),
                "curve-slope-label" => new CurveSlopeLabelAnalysisDefinition(),
                "projected-elevation-label" => new ProjectedElevationLabelAnalysisDefinition(),
                "point-slope-label" => new PointSlopeLabelAnalysisDefinition(),
                "terrain-section" => new TerrainSectionAnalysisDefinition(),
                "cross-section-station" => new CrossSectionStationAnalysisDefinition(),
                "longitudinal-section" => new LongitudinalSectionAnalysisDefinition(),
                _ => null
            };

            if (analysis != null)
                terrain.Analyses.Insert(0, analysis);
        }, scheduleRebuild: false);

        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (terrain != null)
        {
            RefreshUi();
            RefreshTerrainPreview(terrain.TerrainId);
        }
    }

    private void MutateSelectedTerrain(Action<TerrainDefinition> mutator, bool scheduleRebuild = true)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.MutateTerrain(doc, terrain.TerrainId, mutator, scheduleRebuild);
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

    private void MutateZone(Guid terrainId, Guid zoneId, Action<CollageZoneDefinition> mutator, bool scheduleRebuild = true)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var zone = terrain.Zones.FirstOrDefault(item => item.ZoneId == zoneId);
            if (zone != null)
                mutator(zone);
        }, scheduleRebuild);
    }

    private void MutateMarker(Guid terrainId, Guid markerId, Action<MarkerDefinition> mutator, bool scheduleRebuild = true)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var marker = terrain.Markers.FirstOrDefault(item => item.Id == markerId);
            if (marker != null)
                mutator(marker);
        }, scheduleRebuild);
    }

    private void MutateObjectDefinition(Guid terrainId, Guid definitionId, Action<TerrainObjectDefinition> mutator, bool scheduleRebuild = true)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var definition = terrain.Objects.FirstOrDefault(item => item.Id == definitionId);
            if (definition != null)
                mutator(definition);
        }, scheduleRebuild);
    }

    private void MutateAnalysis(Guid terrainId, Guid analysisId, Action<AnalysisDefinition> mutator, bool scheduleRebuild = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var analysis = terrain.Analyses.FirstOrDefault(item => item.Id == analysisId);
            if (analysis != null)
                mutator(analysis);
        }, scheduleRebuild);
    }

    private void MutateAndRefreshAnalysis(Guid terrainId, Guid analysisId, Action<AnalysisDefinition> mutator)
    {
        MutateAnalysis(terrainId, analysisId, mutator, scheduleRebuild: false);
        RefreshTerrainPreview(terrainId);
    }

    private void RefreshTerrainPreview(Guid terrainId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
            _controller.RefreshTerrainDisplay(doc, terrainId);
    }

    private static TerrainAnalysisSummary? GetAnalysisSummary(TerrainDefinition terrain, Guid analysisId)
    {
        return terrain.LastAnalysisResults.FirstOrDefault(item => item.AnalysisId == analysisId);
    }

    private static string GetAnalysisTypeLabel(AnalysisDefinition analysis)
    {
        return analysis switch
        {
            EarthworkAnalysisDefinition => "Earthworks",
            SlopeAnalysisDefinition => "Slope",
            ElevationAnalysisDefinition => "Elevation",
            CutFillAnalysisDefinition => "Cut / Fill",
            ContourAnalysisDefinition => "Contours",
            CurveElevationLabelAnalysisDefinition => "Curve Elevation",
            CurveSlopeLabelAnalysisDefinition => "Curve Slope",
            ProjectedElevationLabelAnalysisDefinition => "Proj. Elevation",
            PointSlopeLabelAnalysisDefinition => "Point Slope",
            TerrainSectionAnalysisDefinition => "Terrain Section",
            CrossSectionStationAnalysisDefinition => "Cross-Sections",
            LongitudinalSectionAnalysisDefinition => "Long. Section",
            _ => "Analysis"
        };
    }

    private static string GetTerrainObjectTypeLabel(TerrainObjectDefinition definition)
    {
        return definition switch
        {
            LowestPointObjectDefinition => "Plant",
            SurfaceOrientedObjectDefinition => "Orient",
            _ => "Objects"
        };
    }

    private static string GetTerrainObjectKind(TerrainObjectDefinition definition) => definition switch
    {
        LowestPointObjectDefinition => "lowest-point",
        SurfaceOrientedObjectDefinition => "surface-oriented",
        _ => string.Empty
    };

    private static Color TerrainObjectTypeColor(string kind) => kind switch
    {
        "lowest-point" => Color.FromArgb(30, 136, 229),
        "surface-oriented" => Color.FromArgb(67, 160, 71),
        _ => Color.FromArgb(120, 120, 120)
    };

    private static string GetTerrainObjectIconLabel(TerrainObjectDefinition definition) => definition switch
    {
        LowestPointObjectDefinition => "Z",
        SurfaceOrientedObjectDefinition => "XY",
        _ => "O"
    };

    private static string GetTerrainObjectSubtitle(TerrainObjectDefinition definition) => definition switch
    {
        LowestPointObjectDefinition => "Place lowest point on terrain",
        SurfaceOrientedObjectDefinition => "Orient to terrain slope",
        _ => "Terrain objects"
    };

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

    private static string GetZoneCollapsedSummary(CollageZoneDefinition zone)
    {
        var parts = new List<string>();
        string? layerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(layerPath))
            parts.Add(GetLeafLayerName(layerPath));

        parts.Add(zone.UseInputElevationForPriority ? "Elevation priority" : "Stack order");
        parts.Add($"{CountReferences(zone.Boundaries)} refs");
        return string.Join(" | ", parts);
    }

    private static string GetAnalysisKind(AnalysisDefinition analysis) => analysis switch
    {
        EarthworkAnalysisDefinition => "earthwork",
        SlopeAnalysisDefinition => "slope",
        ElevationAnalysisDefinition => "elevation",
        CutFillAnalysisDefinition => "cut-fill",
        ContourAnalysisDefinition => "contour",
        CurveElevationLabelAnalysisDefinition => "curve-elevation-label",
        CurveSlopeLabelAnalysisDefinition => "curve-slope-label",
        ProjectedElevationLabelAnalysisDefinition => "projected-elevation-label",
        PointSlopeLabelAnalysisDefinition => "point-slope-label",
        TerrainSectionAnalysisDefinition => "terrain-section",
        CrossSectionStationAnalysisDefinition => "cross-section-station",
        LongitudinalSectionAnalysisDefinition => "longitudinal-section",
        _ => string.Empty
    };

    private static bool IsAnnotationAnalysis(AnalysisDefinition analysis) => analysis is
        ContourAnalysisDefinition or
        CurveElevationLabelAnalysisDefinition or
        CurveSlopeLabelAnalysisDefinition or
        ProjectedElevationLabelAnalysisDefinition or
        PointSlopeLabelAnalysisDefinition or
        TerrainSectionAnalysisDefinitionBase;

    private static Color AnalysisTypeColor(string kind) => kind switch
    {
        "earthwork" => Color.FromArgb(141, 110, 99),
        "slope" => Color.FromArgb(67, 160, 71),
        "elevation" => Color.FromArgb(30, 136, 229),
        "cut-fill" => Color.FromArgb(239, 108, 0),
        "contour" => Color.FromArgb(0, 121, 107),
        "curve-elevation-label" => Color.FromArgb(21, 101, 192),
        "curve-slope-label" => Color.FromArgb(46, 125, 50),
        "projected-elevation-label" => Color.FromArgb(21, 101, 192),
        "point-slope-label" => Color.FromArgb(2, 136, 209),
        "terrain-section" => Color.FromArgb(123, 31, 162),
        "cross-section-station" => Color.FromArgb(142, 36, 170),
        "longitudinal-section" => Color.FromArgb(94, 53, 177),
        _ => Color.FromArgb(120, 120, 120)
    };

    private static string GetAnalysisIconLabel(AnalysisDefinition analysis) => analysis switch
    {
        EarthworkAnalysisDefinition => "EW",
        SlopeAnalysisDefinition => "%",
        ElevationAnalysisDefinition => "Z",
        CutFillAnalysisDefinition => "+/-",
        ContourAnalysisDefinition => "CT",
        CurveElevationLabelAnalysisDefinition => "CE",
        CurveSlopeLabelAnalysisDefinition => "C%",
        ProjectedElevationLabelAnalysisDefinition => "PZ",
        PointSlopeLabelAnalysisDefinition => "P%",
        TerrainSectionAnalysisDefinition => "TS",
        CrossSectionStationAnalysisDefinition => "XS",
        LongitudinalSectionAnalysisDefinition => "LS",
        _ => "A"
    };

    private static string GetAnalysisSubtitle(AnalysisDefinition analysis, bool isActive)
    {
        return analysis switch
        {
            EarthworkAnalysisDefinition => "Refs + summary",
            SlopeAnalysisDefinition => isActive ? "Preview colors" : "Slope preview",
            ElevationAnalysisDefinition => isActive ? "Preview colors" : "Elevation preview",
            CutFillAnalysisDefinition => isActive ? "Preview colors" : "Signed delta preview",
            ContourAnalysisDefinition => "Contour output",
            CurveElevationLabelAnalysisDefinition => "Curve elevation blocks",
            CurveSlopeLabelAnalysisDefinition => "Curve grade blocks",
            ProjectedElevationLabelAnalysisDefinition => "Projected elevation blocks",
            PointSlopeLabelAnalysisDefinition => "Point slope blocks",
            TerrainSectionAnalysisDefinition => "Geländeschnitt profile",
            CrossSectionStationAnalysisDefinition => "Stations + grid",
            LongitudinalSectionAnalysisDefinition => "Unrolled longitudinal",
            _ => "Analysis"
        };
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
            ContourAnalysisDefinition contour => summary != null
                ? $"{summary.ContourCurveCount} curves | {contour.Interval:G4} @ {contour.StartZ:G4}"
                : $"{contour.Interval:G4} every | start {contour.StartZ:G4}",
            CurveElevationLabelAnalysisDefinition curveElevation => summary != null
                ? summary.GeneratedOutputCount > 0
                    ? $"{summary.GeneratedOutputCount} labels | {FormatAnalysisValue(summary.SampleAverageValue, curveElevation.ValueFormat)} avg"
                    : "0 labels"
                : $"{CountReferences(curveElevation.Sources)} refs | {curveElevation.Interval:G4} every",
            CurveSlopeLabelAnalysisDefinition curveSlope => summary != null
                ? summary.GeneratedOutputCount > 0
                    ? $"{summary.GeneratedOutputCount} labels | {FormatSlopeValue(summary.SampleAverageValue, curveSlope.Unit)} avg"
                    : "0 labels"
                : $"{CountReferences(curveSlope.Sources)} refs | {curveSlope.Interval:G4} every",
            ProjectedElevationLabelAnalysisDefinition projectedElevation => summary != null
                ? summary.GeneratedOutputCount > 0
                    ? $"{summary.GeneratedOutputCount} labels | {FormatAnalysisValue(summary.SampleMinValue, projectedElevation.ValueFormat)} to {FormatAnalysisValue(summary.SampleMaxValue, projectedElevation.ValueFormat)}"
                    : "0 labels"
                : $"{CountReferences(projectedElevation.Sources)} refs | projected Z",
            PointSlopeLabelAnalysisDefinition pointSlope => summary != null
                ? summary.GeneratedOutputCount > 0
                    ? $"{summary.GeneratedOutputCount} labels | {FormatSlopeValue(summary.SampleAverageValue, pointSlope.Unit)} avg"
                    : "0 labels"
                : $"{CountReferences(pointSlope.Sources)} refs | terrain slope",
            TerrainSectionAnalysisDefinition section => summary != null
                ? $"{summary.GeneratedOutputCount} objects | {summary.SampleSourceCount} cuts"
                : $"{CountReferences(section.Sources)} refs | profile",
            CrossSectionStationAnalysisDefinition crossSection => summary != null
                ? $"{summary.GeneratedOutputCount} objects | {crossSection.StationInterval:G4} every"
                : $"{CountReferences(crossSection.Sources)} refs | {crossSection.StationInterval:G4} stations",
            LongitudinalSectionAnalysisDefinition longitudinal => summary != null
                ? $"{summary.GeneratedOutputCount} objects | V exag {longitudinal.VerticalExaggeration:G3}"
                : $"{CountReferences(longitudinal.Sources)} refs | sample {longitudinal.SampleInterval:G4}",
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
                MutateAndRefreshAnalysis(terrainId, slope.Id, item =>
                {
                    if (item is not SlopeAnalysisDefinition target)
                        return;

                    target.Unit = nextUnit;
                    target.RangeLow = rangeLow;
                    target.RangeHigh = rangeHigh;
                });
            },
            "Show slope values as percent, promille, rise/run ratio, or degrees.");
    }

    private static string GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit unit) => unit switch
    {
        SlopeAnalyzer.SlopeUnit.Percent => "percent",
        SlopeAnalyzer.SlopeUnit.Promille => "promille",
        SlopeAnalyzer.SlopeUnit.Ratio => "ratio",
        SlopeAnalyzer.SlopeUnit.Degrees => "degrees",
        _ => "percent"
    };

    private static SlopeAnalyzer.SlopeUnit ParseSlopeUnit(string key) => key switch
    {
        "promille" => SlopeAnalyzer.SlopeUnit.Promille,
        "ratio" => SlopeAnalyzer.SlopeUnit.Ratio,
        "degrees" => SlopeAnalyzer.SlopeUnit.Degrees,
        _ => SlopeAnalyzer.SlopeUnit.Percent
    };

    private static string GetSlopeUnitSuffixLabel(SlopeAnalyzer.SlopeUnit unit) => unit switch
    {
        SlopeAnalyzer.SlopeUnit.Percent => "%",
        SlopeAnalyzer.SlopeUnit.Promille => "promille",
        SlopeAnalyzer.SlopeUnit.Ratio => "ratio",
        SlopeAnalyzer.SlopeUnit.Degrees => "deg",
        _ => "%"
    };

    private static string FormatSlopeSummaryValue(double percentValue, SlopeAnalyzer.SlopeUnit unit)
    {
        return FormatSlopeValue(ConvertPercentToSlopeUnit(percentValue, unit), unit);
    }

    private static string FormatSlopeValue(double value, SlopeAnalyzer.SlopeUnit unit)
    {
        if (double.IsNaN(value))
            return "n/a";

        if (double.IsPositiveInfinity(value))
        {
            return unit switch
            {
                SlopeAnalyzer.SlopeUnit.Degrees => "90.0 deg",
                SlopeAnalyzer.SlopeUnit.Percent => "inf %",
                SlopeAnalyzer.SlopeUnit.Promille => "inf promille",
                SlopeAnalyzer.SlopeUnit.Ratio => "inf",
                _ => "inf"
            };
        }

        return unit switch
        {
            SlopeAnalyzer.SlopeUnit.Percent => $"{value:F1}%",
            SlopeAnalyzer.SlopeUnit.Promille => $"{value:F1} promille",
            SlopeAnalyzer.SlopeUnit.Ratio => $"{value:F3}",
            SlopeAnalyzer.SlopeUnit.Degrees => $"{value:F1} deg",
            _ => value.ToString("F1")
        };
    }

    private static double ConvertPercentToSlopeUnit(double percentValue, SlopeAnalyzer.SlopeUnit unit)
    {
        return ConvertSlopeValue(percentValue, SlopeAnalyzer.SlopeUnit.Percent, unit);
    }

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

    private static double ConvertSlopeValue(double value, SlopeAnalyzer.SlopeUnit fromUnit, SlopeAnalyzer.SlopeUnit toUnit)
    {
        if (fromUnit == toUnit || Math.Abs(value) <= 1e-9)
            return value;

        double ratio = SlopeAnalyzer.ConvertUnitToRatio(value, fromUnit);
        return SlopeAnalyzer.ConvertRatioToUnit(ratio, toUnit);
    }

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

    // ── Drag-and-drop: zone cards ─────────────────────────────────────────

    private void WireZoneCardDragDrop(Control box, Guid terrainId, Guid zoneId)
    {
        box.AllowDrop = true;
        var cardHighlight = UiTheme.DragHighlight;

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverZoneId.HasValue && _zoneCardMap.TryGetValue(_dragOverZoneId.Value, out var prevCard))
                prevCard.BackgroundColor = UiTheme.CardBackground;
            _dragOverZoneId = zoneId;
            if (_zoneCardMap.TryGetValue(zoneId, out var card))
                card.BackgroundColor = cardHighlight;
            ClearAllSepHighlights(_zoneSepMap);
            if (_zoneSepMap.TryGetValue(zoneId, out var sep))
                sep.BackgroundColor = UiTheme.SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverZoneId == zoneId)
            {
                if (_zoneCardMap.TryGetValue(zoneId, out var card))
                    card.BackgroundColor = UiTheme.CardBackground;
                _dragOverZoneId = null;
                ClearAllSepHighlights(_zoneSepMap);
            }
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            var idStr = e.Data.GetString("zone-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            if (_zoneCardMap.TryGetValue(zoneId, out var card))
                card.BackgroundColor = UiTheme.CardBackground;
            _dragOverZoneId = null;
            ClearAllSepHighlights(_zoneSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveZoneToDisplaySeparator(t, sourceId, zoneId));
        };
    }

    private void WireZoneSepDragDrop(Panel outerSep, Panel innerSep, Guid terrainId, Guid insertBeforeId)
    {
        outerSep.AllowDrop = true;

        outerSep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverZoneId.HasValue && _zoneCardMap.TryGetValue(_dragOverZoneId.Value, out var prevCard))
                prevCard.BackgroundColor = UiTheme.CardBackground;
            _dragOverZoneId = null;
            ClearAllSepHighlights(_zoneSepMap);
            innerSep.BackgroundColor = UiTheme.SepHighlight;
        };

        outerSep.DragLeave += (_, _) => innerSep.BackgroundColor = Colors.Transparent;

        outerSep.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            var idStr = e.Data.GetString("zone-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            innerSep.BackgroundColor = Colors.Transparent;
            ClearAllSepHighlights(_zoneSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveZoneToDisplaySeparator(t, sourceId, insertBeforeId));
        };
    }

    private static void ClearAllSepHighlights(Dictionary<Guid, Panel> sepMap)
    {
        foreach (var sep in sepMap.Values)
            sep.BackgroundColor = Colors.Transparent;
    }

    private static void MoveZoneToDisplaySeparator(TerrainDefinition terrain, Guid sourceId, Guid insertBeforeId)
    {
        if (sourceId == insertBeforeId)
            return;

        int fromIdx = terrain.Zones.FindIndex(zone => zone.ZoneId == sourceId);
        if (fromIdx < 0)
            return;

        var item = terrain.Zones[fromIdx];
        terrain.Zones.RemoveAt(fromIdx);

        if (insertBeforeId == Guid.Empty)
        {
            terrain.Zones.Add(item);
            return;
        }

        int insertIdx = terrain.Zones.FindIndex(zone => zone.ZoneId == insertBeforeId);
        terrain.Zones.Insert(insertIdx >= 0 ? insertIdx : terrain.Zones.Count, item);
    }

    private static Color ModifierTypeColor(string kind) => kind switch
    {
        "triangulate"    => Color.FromArgb(25, 118, 210),
        "add-geometry"   => Color.FromArgb(2, 136, 209),
        "remesh"         => Color.FromArgb(56, 142, 60),
        "smooth"         => Color.FromArgb(123, 31, 162),
        "retaining-wall" => Color.FromArgb(230, 74, 25),
        "grade-pad"      => Color.FromArgb(245, 124, 0),
        "grade-path"     => Color.FromArgb(93, 64, 55),
        "in-situ-stair"  => Color.FromArgb(0, 121, 107),
        _                => Color.FromArgb(120, 120, 120)
    };

    private static string GetModifierKind(ModifierDefinition modifier) => modifier switch
    {
        TriangulateModifierDefinition    => "triangulate",
        AddGeometryModifierDefinition    => "add-geometry",
        RemeshModifierDefinition         => "remesh",
        SmoothModifierDefinition         => "smooth",
        RetainingWallModifierDefinition  => "retaining-wall",
        GradePadModifierDefinition       => "grade-pad",
        GradePathModifierDefinition      => "grade-path",
        InSituStairModifierDefinition    => "in-situ-stair",
        _                                => string.Empty
    };

    private static string GetModifierTypeLabel(ModifierDefinition modifier) => modifier switch
    {
        TriangulateModifierDefinition    => "Triangulate",
        AddGeometryModifierDefinition    => "Add Geometry",
        RemeshModifierDefinition         => "Remesh",
        SmoothModifierDefinition         => "Smooth",
        RetainingWallModifierDefinition  => "Retaining Wall",
        GradePadModifierDefinition       => "Grade Pad",
        GradePathModifierDefinition      => "Grade Path",
        InSituStairModifierDefinition    => "In-Situ Stair",
        _                                => "Modifier"
    };

    private static string GetModifierIconName(string kind) => kind switch
    {
        "triangulate" => "ModTriangulate",
        "add-geometry" => "ModAddGeometry",
        "remesh" => "ModRemesh",
        "smooth" => "ModSmooth",
        "retaining-wall" => "ModRetainingWall",
        "grade-pad" => "ModGradePad",
        "grade-path" => "ModGradePath",
        "in-situ-stair" => "ModGradePath",
        _ => "ModTriangulate"
    };

    private static string GetModifierSubtitle(ModifierDefinition modifier, bool isPinnedBaseTriangulate) => modifier switch
    {
        TriangulateModifierDefinition => isPinnedBaseTriangulate ? "Base geometry" : "Terrain geometry",
        AddGeometryModifierDefinition => "Add source geometry",
        RemeshModifierDefinition => "Constraint-preserving remesh",
        SmoothModifierDefinition => "Z-only smoothing",
        RetainingWallModifierDefinition => "Wall breaklines",
        GradePadModifierDefinition => "Pad + daylight grading",
        GradePathModifierDefinition => "Path corridor grading",
        InSituStairModifierDefinition => "Support surface + stair Breps",
        _ => "Modifier"
    };

    private static string GetCollapsedSummary(ModifierDefinition modifier)
    {
        switch (modifier)
        {
            case TriangulateModifierDefinition t:
                int pts = t.Points.ObjectIds.Count + t.Points.LayerPaths.Count;
                int bkl = t.Breaklines.ObjectIds.Count + t.Breaklines.LayerPaths.Count;
                int ctr = t.Contours.ObjectIds.Count + t.Contours.LayerPaths.Count;
                return $"{pts} pts | {bkl} breaks | {ctr} contours";
            case AddGeometryModifierDefinition a:
                int addPts = a.Points.ObjectIds.Count + a.Points.LayerPaths.Count;
                int addBkl = a.Breaklines.ObjectIds.Count + a.Breaklines.LayerPaths.Count;
                int addCtr = a.Contours.ObjectIds.Count + a.Contours.LayerPaths.Count;
                return $"{addPts} pts | {addBkl} breaks | {addCtr} contours";
            case RemeshModifierDefinition r:
                if (r.EdgeLength == 0 && r.MaxArea == 0 && r.MinAngle == 0)
                    return "(defaults)";
                var parts = new System.Collections.Generic.List<string>();
                if (r.EdgeLength > 0) parts.Add($"MaxLen: {r.EdgeLength:G4}");
                if (r.MaxArea > 0) parts.Add($"MaxArea: {r.MaxArea:G4}");
                if (r.MinAngle > 0) parts.Add($"MinAngle: {r.MinAngle:G4} deg");
                return parts.Count > 0 ? string.Join(" | ", parts) : "(defaults)";
            case SmoothModifierDefinition s:
                return $"{s.Iterations} iter | Str {s.Strength:G3}";
            case GradePadModifierDefinition p:
                int bounds = p.Boundaries.ObjectIds.Count + p.Boundaries.LayerPaths.Count;
                return $"{bounds} boundaries | Daylight {p.SlopeAngle:G4} deg";
            case GradePathModifierDefinition path:
                int paths = path.Paths.ObjectIds.Count + path.Paths.LayerPaths.Count;
                return $"{paths} paths | W={path.Width:G4}";
            case RetainingWallModifierDefinition w:
                int curves = w.WallCurves.ObjectIds.Count + w.WallCurves.LayerPaths.Count;
                return $"{curves} curves";
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
        if (terrains.Count == 0)
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
        btn = MakeMiniButton("Browse", (_, _) => ShowSingleLayerPickerPopover(btn!, onPick), toolTip, width: 62);
        btn.ToolTip = toolTip;
        return btn;
    }

    private void ShowLayerSourcePopover(
        Button anchor,
        IReadOnlyList<string> currentLayerPaths,
        Action<string> onAddLayer,
        Action<string> onRemoveLayer)
    {
        ShowLayerPickerPopover(
            anchor,
            currentLayerPaths,
            onAddLayer,
            onRemoveLayer,
            onClear: null,
            preferredSize: new Size(300, 320),
            clearToolTip: string.Empty);
    }

    private void ShowLayerPickerPopover(
        Button anchor,
        IReadOnlyList<string> currentLayerPaths,
        Action<string> onCommitLayer,
        Action<string>? onRemoveLayer,
        Action? onClear,
        Size preferredSize,
        string clearToolTip)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        LayerPickerMode mode = onRemoveLayer == null ? LayerPickerMode.SingleSelect : LayerPickerMode.MultiSelect;
        var popup = CreateLayerPickerPopup(doc);
        var searchBox = new TextBox { PlaceholderText = "Find a layer..." };
        StyleTextBox(searchBox);

        var selectedSet = new HashSet<string>(currentLayerPaths, StringComparer.OrdinalIgnoreCase);
        var availableEntries = doc.Layers
            .Where(layer => !layer.IsDeleted && !selectedSet.Contains(layer.FullPath))
            .Select(layer => new LayerPickerEntry(layer.FullPath, layer.FullPath, ToEtoColor(layer.Color)))
            .ToList();

        var selectedStack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        foreach (var path in currentLayerPaths)
        {
            var capturedPath = path;
            var rhinoLayer = doc.Layers.FirstOrDefault(layer => !layer.IsDeleted &&
                string.Equals(layer.FullPath, path, StringComparison.OrdinalIgnoreCase));
            var removeButton = MakeMiniButton("Remove", (_, _) =>
            {
                onRemoveLayer?.Invoke(capturedPath);
                popup.Close();
            }, "Remove this watched layer.", width: 62);
            selectedStack.Items.Add(new StackLayoutItem(
                CreateLayerPickerRow(
                    GetLeafLayerName(path),
                    rhinoLayer != null ? ToEtoColor(rhinoLayer.Color) : Color.FromArgb(80, 80, 80),
                    highlighted: false,
                    trailingControl: removeButton),
                HorizontalAlignment.Stretch));
        }

        var availableStack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var availableScroll = new Scrollable
        {
            Content = availableStack,
            Border = BorderType.None,
            ExpandContentWidth = true,
            ExpandContentHeight = false
        };

        List<LayerPickerEntry> visibleEntries = new();
        int highlightedIndex = -1;

        void EnsureHighlightedRowVisible()
        {
            if (highlightedIndex < 0)
                return;

            int targetY = Math.Max(0, highlightedIndex * LayerPickerRowHeight - LayerPickerRowHeight);
            availableScroll.ScrollPosition = new Point(0, targetY);
        }

        void CommitLayer(string path)
        {
            onCommitLayer(path);
            popup.Close();
        }

        void RebuildAvailableRows()
        {
            string query = searchBox.Text ?? string.Empty;
            visibleEntries = availableEntries
                .Where(entry => string.IsNullOrWhiteSpace(query) || entry.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (visibleEntries.Count == 0)
            {
                highlightedIndex = -1;
            }
            else if (highlightedIndex < 0)
            {
                highlightedIndex = 0;
            }
            else if (highlightedIndex >= visibleEntries.Count)
            {
                highlightedIndex = visibleEntries.Count - 1;
            }

            availableStack.Items.Clear();
            if (visibleEntries.Count == 0)
            {
                availableStack.Items.Add(new StackLayoutItem(new Panel
                {
                    Padding = new Padding(8, 6),
                    Content = new Label
                    {
                        Text = "No matching layers.",
                        TextColor = UiTheme.MutedText
                    }
                }, HorizontalAlignment.Stretch));
                return;
            }

            for (int index = 0; index < visibleEntries.Count; index++)
            {
                int capturedIndex = index;
                LayerPickerEntry capturedEntry = visibleEntries[index];
                var row = CreateLayerPickerRow(
                    capturedEntry.DisplayText,
                    capturedEntry.DotColor,
                    highlighted: capturedIndex == highlightedIndex);
                row.MouseDown += (_, e) =>
                {
                    if (e.Buttons != MouseButtons.Primary)
                        return;

                    highlightedIndex = capturedIndex;
                    CommitLayer(capturedEntry.Path);
                    e.Handled = true;
                };
                availableStack.Items.Add(new StackLayoutItem(row, HorizontalAlignment.Stretch));
            }
        }

        void MoveHighlight(int delta)
        {
            if (visibleEntries.Count == 0)
                return;

            if (highlightedIndex < 0)
                highlightedIndex = delta >= 0 ? 0 : visibleEntries.Count - 1;
            else
                highlightedIndex = Math.Clamp(highlightedIndex + delta, 0, visibleEntries.Count - 1);

            RebuildAvailableRows();
            EnsureHighlightedRowVisible();
        }

        searchBox.TextChanged += (_, _) =>
        {
            highlightedIndex = 0;
            RebuildAvailableRows();
        };
        searchBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Keys.Escape:
                    popup.Close();
                    e.Handled = true;
                    break;
                case Keys.Down:
                    MoveHighlight(1);
                    e.Handled = true;
                    break;
                case Keys.Up:
                    MoveHighlight(-1);
                    e.Handled = true;
                    break;
                case Keys.Enter:
                    if (highlightedIndex >= 0 && highlightedIndex < visibleEntries.Count)
                    {
                        CommitLayer(visibleEntries[highlightedIndex].Path);
                        e.Handled = true;
                    }
                    break;
            }
        };

        var toolbar = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Padding(4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { new StackLayoutItem(searchBox, expand: true) }
        };
        if (onClear != null)
        {
            toolbar.Items.Add(MakeMiniButton("Clear", (_, _) =>
            {
                onClear();
                popup.Close();
            }, clearToolTip, width: 48));
        }

        var content = new DynamicLayout { DefaultSpacing = new Size(0, 0), Padding = new Padding(0) };
        if (mode == LayerPickerMode.MultiSelect && selectedStack.Items.Count > 0)
        {
            content.Add(selectedStack, yscale: false);
            content.Add(new Panel { Height = 1, BackgroundColor = UiTheme.ToolbarBackground }, yscale: false);
        }
        content.Add(toolbar, yscale: false);
        content.Add(availableScroll, yscale: true);

        popup.Content = content;
        RebuildAvailableRows();
        PositionLayerPickerPopup(popup, anchor, preferredSize);
        popup.Show();
        Application.Instance.AsyncInvoke(() =>
        {
            if (!popup.IsDisposed)
                searchBox.Focus();
        });
    }

    private static StackLayout CreateLayerPickerRow(string text, Color dotColor, bool highlighted, Control? trailingControl = null)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Padding(6, 3),
            BackgroundColor = highlighted ? UiTheme.ListSelectionBackground : Colors.Transparent,
            Items =
            {
                new Panel { Width = 10, Height = 10, BackgroundColor = dotColor },
                new StackLayoutItem(new Label
                {
                    Text = text,
                    VerticalAlignment = VerticalAlignment.Center
                }, expand: true)
            }
        };

        if (trailingControl != null)
            row.Items.Add(trailingControl);

        return row;
    }

    private static Form CreateLayerPickerPopup(RhinoDoc doc)
    {
        var popup = new Form
        {
            ShowInTaskbar = false,
            Resizable = false,
            Minimizable = false,
            Maximizable = false,
            Title = string.Empty,
            Owner = RhinoEtoApp.MainWindowForDocument(doc)
        };
        popup.UseRhinoStyle();
        popup.LostFocus += (_, _) =>
        {
            Application.Instance.AsyncInvoke(() =>
            {
                if (!popup.HasFocus && !popup.IsDisposed)
                    popup.Close();
            });
        };
        return popup;
    }

    private void PositionLayerPickerPopup(Form popup, Button anchor, Size preferredSize)
    {
        var anchorTop = anchor.PointToScreen(PointF.Empty);
        var anchorBottom = anchor.PointToScreen(new PointF(0, anchor.Height));
        Screen screen = Screen.FromPoint(anchorBottom) ?? Screen.PrimaryScreen;
        var workingArea = screen.WorkingArea;
        int areaX = (int)Math.Round(workingArea.X);
        int areaY = (int)Math.Round(workingArea.Y);
        int areaWidth = (int)Math.Round(workingArea.Width);
        int areaHeight = (int)Math.Round(workingArea.Height);

        int maxWidth = Math.Max(120, areaWidth - (LayerPickerMargin * 2));
        int maxHeight = Math.Max(LayerPickerMinHeight, areaHeight - (LayerPickerMargin * 2));
        int width = Math.Min(preferredSize.Width, maxWidth);
        int height = Math.Min(preferredSize.Height, maxHeight);

        int spaceBelow = (areaY + areaHeight) - (int)anchorBottom.Y - LayerPickerMargin;
        int spaceAbove = (int)anchorTop.Y - areaY - LayerPickerMargin;
        bool openAbove = spaceBelow < height && spaceAbove > spaceBelow;
        int availableHeight = openAbove ? spaceAbove : spaceBelow;
        if (availableHeight > 0)
            height = Math.Min(height, Math.Max(LayerPickerMinHeight, availableHeight));
        height = Math.Min(height, maxHeight);

        int x = Math.Clamp((int)anchorBottom.X, areaX + LayerPickerMargin, areaX + areaWidth - width - LayerPickerMargin);
        int y = openAbove
            ? (int)anchorTop.Y - height
            : (int)anchorBottom.Y;
        y = Math.Clamp(y, areaY + LayerPickerMargin, areaY + areaHeight - height - LayerPickerMargin);

        popup.Size = new Size(width, height);
        popup.Location = new Point(x, y);
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

    private static int? ResolveLayerColorArgb(string? layerPath)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null || string.IsNullOrWhiteSpace(layerPath))
            return null;

        int layerIndex = doc.Layers.FindByFullPath(layerPath, -1);
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return null;

        return doc.Layers[layerIndex].Color.ToArgb();
    }

    private static string DescribeTerrainColor(int argb)
    {
        var color = System.Drawing.Color.FromArgb(argb);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2} · {GetOpacityPercent(argb)}%";
    }

    private static Color ToEtoColor(System.Drawing.Color c) => Color.FromArgb(c.R, c.G, c.B, c.A);
}
