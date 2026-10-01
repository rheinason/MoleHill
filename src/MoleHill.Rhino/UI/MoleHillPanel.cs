using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.UI;

// The MoleHill dock panel: its controls and card state (fields every partial reads), construction, the
// controller subscription, and RefreshUi. Everything else lives in a partial named for what it builds:
// Toolbar, Settings, Status and Tabs for the fixed sections; Modifiers, Objects, Zones, Analysis and
// Annotations for the tabs; Cards, Schema, Editors, Controls, Colors, ColorRamp, LayerPickers and
// DragDrop for shared building blocks; Mutations and Actions for what edits the terrain. Add a new
// section or tab as a new partial rather than growing this file.
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
    private readonly EventHandler _stateChangedHandler;
    private readonly EventHandler _statusChangedHandler;
    private bool _statusRefreshPosted;
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

    public MoleHillPanel()
    {
        _stateChangedHandler = HandleControllerStateChanged;
        _statusChangedHandler = HandleControllerStatusChanged;

        WireToolbarControls();
        WireSettingsControls();

        SubscribeControllerStateChanged();
        LoadComplete += OnPanelLoadComplete;
        UnLoad += OnPanelUnLoad;
        RhinoApp.AppSettingsChanged += OnAppSettingsChanged;

        Content = BuildContent();
        RefreshUi();
    }

    private Control BuildContent()
    {
        var toolbar = BuildTerrainToolbar();
        var settingsCard = BuildSettingsCard();
        var statusCard = BuildStatusCard();

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

        var tabsContainer = BuildTabs();

        var layout = new DynamicLayout();
        layout.Add(top, yscale: false);
        layout.Add(tabsContainer, yscale: true);
        return layout;
    }

    private void SubscribeControllerStateChanged()
    {
        if (_isStateChangedSubscribed)
            return;

        _controller.StateChanged += _stateChangedHandler;
        _controller.StatusChanged += _statusChangedHandler;
        _isStateChangedSubscribed = true;
    }

    private void UnsubscribeControllerStateChanged()
    {
        if (!_isStateChangedSubscribed)
            return;

        _controller.StateChanged -= _stateChangedHandler;
        _controller.StatusChanged -= _statusChangedHandler;
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

    /// <summary>
    /// Only the build status changed, so only the status line is updated: the cards show nothing that
    /// changes when a rebuild is scheduled or starts. A full refresh relaid the visible card stack out
    /// (45 ms on a 100k-face terrain's wall edit) on the UI thread while the rebuild finished behind it.
    /// Skipped when a full refresh is already on its way, which updates the status too.
    /// </summary>
    private void HandleControllerStatusChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !_isPanelLoaded || _statusRefreshPosted || _controllerRefreshPosted)
            return;

        _statusRefreshPosted = true;
        Application.Instance?.AsyncInvoke(() =>
        {
            _statusRefreshPosted = false;
            if (IsDisposed || !_isPanelLoaded || _controllerRefreshPosted || RhinoDoc.ActiveDoc is not { } doc)
                return;

            TerrainDefinition? selected = _controller.GetSelectedTerrain(doc);
            if (selected != null && MoleHill.Shared.ModelUnitContext.FromDocument(doc).IsSupported)
                SetStatusText(selected.LastBuildMessage ?? string.Empty, selected.LastStructuredDiagnostics);
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

    /// <summary>
    /// A begin/end pair for one control that may see several "end" events for a single "begin" (Enter
    /// then LostFocus, MouseUp then LostFocus). Only the end matching this control's own begin releases
    /// the shared deferral, so a stray extra end cannot close another control's gesture mid-drag.
    /// </summary>
    private (Action Begin, Action End) CreatePairedRefreshDeferral()
    {
        bool isDeferring = false;
        return (
            () =>
            {
                if (isDeferring)
                    return;
                isDeferring = true;
                BeginControllerRefreshDeferral();
            },
            () =>
            {
                if (!isDeferring)
                    return;
                isDeferring = false;
                EndControllerRefreshDeferral();
            });
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
}
