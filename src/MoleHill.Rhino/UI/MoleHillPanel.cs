using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

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

    private readonly TerrainController _controller = TerrainController.Instance;
    private readonly TextBox _terrainName = new();
    private readonly CheckBox _liveUpdate = new() { Text = "Live" };
    private readonly Label _statusLabel = new();
    private readonly Label _terrainLayerLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Label _auxLayerLabel     = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Label _statusHintLabel   = new() { VerticalAlignment = VerticalAlignment.Center, TextColor = SystemColors.DisabledText };
    private readonly NumericStepper _toleranceStepper = new();
    private bool _settingsExpanded = true;
    private bool _statusExpanded = false;
    private Panel? _settingsContent;
    private Panel? _statusContent;
    private Button? _settingsChevron;
    private Button? _statusChevron;
    private readonly Button _visibilityButton = new() { Width = 42 };
    private readonly Button _lockButton = new() { Width = 42 };
    private Button _dupButton = new();
    private Button _deleteButton = new();
    private Button _rebuildButton = new();
    private readonly HashSet<Guid> _collapsedModifiers = new();
    private readonly StackLayout _modifierStack = new()
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
    private readonly Dictionary<Guid, Panel>    _modifierCardMap     = new();
    private readonly Dictionary<Guid, Panel>    _modifierSepMap      = new();
    private readonly Dictionary<Guid, Panel>    _modifierStripMap    = new();
    private readonly Dictionary<Guid, Color>    _modifierStripColors = new();
    private readonly Dictionary<Guid, Panel>    _zoneCardMap         = new();
    private readonly Dictionary<Guid, Panel>    _zoneSepMap          = new();
    private readonly Dictionary<Guid, Panel>    _zoneSwatchMap       = new();
    private readonly Dictionary<Guid, Color>    _zoneSwatchColors    = new();
    private Guid? _dragOverModifierId;
    private Guid? _dragOverZoneId;
    private const int PropertyLabelWidth = 92;
    private const int NumericLabelWidth = 110;
    private const int HeaderActionHeight = 22;
    private static readonly Color CardBackground = Color.FromArgb(65, 65, 65);
    private static readonly Color HeaderBackground = Color.FromArgb(56, 56, 56);
    private static readonly Color ToolbarGroupBackground = Color.FromArgb(58, 58, 58);
    private static readonly Color BaseCardBackground = Color.FromArgb(69, 71, 77);
    private static readonly Color DragHighlight = Color.FromArgb(80, 120, 200, 255);
    private static readonly Color SepHighlight  = Color.FromArgb(120, 180, 255, 255);
    private static readonly Color MutedText = Color.FromArgb(168, 168, 168);
    private const int StackedSourceEditorWidth = 430;
    private const int WrappedModifierHeaderWidth = 560;
    private readonly EventHandler _stateChangedHandler;
    private bool _isRefreshing;
    private bool _isPanelLoaded;
    private bool _isStateChangedSubscribed;
    private int _responsiveLayoutKey = -1;

    public MoleHillPanel()
    {
        _stateChangedHandler = HandleControllerStateChanged;

        ApplyHelp(_terrainName, "Terrain name. Commits when you press Enter or leave the field.");
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
        ApplyHelp(_toleranceStepper, "Global Z-snapping tolerance for point deduplication.");
        var toleranceTimer = new UITimer { Interval = 0.25 };
        toleranceTimer.Elapsed += (_, _) =>
        {
            toleranceTimer.Stop();
            MutateSelectedTerrain(t => t.GlobalTolerance = _toleranceStepper.Value, scheduleRebuild: true);
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
            MutateSelectedTerrain(t => t.GlobalTolerance = _toleranceStepper.Value, scheduleRebuild: true);
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
        ApplyHelp(_lockButton, "Lock or unlock all generated terrain outputs.");

        SubscribeControllerStateChanged();
        SizeChanged += HandlePanelSizeChanged;
        LoadComplete += OnPanelLoadComplete;
        UnLoad += OnPanelUnLoad;

        Content = BuildContent();
        _responsiveLayoutKey = GetResponsiveLayoutKey();
        RefreshUi();
    }

    private Control BuildContent()
    {
        Button? pickerButton = null;
        pickerButton = MakeToolbarButton("Menu", (_, _) => ShowTerrainPickerMenu(pickerButton!), "Switch terrain", width: 62);

        var newButton = MakeToolbarButton("New", OnNewTerrain, "Create a new terrain", width: 54);
        _dupButton = MakeToolbarButton("Copy", OnDuplicateTerrain, "Duplicate selected terrain", width: 58);
        _deleteButton = MakeToolbarButton("Delete", OnDeleteTerrain, "Delete selected terrain", width: 66);
        _rebuildButton = MakeToolbarButton("Rebuild", OnRebuildTerrain, "Force rebuild terrain now", width: 72);
        _visibilityButton.ToolTip = "Toggle terrain visibility";
        _lockButton.ToolTip = "Lock terrain to prevent accidental edits";
        _visibilityButton.Width = 66;
        _lockButton.Width = 66;
        _visibilityButton.Height = 26;
        _lockButton.Height = 26;
        _liveUpdate.Height = 26;

        // ── Toolbar (single row) ─────────────────────────────────────
        var identityRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "Terrain", TextColor = MutedText, VerticalAlignment = VerticalAlignment.Center },
                new StackLayoutItem(_terrainName, expand: true),
                pickerButton
            }
        };
        var identityGroup = new Panel
        {
            BackgroundColor = ToolbarGroupBackground,
            Padding = new Padding(8, 6, 8, 6),
            Content = identityRow
        };

        var toolbar = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Padding(8, 8, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new Label { Text = "ACTIVE TERRAIN", TextColor = MutedText },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items =
                    {
                        new StackLayoutItem(identityGroup, expand: true),
                        CreateToolbarGroup(newButton, _dupButton),
                        CreateToolbarGroup(_visibilityButton, _lockButton, _liveUpdate),
                        CreateToolbarGroup(_rebuildButton, _deleteButton)
                    }
                }
            }
        };

        // ── Settings card (collapsible, expanded by default) ─────────
        _settingsChevron = MakeMiniButton("v", (_, _) =>
        {
            _settingsExpanded = !_settingsExpanded;
            _settingsChevron!.Text = _settingsExpanded ? "v" : ">";
            _settingsContent!.Visible = _settingsExpanded;
        }, "Collapse terrain settings", width: 24);

        var settingsHeader = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Padding(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            BackgroundColor = HeaderBackground,
            Items =
            {
                _settingsChevron,
                new Label { Text = "Terrain Settings", Font = new Font(SystemFont.Bold), VerticalAlignment = VerticalAlignment.Center },
                new StackLayoutItem(new Label
                {
                    Text = "Document defaults and output layers",
                    TextColor = MutedText,
                    VerticalAlignment = VerticalAlignment.Center
                }, expand: true)
            }
        };

        var terrainLayerRow = new StackLayout
        {
            Orientation = Orientation.Horizontal, Spacing = 3,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Padding(0, 1),
            Items =
            {
                CreateHelpLabel("Terrain Layer", "Output layer for the main terrain mesh.", PropertyLabelWidth),
                _terrainLayerLabel,
                MakeCompactButton("Use Current", OnAssignTerrainLayer, "Assign the current Rhino layer."),
                MakeLayerPickerButton(path => MutateSelectedTerrain(t => t.TerrainLayerPath = path, scheduleRebuild: false), "Browse and pick the terrain layer"),
                MakeCompactButton("Clear", (_, _) => MutateSelectedTerrain(t => t.TerrainLayerPath = null, scheduleRebuild: false), "Clear the terrain layer assignment.")
            }
        };
        var auxLayerRow = new StackLayout
        {
            Orientation = Orientation.Horizontal, Spacing = 3,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Padding(0, 1),
            Items =
            {
                CreateHelpLabel("Aux Layer", "Output layer for auxiliary geometry like retaining walls.", PropertyLabelWidth),
                _auxLayerLabel,
                MakeCompactButton("Use Current", OnAssignAuxLayer, "Assign the current Rhino layer."),
                MakeLayerPickerButton(path => MutateSelectedTerrain(t => t.AuxiliaryLayerPath = path, scheduleRebuild: true), "Browse and pick the auxiliary layer"),
                MakeCompactButton("Clear", (_, _) => MutateSelectedTerrain(t => t.AuxiliaryLayerPath = null, scheduleRebuild: true), "Clear the auxiliary layer assignment.")
            }
        };
        var toleranceRow = new StackLayout
        {
            Orientation = Orientation.Horizontal, Spacing = 3,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Padding(0, 1),
            Items = { CreateHelpLabel("Tolerance", "Global Z-snapping tolerance for point deduplication.", PropertyLabelWidth), _toleranceStepper }
        };
        var settingsInner = new StackLayout
        {
            Orientation = Orientation.Vertical, Spacing = 6, Padding = new Padding(10, 8, 10, 8),
            Items =
            {
                new StackLayoutItem(terrainLayerRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(auxLayerRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(toleranceRow, HorizontalAlignment.Stretch)
            }
        };

        _settingsContent = new Panel { Content = settingsInner, Visible = _settingsExpanded, BackgroundColor = CardBackground };

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
        _statusChevron = MakeMiniButton(">", (_, _) =>
        {
            _statusExpanded = !_statusExpanded;
            _statusChevron!.Text = _statusExpanded ? "v" : ">";
            _statusContent!.Visible = _statusExpanded;
            _statusHintLabel.Visible = !_statusExpanded;
        }, "Show or hide build status", width: 24);

        var statusHeader = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Padding(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            BackgroundColor = HeaderBackground,
            Items =
            {
                _statusChevron,
                new Label { Text = "Status", Font = new Font(SystemFont.Bold), VerticalAlignment = VerticalAlignment.Center },
                new StackLayoutItem(_statusHintLabel, expand: true)
            }
        };

        _statusContent = new Panel
        {
            Content = new Panel { Content = _statusLabel, Padding = new Padding(10, 6) },
            Visible = _statusExpanded,
            BackgroundColor = CardBackground
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

        var tabs = new TabControl();
        tabs.Pages.Add(new TabPage { Text = "Modifiers", Image = PanelIcons.Load("TabModifiers"), Content = BuildScrollable(_modifierStack) });
        tabs.Pages.Add(new TabPage { Text = "Zones",     Image = PanelIcons.Load("TabZones"),     Content = BuildScrollable(_zonesStack) });
        tabs.Pages.Add(new TabPage { Text = "Markers",   Image = PanelIcons.Load("TabMarkers"),   Content = BuildScrollable(_markerStack) });
        tabs.Pages.Add(new TabPage { Text = "Analysis",  Image = PanelIcons.Load("TabAnalysis"),  Content = BuildScrollable(_analysisStack) });

        var layout = new DynamicLayout();
        layout.Add(top, yscale: false);
        layout.Add(tabs, yscale: true);
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

            RefreshUi();
        });
    }

    private void OnPanelLoadComplete(object? sender, EventArgs e)
    {
        _isPanelLoaded = true;
        SubscribeControllerStateChanged();

        if (!IsDisposed)
            Application.Instance?.AsyncInvoke(RefreshUi);
    }

    private void OnPanelUnLoad(object? sender, EventArgs e)
    {
        _isPanelLoaded = false;
        UnsubscribeControllerStateChanged();
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

        Application.Instance?.AsyncInvoke(RefreshUi);
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
        return key;
    }

    private bool UseStackedSourceEditors() => (_responsiveLayoutKey & 1) != 0;

    private bool UseWrappedModifierActions() => (_responsiveLayoutKey & 2) != 0;

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

    private void OnRebuildTerrain(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.RebuildTerrain(doc, terrain.TerrainId);
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

    private void RefreshUi()
    {
        if (IsDisposed)
            return;

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
                _statusHintLabel.Text = string.Empty;
                _toleranceStepper.Value = 0;
                _visibilityButton.Text = "Shown";
                _lockButton.Text = "Unlocked";
                SetActionButtonsEnabled(false);
                _terrainName.Enabled = false;
                _modifierStack.Items.Clear();
                _zonesStack.Items.Clear();
                _markerStack.Items.Clear();
                _analysisStack.Items.Clear();
                return;
            }

            var terrains = _controller.GetTerrains(doc).ToList();

            var selectedTerrain = _controller.GetSelectedTerrain(doc);
            if (selectedTerrain == null && terrains.Count > 0)
            {
                _controller.SetSelectedTerrain(doc, terrains[0].TerrainId);
                selectedTerrain = terrains[0];
            }

            _terrainName.Text = selectedTerrain?.Name ?? string.Empty;
            _liveUpdate.Checked = selectedTerrain?.LiveUpdateEnabled ?? false;
            _terrainLayerLabel.Text = selectedTerrain?.TerrainLayerPath is { } tl ? GetLeafLayerName(tl) : "(current layer)";
            _auxLayerLabel.Text = selectedTerrain?.AuxiliaryLayerPath is { } al ? GetLeafLayerName(al) : "MoleHill::Auxiliary";
            _toleranceStepper.Value = selectedTerrain?.GlobalTolerance ?? 0;
            var statusText = selectedTerrain?.LastBuildMessage ?? "Create a terrain to start.";
            SetStatusText(statusText);
            _statusHintLabel.Text = statusText.Length > 45 ? statusText[..45] + "..." : statusText;
            _visibilityButton.Text = selectedTerrain?.IsVisible != false ? "Shown" : "Hidden";
            _lockButton.Text = selectedTerrain?.IsLocked == true ? "Locked" : "Unlocked";
            bool hasTerrain = selectedTerrain != null;
            SetActionButtonsEnabled(hasTerrain);
            _terrainName.Enabled = hasTerrain;

            RebuildModifierLayout(selectedTerrain);
            RebuildZonesLayout(selectedTerrain);
            RebuildMarkerLayout(selectedTerrain);
            RebuildAnalysisLayout(selectedTerrain);
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
            var wrapper = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                Padding = new Padding(4, 2),
                BackgroundColor = isPinnedBaseTriangulate ? BaseCardBackground : CardBackground,
                Items = { strip, new StackLayoutItem(box, expand: true) }
            };
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

    private void RebuildZonesLayout(TerrainDefinition? terrain)
    {
        _zoneCardMap.Clear();
        _zoneSepMap.Clear();
        _zoneSwatchMap.Clear();
        _zoneSwatchColors.Clear();
        _zonesStack.Items.Clear();
        _zonesStack.Items.Add(new StackLayoutItem(BuildZonesToolbar(terrain), HorizontalAlignment.Stretch));

        if (terrain == null)
            return;

        _zonesStack.Items.Add(new StackLayoutItem(new Panel
        {
            Padding = new Padding(6, 0, 6, 4),
            Content = new Label
            {
                Text = "Later zones win when priorities tie. Enable Use input Z for planar composition from vertically stacked inputs.",
                TextColor = SystemColors.DisabledText
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

            var box = CreateZoneGroup(terrain, zone);
            _zoneCardMap[zoneId] = box;
            var zoneWrapper = new Panel
            {
                Content = box,
                Padding = new Padding(4, 2),
                BackgroundColor = CardBackground
            };
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
        _analysisStack.Items.Clear();

        if (terrain == null)
            return;

        var editorLayout = new DynamicLayout { DefaultSpacing = new Size(6, 4), Padding = new Padding(6, 4) };
        editorLayout.AddRow(CreateSourceEditor("Compare To", terrain.EarthworkReference,
            apply => MutateSelectedTerrain(item => apply(item.EarthworkReference), scheduleRebuild: true),
            RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion,
            doc => _controller.GetSelectedLayerPaths(doc)));
        editorLayout.AddRow(CreateSourceEditor("Boundary", terrain.EarthworkBoundary,
            apply => MutateSelectedTerrain(item => apply(item.EarthworkBoundary), scheduleRebuild: true),
            RhinoObjectType.Curve,
            doc => _controller.GetSelectedLayerPaths(doc)));
        var localRebuildButton = MakeButton("Rebuild", OnRebuildTerrain, "Force rebuild terrain now");
        editorLayout.AddSeparateRow(localRebuildButton, null);
        _analysisStack.Items.Add(new StackLayoutItem(new GroupBox
        {
            Text = "Earthwork Inputs",
            Content = editorLayout
        }, HorizontalAlignment.Stretch));

        var analysis = terrain.LastAnalysis;
        if (analysis == null)
        {
            _analysisStack.Items.Add(new StackLayoutItem(new Panel
            {
                Padding = new Padding(8),
                Content = new Label
                {
                    Text = "No analysis yet. Rebuild the terrain to populate slope and earthworks.",
                    TextColor = SystemColors.DisabledText
                }
            }, HorizontalAlignment.Stretch));
            return;
        }

        _analysisStack.Items.Add(new StackLayoutItem(CreateAnalysisGroup("Earthworks", new[]
        {
            ("Cut", FormatVolume(analysis.CutVolume)),
            ("Fill", FormatVolume(analysis.FillVolume)),
            ("Net", FormatVolume(analysis.NetVolume)),
            ("Mode", analysis.EarthworkIsEstimated ? "Estimated from terrain delta" : "Exact")
        }), HorizontalAlignment.Stretch));

        _analysisStack.Items.Add(new StackLayoutItem(CreateAnalysisGroup("Slope", new[]
        {
            ("Min", $"{analysis.SlopeMinPercent:F1}%"),
            ("Average", $"{analysis.SlopeAveragePercent:F1}%"),
            ("Max", $"{analysis.SlopeMaxPercent:F1}%"),
            ("Area", $"{analysis.SurfaceArea:F2} sq units")
        }), HorizontalAlignment.Stretch));
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

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Padding(8, 8, 8, 4),
            Items =
            {
                new Label { Text = "MODIFIER STACK", TextColor = MutedText },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Items =
                    {
                        addButton,
                        new StackLayoutItem(new Label
                        {
                            Text = "Base geometry stays pinned at the bottom.",
                            TextColor = MutedText,
                            VerticalAlignment = VerticalAlignment.Center
                        }, expand: true)
                    }
                }
            }
        };
    }

    private Control BuildZonesToolbar(TerrainDefinition? terrain)
    {
        var addButton = new Button
        {
            Text = "+ From Layers"
        };
        ApplyHelp(addButton, "Create one zone per selected Rhino layer. Zone output bakes to Baked<LayerName>.");
        addButton.Enabled = terrain != null;
        addButton.Click += (_, _) =>
        {
            if (terrain == null)
                return;

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            var selectedLayers = _controller.GetSelectedLayerPaths(doc);
            if (selectedLayers.Count == 0)
                return;

            MutateSelectedTerrain(selected =>
            {
                var existing = selected.Zones
                    .Select(zone => zone.Boundaries.LayerPaths.FirstOrDefault())
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

        var showTerrain = new CheckBox
        {
            Text = "Show Terrain",
            Checked = terrain?.ShowTerrainMesh ?? true
        };
        ApplyHelp(showTerrain, "Show or hide the full terrain mesh while keeping the terrain definition intact.");
        showTerrain.CheckedChanged += (_, _) =>
        {
            if (terrain == null)
                return;

            MutateSelectedTerrain(selected => selected.ShowTerrainMesh = showTerrain.Checked == true, scheduleRebuild: false);
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
        };

        var showZones = new CheckBox
        {
            Text = "Show Zones",
            Checked = terrain?.ShowZoneMeshes ?? true
        };
        ApplyHelp(showZones, "Show or hide the split zone meshes independently of the terrain mesh.");
        showZones.CheckedChanged += (_, _) =>
        {
            if (terrain == null)
                return;

            MutateSelectedTerrain(selected => selected.ShowZoneMeshes = showZones.Checked == true, scheduleRebuild: false);
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
        };

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Padding = new Padding(8, 8, 8, 4),
            Spacing = 4,
            Items =
            {
                new Label { Text = "ZONE OUTPUT", TextColor = MutedText },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Items = { addButton, showTerrain, showZones }
                }
            }
        };
    }

    private Panel CreateModifierCard(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        bool collapsed = _collapsedModifiers.Contains(modifier.Id);
        var collapseLabel = new Label
        {
            Text = collapsed ? ">" : "v",
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
        var iconPlate = new Panel
        {
            BackgroundColor = new Color(accent.R, accent.G, accent.B, 0.20f),
            Padding = new Padding(6, 4),
            Content = iconControl
        };

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
                    new Label
                    {
                        Text = GetCollapsedSummary(modifier),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextColor = MutedText
                    }
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
                    new Label
                    {
                        Text = GetModifierSubtitle(modifier, isPinnedBaseTriangulate),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextColor = MutedText
                    }
                }
            };
        }

        bool wrapActions = UseWrappedModifierActions();
        var header = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = wrapActions ? 4 : 0,
            Padding = new Padding(8, 6, 8, 6),
            BackgroundColor = isPinnedBaseTriangulate ? BaseCardBackground : HeaderBackground
        };

        var headerTopRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        headerTopRow.Items.Add(new StackLayoutItem(handle));
        headerTopRow.Items.Add(new StackLayoutItem(collapseLabel));
        headerTopRow.Items.Add(new StackLayoutItem(iconPlate));
        headerTopRow.Items.Add(new StackLayoutItem(enabledCheck));
        headerTopRow.Items.Add(new StackLayoutItem(titleBlock, expand: true));

        if (isPinnedBaseTriangulate)
        {
            headerTopRow.Items.Add(new StackLayoutItem(new Label
            {
                Text = "Pinned",
                TextColor = accent,
                VerticalAlignment = VerticalAlignment.Center
            }));
        }
        else
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

            if (wrapActions)
            {
                header.Items.Add(new StackLayoutItem(headerTopRow, HorizontalAlignment.Stretch));
                var actionRow = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Items =
                    {
                        new StackLayoutItem(new Panel(), expand: true),
                        copyButton,
                        deleteButton
                    }
                };
                header.Items.Add(new StackLayoutItem(actionRow, HorizontalAlignment.Stretch));
            }
            else
            {
                headerTopRow.Items.Add(new StackLayoutItem(copyButton));
                headerTopRow.Items.Add(new StackLayoutItem(deleteButton));
                header.Items.Add(new StackLayoutItem(headerTopRow, HorizontalAlignment.Stretch));
            }
        }
        if (isPinnedBaseTriangulate)
            header.Items.Add(new StackLayoutItem(headerTopRow, HorizontalAlignment.Stretch));

        header.MouseDown += (_, _) =>
        {
            if (_collapsedModifiers.Contains(capturedModifierId))
                _collapsedModifiers.Remove(capturedModifierId);
            else
                _collapsedModifiers.Add(capturedModifierId);

            var doc = RhinoDoc.ActiveDoc;
            RebuildModifierLayout(doc == null ? null : _controller.GetSelectedTerrain(doc));
        };

        var card = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0
        };
        card.Items.Add(new StackLayoutItem(header, HorizontalAlignment.Stretch));

        if (!collapsed)
            card.Items.Add(new StackLayoutItem(CreateModifierBody(terrain, modifier), HorizontalAlignment.Stretch));

        return new Panel
        {
            BackgroundColor = isPinnedBaseTriangulate ? BaseCardBackground : CardBackground,
            Content = card
        };
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
                layout.AddRow(CreateNumericEditor("Iterations", smooth.Iterations, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((SmoothModifierDefinition)item).Iterations = (int)Math.Round(value)),
                    decimalPlaces: 0,
                    help: "How many Z-only smoothing passes to run. Default 1 is light; 2-4 is usually enough; high values will flatten terrain detail."));
                layout.AddRow(CreateNumericEditor("Strength", smooth.Strength, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((SmoothModifierDefinition)item).Strength = value),
                    help: "How strongly each pass moves vertex Z. Default 0.2 is gentle; below 0.1 is subtle; above 0.5 is aggressive. X and Y stay fixed."));
                layout.AddRow(CreateNumericEditor("Fixity", smooth.BreaklineFixity, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((SmoothModifierDefinition)item).BreaklineFixity = value),
                    help: "How strongly breaklines resist smoothing. Default 1.0 locks them hard; 0.5 lets them soften; 0.0 ignores them."));
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
                layout.AddRow(CreateNumericEditor("Tolerance", walls.Tolerance, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((RetainingWallModifierDefinition)item).Tolerance = value),
                    help: "Pairing tolerance for wall curves. Lower values demand cleaner inputs; slightly higher values help catch near-matches."));
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
                    help: "Pad tie-in slope in degrees. Lower values are flatter and extend farther; higher values are steeper and tighter."));
                layout.AddRow(CreateNumericEditor("Max Distance", gradePad.MaxDistance, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((GradePadModifierDefinition)item).MaxDistance = value),
                    help: "Maximum grading reach. 0 means unlimited; smaller values keep the effect close to the pad."));
                layout.AddRow(CreateNumericEditor("Max Area", gradePad.MaxArea, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((GradePadModifierDefinition)item).MaxArea = value),
                    help: "Maximum triangle area for the temporary re-triangulation. Lower values are slower but capture grade transitions better."));
                layout.AddRow(CreateNumericEditor("Min Angle", gradePad.MinAngle, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((GradePadModifierDefinition)item).MinAngle = value),
                    help: "Minimum triangle angle during grading remesh. Moderate values improve quality; very high values can become brittle."));
                break;
            case GradePathModifierDefinition gradePath:
                layout.AddRow(CreateSourceEditor("Paths", gradePath.Paths,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((GradePathModifierDefinition)item).Paths)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
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

    private Panel CreateZoneGroup(TerrainDefinition terrain, CollageZoneDefinition zone)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(4, 4), Padding = new Padding(6, 4) };

        var nameBox = new TextBox { Text = zone.Name };
        ApplyHelp(nameBox, "Friendly zone name shown in the panel and on generated output objects.");
        BindCommittedText(nameBox, () => zone.Name, text =>
            MutateZone(terrain.TerrainId, zone.ZoneId, item => item.Name = text, scheduleRebuild: false));

        var enabledCheck = new CheckBox
        {
            Text = "On",
            Checked = zone.IsEnabled
        };
        ApplyHelp(enabledCheck, "Disable a zone without deleting it.");
        enabledCheck.CheckedChanged += (_, _) =>
            MutateZone(terrain.TerrainId, zone.ZoneId, item => item.IsEnabled = enabledCheck.Checked == true);

        var useInputElevationCheck = new CheckBox
        {
            Text = "Priority by elevation",
            Checked = zone.UseInputElevationForPriority
        };
        ApplyHelp(useInputElevationCheck, "When enabled, zones with higher source geometry win where two zones overlap. Useful when compositing objects at different elevations (e.g., a raised platform on flat terrain).");
        useInputElevationCheck.CheckedChanged += (_, _) =>
            MutateZone(terrain.TerrainId, zone.ZoneId, item => item.UseInputElevationForPriority = useInputElevationCheck.Checked == true);

        var capturedZoneId = zone.ZoneId;
        var capturedTerrainId = terrain.TerrainId;

        var handle = CreateDragHandle();
        ApplyHelp(handle, "Drag to reorder this zone. Later zones win when priorities tie.");
        handle.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary) return;
            var data = new DataObject();
            data.SetString(capturedZoneId.ToString(), "zone-drag");
            handle.DoDragDrop(data, DragEffects.Move);
        };

        int argb = zone.ColorArgb;
        var zoneColor = Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF,
            (int)((uint)argb >> 24));
        var swatch = new Panel { Width = 14, Height = 14, BackgroundColor = zoneColor };
        _zoneSwatchMap[zone.ZoneId] = swatch;
        _zoneSwatchColors[zone.ZoneId] = zoneColor;

        layout.AddSeparateRow(
            handle,
            swatch,
            enabledCheck,
            nameBox,
            MakeMiniButton("Up", (_, _) => MoveZone(capturedTerrainId, capturedZoneId, -1), "Move this zone earlier in the overlap order.", width: 40),
            MakeMiniButton("Down", (_, _) => MoveZone(capturedTerrainId, capturedZoneId, +1), "Move this zone later in the overlap order.", width: 52),
            MakeMiniButton("Delete", (_, _) => RemoveZone(capturedTerrainId, capturedZoneId), "Delete this zone.", width: 58),
            null);
        layout.AddRow(CreateZoneLayerEditor(terrain, zone));
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
                new Label { Text = "MARKERS", TextColor = MutedText },
                new StackLayout
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
                ApplyHelp(formatBox, "Numeric format string for elevation labels. Default F2 gives two decimals.");
                BindCommittedText(formatBox, () => elevation.Format, text =>
                    MutateMarker(terrain.TerrainId, marker.Id, item => ((ElevationMarkerDefinition)item).Format = text, scheduleRebuild: true), trim: false);
                layout.AddSeparateRow(new Label { Text = "Format", Width = 82 }, formatBox, null);
                break;
            case SlopeMarkerDefinition slope:
                var slopeFormatBox = new TextBox { Text = slope.Format };
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
        Func<RhinoDoc, IEnumerable<string>> getLayerPaths)
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
        ApplyHelp(titleLabel, $"{label} accepts Rhino object picks and layers.");

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

    private Control CreateNumericEditor(string label, double value, Action<double> onChanged, int decimalPlaces = 3, string? help = null)
    {
        help ??= GetNumericHelp(label);
        var stepper = new NumericStepper
        {
            Value = value,
            DecimalPlaces = decimalPlaces,
            Increment = decimalPlaces == 0 ? 1 : 0.1,
            MinValue = 0,
            Width = 110
        };
        ApplyHelp(stepper, help);
        var timer = new UITimer { Interval = 0.25 };
        timer.Elapsed += (_, _) =>
        {
            timer.Stop();
            onChanged(stepper.Value);
        };
        stepper.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            timer.Stop();
            timer.Start();
        };
        stepper.LostFocus += (_, _) =>
        {
            timer.Stop();
            onChanged(stepper.Value);
        };
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

    private Control CreateReadOnlyValueRow(string label, string value, string help)
    {
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                new Label
                {
                    Text = value,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextColor = SystemColors.ControlText
                }
            }
        };
    }

    private Control CreateCheckEditor(string label, bool value, Action<bool> onChanged, string help)
    {
        var checkBox = new CheckBox { Checked = value };
        ApplyHelp(checkBox, help);
        checkBox.CheckedChanged += (_, _) => onChanged(checkBox.Checked == true);

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

    private Control CreateZoneLayerEditor(TerrainDefinition terrain, CollageZoneDefinition zone)
    {
        string? layerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        string bakedLayer = string.IsNullOrWhiteSpace(layerPath) ? "None" : $"Baked{GetLeafLayerName(layerPath)}";

        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 1)
        };

        var zoneLayerLabel = new Label
        {
            Text = "Layer",
            Width = PropertyLabelWidth,
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplyHelp(zoneLayerLabel, "Zones are driven by Rhino layers. The baked output layer is generated automatically.");
        row.Items.Add(zoneLayerLabel);

        var assignedLayerLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(layerPath) ? "No layer" : GetLeafLayerName(layerPath),
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplyHelp(assignedLayerLabel, layerPath ?? "No input layer assigned.");
        row.Items.Add(assignedLayerLabel);

        row.Items.Add(MakeCompactButton("Use Current", (_, _) =>
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
        }, "Assign the first selected Rhino layer to this zone."));

        row.Items.Add(MakeLayerPickerButton(path =>
        {
            if (path == null) return;
            MutateZone(terrain.TerrainId, zone.ZoneId, item =>
            {
                item.Boundaries.ObjectIds.Clear();
                item.Boundaries.ReplaceLayers(new[] { path });
                item.Name = GetLeafLayerName(path);
            });
        }, "Browse and pick a layer for this zone."));

        row.Items.Add(MakeCompactButton("Clear", (_, _) =>
        {
            MutateZone(terrain.TerrainId, zone.ZoneId, item =>
            {
                item.Boundaries.ObjectIds.Clear();
                item.Boundaries.ReplaceLayers(Array.Empty<string>());
            });
        }, "Remove the assigned input layer."));

        var bakedLayerLabel = new Label
        {
            Text = $"Bake -> {bakedLayer}",
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = SystemColors.DisabledText
        };
        ApplyHelp(bakedLayerLabel, "Generated zone meshes are placed on this baked layer and inherit that layer's properties.");
        row.Items.Add(bakedLayerLabel);

        return row;
    }

    private Control CreateLayerAssignmentEditor(string label, string? layerPath, Action<string?> onCommit, string help)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 1)
        };

        row.Items.Add(CreateHelpLabel(label, help, PropertyLabelWidth));
        var assignedLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(layerPath) ? "(default)" : layerPath,
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplyHelp(assignedLabel, help);
        row.Items.Add(assignedLabel);

        row.Items.Add(MakeCompactButton("Use Current", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            onCommit(doc?.Layers.CurrentLayer?.FullPath);
        }, "Assign Rhino's current layer."));

        row.Items.Add(MakeLayerPickerButton(path => onCommit(path), "Browse and pick a layer"));

        row.Items.Add(MakeCompactButton("Clear", (_, _) => onCommit(null), "Clear the explicit layer assignment and fall back to the default."));
        return row;
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
            BackgroundColor = Color.FromArgb(55, 55, 55),
            TextColor = Colors.White,
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
            BackgroundColor = ToolbarGroupBackground,
            Padding = new Padding(6, 6, 6, 6),
            Content = row
        };
    }

    private Label CreateHelpLabel(string text, string help, int width)
    {
        var label = new Label { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center };
        ApplyHelp(label, help);
        return label;
    }

    private static string GetNumericHelp(string label)
    {
        return label switch
        {
            "Tolerance" => "Changes are applied after a short pause. 0 uses document tolerance. Lower values are stricter; higher values are more forgiving.",
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

    private void ApplyHelp(Control control, string help)
    {
        control.ToolTip = help;
    }

    private void RestoreStatusText()
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        var text = terrain?.LastBuildMessage ?? "Create a terrain to start.";
        SetStatusText(text);
    }

    private void SetStatusText(string text)
    {
        _statusLabel.Text = text;
        _statusLabel.TextColor = GetStatusColor(text);
    }

    private static Color GetStatusColor(string text)
    {
        if (text.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            return Colors.Red;
        if (text.Contains("Scheduled", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Building", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(200, 120, 0);
        return SystemColors.ControlText;
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        _dupButton.Enabled             = enabled;
        _deleteButton.Enabled          = enabled;
        _rebuildButton.Enabled         = enabled;
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

    private void MutateSelectedTerrain(Action<TerrainDefinition> mutator, bool scheduleRebuild = true)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.MutateTerrain(doc, terrain.TerrainId, mutator, scheduleRebuild);
    }

    private void MutateModifier(Guid terrainId, Guid modifierId, Action<ModifierDefinition> mutator, bool scheduleRebuild = true)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var modifier = terrain.Modifiers.FirstOrDefault(item => item.Id == modifierId);
            if (modifier != null)
                mutator(modifier);
        }, scheduleRebuild);
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
                sep.BackgroundColor = SepHighlight;
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
            innerSep.BackgroundColor = SepHighlight;
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
        var cardHighlight = Color.FromArgb(100, 120, 200, 255);

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverZoneId.HasValue && _zoneSwatchMap.TryGetValue(_dragOverZoneId.Value, out var prevSwatch))
                if (_zoneSwatchColors.TryGetValue(_dragOverZoneId.Value, out var prevColor))
                    prevSwatch.BackgroundColor = prevColor;
            _dragOverZoneId = zoneId;
            if (_zoneSwatchMap.TryGetValue(zoneId, out var swatch))
                swatch.BackgroundColor = cardHighlight;
            ClearAllSepHighlights(_zoneSepMap);
            if (_zoneSepMap.TryGetValue(zoneId, out var sep))
                sep.BackgroundColor = SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverZoneId == zoneId)
            {
                if (_zoneSwatchMap.TryGetValue(zoneId, out var swatch))
                    if (_zoneSwatchColors.TryGetValue(zoneId, out var origColor))
                        swatch.BackgroundColor = origColor;
                _dragOverZoneId = null;
                ClearAllSepHighlights(_zoneSepMap);
            }
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            var idStr = e.Data.GetString("zone-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            if (_zoneSwatchMap.TryGetValue(zoneId, out var swatch))
                if (_zoneSwatchColors.TryGetValue(zoneId, out var origColor))
                    swatch.BackgroundColor = origColor;
            _dragOverZoneId = null;
            ClearAllSepHighlights(_zoneSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t =>
            {
                int fromIdx = t.Zones.FindIndex(z => z.ZoneId == sourceId);
                int toIdx   = t.Zones.FindIndex(z => z.ZoneId == zoneId);
                if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;
                var item = t.Zones[fromIdx];
                t.Zones.RemoveAt(fromIdx);
                if (fromIdx < toIdx) toIdx--;
                t.Zones.Insert(toIdx, item);
            });
        };
    }

    private void WireZoneSepDragDrop(Panel outerSep, Panel innerSep, Guid terrainId, Guid insertBeforeId)
    {
        outerSep.AllowDrop = true;

        outerSep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverZoneId.HasValue && _zoneSwatchMap.TryGetValue(_dragOverZoneId.Value, out var prevSwatch))
                if (_zoneSwatchColors.TryGetValue(_dragOverZoneId.Value, out var prevColor))
                    prevSwatch.BackgroundColor = prevColor;
            _dragOverZoneId = null;
            ClearAllSepHighlights(_zoneSepMap);
            innerSep.BackgroundColor = SepHighlight;
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
            _controller.MutateTerrain(doc, terrainId, t =>
            {
                int fromIdx = t.Zones.FindIndex(z => z.ZoneId == sourceId);
                if (fromIdx < 0) return;
                var item = t.Zones[fromIdx];
                t.Zones.RemoveAt(fromIdx);

                if (insertBeforeId == Guid.Empty)
                {
                    t.Zones.Insert(0, item);
                }
                else
                {
                    int toIdx = t.Zones.FindIndex(z => z.ZoneId == insertBeforeId);
                    t.Zones.Insert(toIdx >= 0 ? toIdx : t.Zones.Count, item);
                }
            });
        };
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
        AddGeometryModifierDefinition => "Adds geometry to the current mesh",
        RemeshModifierDefinition => "Constraint-preserving remesh",
        SmoothModifierDefinition => "Z-only smoothing",
        RetainingWallModifierDefinition => "Hard wall breaklines",
        GradePadModifierDefinition => "Pad and daylight grading",
        GradePathModifierDefinition => "Path corridor grading",
        InSituStairModifierDefinition => "Grades to a support surface and generates stair Breps",
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
                return $"{bounds} boundaries | Slope {p.SlopeAngle:G4} deg";
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

    private void ShowLayerPickerMenu(Button anchor, Action<string?> onPick)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        var menu = new ContextMenu();
        foreach (var layer in doc.Layers)
        {
            if (layer.IsDeleted)
                continue;
            var item = new ButtonMenuItem { Text = layer.FullPath };
            var capturedPath = layer.FullPath;
            item.Click += (_, _) => onPick(capturedPath);
            menu.Items.Add(item);
        }
        menu.Show(anchor);
    }

    private Button MakeLayerPickerButton(Action<string?> onPick, string toolTip = "Browse layers")
    {
        Button? btn = null;
        btn = MakeMiniButton("Browse", (_, _) => ShowLayerPickerMenu(btn!, onPick), toolTip, width: 62);
        btn.ToolTip = toolTip;
        return btn;
    }

    private void ShowLayerSourcePopover(
        Button anchor,
        IReadOnlyList<string> currentLayerPaths,
        Action<string> onAddLayer,
        Action<string> onRemoveLayer)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;

        var popup = new Form
        {
            ShowInTaskbar = false,
            Resizable = false,
            Minimizable = false,
            Maximizable = false,
            Title = string.Empty,
            Size = new Size(280, 320),
            Owner = RhinoEtoApp.MainWindowForDocument(doc)
        };
        popup.UseRhinoStyle();

        // ── Selected layers ───────────────────────────────────────
        var selectedStack = new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 };
        foreach (var path in currentLayerPaths)
        {
            var capturedPath = path;
            var rhinoLayer = doc.Layers.FirstOrDefault(l => !l.IsDeleted &&
                string.Equals(l.FullPath, path, StringComparison.OrdinalIgnoreCase));
            var dotColor = rhinoLayer != null ? ToEtoColor(rhinoLayer.Color) : Color.FromArgb(80, 80, 80);
            var dot = new Panel { Width = 10, Height = 10, BackgroundColor = dotColor };
            var nameLabel = new Label { Text = GetLeafLayerName(path), VerticalAlignment = VerticalAlignment.Center };
            var removeBtn = new Button { Text = "Remove", Width = 62, Height = 20 };
            removeBtn.Click += (_, _) => { onRemoveLayer(capturedPath); popup.Close(); };
            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(6, 2),
                Items = { dot, new StackLayoutItem(nameLabel, expand: true), removeBtn }
            };
            selectedStack.Items.Add(new StackLayoutItem(row, HorizontalAlignment.Stretch));
        }

        // ── Search box ────────────────────────────────────────────
        var searchBox = new TextBox { PlaceholderText = "Find an option..." };

        // ── Available layers ──────────────────────────────────────
        var availableStack = new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 };
        var currentSet = new HashSet<string>(currentLayerPaths, StringComparer.OrdinalIgnoreCase);
        var availableEntries = new List<(StackLayout row, string path)>();
        foreach (var layer in doc.Layers)
        {
            if (layer.IsDeleted || currentSet.Contains(layer.FullPath))
                continue;
            var capturedPath = layer.FullPath;
            var dot = new Panel { Width = 10, Height = 10, BackgroundColor = ToEtoColor(layer.Color) };
            var nameLabel = new Label { Text = layer.FullPath, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(6, 3),
                Items = { dot, new StackLayoutItem(nameLabel, expand: true) }
            };
            row.MouseDown += (_, e) =>
            {
                if (e.Buttons != MouseButtons.Primary) return;
                onAddLayer(capturedPath);
                popup.Close();
            };
            availableStack.Items.Add(new StackLayoutItem(row, HorizontalAlignment.Stretch));
            availableEntries.Add((row, capturedPath));
        }

        searchBox.TextChanged += (_, _) =>
        {
            var q = searchBox.Text ?? string.Empty;
            foreach (var (row, path) in availableEntries)
                row.Visible = string.IsNullOrEmpty(q) || path.Contains(q, StringComparison.OrdinalIgnoreCase);
        };
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Escape) popup.Close();
        };

        // ── Popup layout ──────────────────────────────────────────
        var innerLayout = new DynamicLayout { DefaultSpacing = new Size(0, 0), Padding = new Padding(0) };
        if (selectedStack.Items.Count > 0)
        {
            innerLayout.Add(selectedStack, yscale: false);
            innerLayout.Add(new Panel { Height = 1, BackgroundColor = SystemColors.ControlBackground }, yscale: false);
        }
        innerLayout.Add(new Panel { Content = searchBox, Padding = new Padding(4) }, yscale: false);
        innerLayout.Add(new Scrollable
        {
            Content = availableStack,
            Border = BorderType.None,
            ExpandContentWidth = true,
            ExpandContentHeight = false
        }, yscale: true);

        popup.Content = innerLayout;

        var pt = anchor.PointToScreen(new PointF(0, anchor.Height));
        popup.Location = new Point((int)pt.X, (int)pt.Y);
        popup.Show();
    }

    private static Color ToEtoColor(System.Drawing.Color c) => Color.FromArgb(c.R, c.G, c.B);
}
