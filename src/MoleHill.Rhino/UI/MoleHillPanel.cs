using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.UI;

[System.Runtime.InteropServices.Guid("E8A65B83-74A6-4325-B66D-D7005A4A8257")]
public sealed class MoleHillPanel : Panel
{
    private static readonly (string Label, string Kind)[] ModifierKinds =
    {
        ("Triangulate", "triangulate"),
        ("Remesh", "remesh"),
        ("Smooth", "smooth"),
        ("Retaining Wall", "retaining-wall"),
        ("Grade Pad", "grade-pad"),
        ("Grade Path", "grade-path")
    };

    private readonly TerrainController _controller = TerrainController.Instance;
    private readonly DropDown _terrainDropDown = new();
    private readonly TextBox _terrainName = new();
    private readonly CheckBox _liveUpdate = new() { Text = "Live update" };
    private readonly Label _statusLabel = new();
    private readonly Label _terrainLayerLabel = new();
    private readonly Label _auxLayerLabel = new();
    private readonly Button _visibilityButton = new() { Width = 42 };
    private readonly Button _lockButton = new() { Width = 42 };
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
    private readonly Dictionary<Guid, GroupBox> _modifierCardMap = new();
    private readonly Dictionary<Guid, Panel>    _modifierSepMap  = new();
    private readonly Dictionary<Guid, GroupBox> _zoneCardMap     = new();
    private readonly Dictionary<Guid, Panel>    _zoneSepMap      = new();
    private Guid? _dragOverModifierId;
    private Guid? _dragOverZoneId;
    private static readonly Color DragHighlight = Color.FromArgb(80, 120, 200, 255);
    private static readonly Color SepHighlight  = Color.FromArgb(120, 180, 255, 255);
    private bool _isRefreshing;

    public MoleHillPanel()
    {
        _terrainDropDown.SelectedIndexChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            var terrains = _controller.GetTerrains(doc);
            if (_terrainDropDown.SelectedIndex >= 0 && _terrainDropDown.SelectedIndex < terrains.Count)
                _controller.SetSelectedTerrain(doc, terrains[_terrainDropDown.SelectedIndex].TerrainId);

            RefreshUi();
        };

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
        ApplyHelp(_terrainDropDown, "Choose the active MoleHill terrain in this document.");
        ApplyHelp(_visibilityButton, "Hide or show all generated terrain outputs.");
        ApplyHelp(_lockButton, "Lock or unlock all generated terrain outputs.");

        _controller.StateChanged += (_, _) => Application.Instance?.AsyncInvoke(RefreshUi);

        Content = BuildContent();
        RefreshUi();
    }

    private Control BuildContent()
    {
        var top = new DynamicLayout { DefaultSpacing = new Size(4, 4), Padding = new Padding(6, 6, 6, 2) };
        top.AddSeparateRow(
            _terrainDropDown,
            MakeButton("New", OnNewTerrain, "Create a new managed MoleHill terrain, seeded from the current Rhino selection when possible."),
            MakeButton("Dup", OnDuplicateTerrain, "Duplicate this terrain definition and its modifier stack."),
            _visibilityButton,
            _lockButton,
            MakeButton("Bake", OnBakeTerrain, "Copy the current generated result into plain Rhino geometry and keep the MoleHill terrain."),
            MakeIconButton("🗑", OnDeleteTerrain, "Delete this managed terrain and its generated outputs."));
        top.AddSeparateRow(
            _terrainName,
            MakeButton("Rebuild", OnRebuildTerrain, "Rebuild this terrain now, even if live update is paused."),
            MakeButton("Detach", OnConvertTerrain, "Keep the generated Rhino objects, but remove this terrain from MoleHill management."));
        top.AddSeparateRow(
            CreateHelpLabel("Terrain Layer", "Layer used for the main terrain mesh. If you move the terrain mesh to another Rhino layer, MoleHill will track that change.", 86),
            _terrainLayerLabel,
            MakeButton("Use Current", OnAssignTerrainLayer, "Assign the current Rhino layer to the terrain mesh output."),
            CreateHelpLabel("Aux Layer", "Layer used for generated wall and auxiliary geometry.", 70),
            _auxLayerLabel,
            MakeButton("Use Current", OnAssignAuxLayer, "Assign the current Rhino layer to auxiliary outputs like retaining walls."));
        top.AddSeparateRow(_liveUpdate, _statusLabel, null);

        var tabs = new TabControl();
        tabs.Pages.Add(new TabPage { Text = "Modifiers", Content = BuildScrollable(_modifierStack) });
        tabs.Pages.Add(new TabPage { Text = "Zones", Content = BuildScrollable(_zonesStack) });
        tabs.Pages.Add(new TabPage { Text = "Markers", Content = BuildScrollable(_markerStack) });
        tabs.Pages.Add(new TabPage { Text = "Analysis", Content = BuildScrollable(_analysisStack) });

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
        var doc = RhinoDoc.ActiveDoc;
        _isRefreshing = true;
        try
        {
            if (doc == null)
            {
                _terrainDropDown.DataStore = Array.Empty<object>();
                _terrainName.Text = string.Empty;
                _liveUpdate.Checked = false;
                _statusLabel.Text = "No active Rhino document.";
                _terrainLayerLabel.Text = "-";
                _auxLayerLabel.Text = "-";
                _visibilityButton.Text = "👁";
                _lockButton.Text = "🔓";
                _modifierStack.Items.Clear();
                _zonesStack.Items.Clear();
                _markerStack.Items.Clear();
                _analysisStack.Items.Clear();
                return;
            }

            var terrains = _controller.GetTerrains(doc).ToList();
            _terrainDropDown.DataStore = terrains.Select(t => t.Name).Cast<object>().ToList();

            var selectedTerrain = _controller.GetSelectedTerrain(doc);
            if (selectedTerrain == null && terrains.Count > 0)
            {
                _controller.SetSelectedTerrain(doc, terrains[0].TerrainId);
                selectedTerrain = terrains[0];
            }

            _terrainDropDown.SelectedIndex = selectedTerrain == null
                ? -1
                : terrains.FindIndex(t => t.TerrainId == selectedTerrain.TerrainId);

            _terrainName.Text = selectedTerrain?.Name ?? string.Empty;
            _liveUpdate.Checked = selectedTerrain?.LiveUpdateEnabled ?? false;
            _terrainLayerLabel.Text = selectedTerrain?.TerrainLayerPath ?? "(current layer)";
            _auxLayerLabel.Text = selectedTerrain?.AuxiliaryLayerPath ?? "MoleHill::Auxiliary";
            _statusLabel.Text = selectedTerrain?.LastBuildMessage ?? "Create a terrain to start.";
            _visibilityButton.Text = selectedTerrain?.IsVisible != false ? "👁" : "⊘";
            _lockButton.Text = selectedTerrain?.IsLocked == true ? "🔒" : "🔓";

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
        _modifierStack.Items.Clear();
        _modifierStack.Items.Add(new StackLayoutItem(BuildAddModifierBar(terrain), HorizontalAlignment.Stretch));

        if (terrain == null)
            return;

        var terrainId = terrain.TerrainId;
        foreach (var modifier in Enumerable.Reverse(terrain.Modifiers))
        {
            var modifierId = modifier.Id;

            var sep = new Panel { Height = 6, BackgroundColor = Colors.Transparent };
            ApplyHelp(sep, "Drop here to reorder modifiers.");
            _modifierSepMap[modifierId] = sep;
            WireModifierSepDragDrop(sep, terrainId, modifierId);
            _modifierStack.Items.Add(new StackLayoutItem(sep, HorizontalAlignment.Stretch));

            var box = CreateModifierCard(terrain, modifier);
            _modifierCardMap[modifierId] = box;
            _modifierStack.Items.Add(new StackLayoutItem(box, HorizontalAlignment.Stretch));
        }

        var tailSep = new Panel { Height = 6, BackgroundColor = Colors.Transparent };
        ApplyHelp(tailSep, "Drop here to reorder modifiers.");
        _modifierSepMap[Guid.Empty] = tailSep;
        WireModifierSepDragDrop(tailSep, terrainId, Guid.Empty);
        _modifierStack.Items.Add(new StackLayoutItem(tailSep, HorizontalAlignment.Stretch));
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
                Text = "Later zones win when priorities tie. Enable Use input Z for planar composition from vertically stacked inputs.",
                TextColor = SystemColors.DisabledText
            }
        }, HorizontalAlignment.Stretch));

        var terrainId = terrain.TerrainId;
        foreach (var zone in terrain.Zones)
        {
            var zoneId = zone.ZoneId;

            var sep = new Panel { Height = 6, BackgroundColor = Colors.Transparent };
            ApplyHelp(sep, "Drop here to reorder zones.");
            _zoneSepMap[zoneId] = sep;
            WireZoneSepDragDrop(sep, terrainId, zoneId);
            _zonesStack.Items.Add(new StackLayoutItem(sep, HorizontalAlignment.Stretch));

            var box = CreateZoneGroup(terrain, zone);
            _zoneCardMap[zoneId] = box;
            _zonesStack.Items.Add(new StackLayoutItem(box, HorizontalAlignment.Stretch));
        }

        var tailSep = new Panel { Height = 6, BackgroundColor = Colors.Transparent };
        ApplyHelp(tailSep, "Drop here to reorder zones.");
        _zoneSepMap[Guid.Empty] = tailSep;
        WireZoneSepDragDrop(tailSep, terrainId, Guid.Empty);
        _zonesStack.Items.Add(new StackLayoutItem(tailSep, HorizontalAlignment.Stretch));
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
            doc => _controller.GetSelectedMeshObjectIds(doc),
            doc => _controller.GetSelectedLayerPaths(doc)));
        editorLayout.AddRow(CreateSourceEditor("Boundary", terrain.EarthworkBoundary,
            apply => MutateSelectedTerrain(item => apply(item.EarthworkBoundary), scheduleRebuild: true),
            doc => _controller.GetSelectedCurveObjectIds(doc),
            doc => _controller.GetSelectedLayerPaths(doc)));
        _analysisStack.Items.Add(new StackLayoutItem(new GroupBox
        {
            Text = "Inputs",
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
        var addButton = new Button { Text = "+ Add Modifier" };
        if (terrain != null)
        {
            var menu = new ContextMenu();
            foreach (var (label, kind) in ModifierKinds)
            {
                var item = new ButtonMenuItem { Text = label };
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
            Orientation = Orientation.Horizontal,
            Padding = new Padding(6, 4),
            Items = { addButton }
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
            Orientation = Orientation.Horizontal,
            Padding = new Padding(6, 4),
            Spacing = 8,
            Items = { addButton, showTerrain, showZones }
        };
    }

    private GroupBox CreateModifierCard(TerrainDefinition terrain, ModifierDefinition modifier)
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

        var nameLabel = new Label
        {
            Text = modifier.Label,
            Font = new Font(SystemFont.Bold),
            VerticalAlignment = VerticalAlignment.Center
        };

        var capturedModifierId = modifier.Id;
        var capturedTerrainId = terrain.TerrainId;

        var handle = new Label
        {
            Text = "\u22EE\u22EE",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 16,
            Cursor = Cursors.Move
        };
        ApplyHelp(handle, "Drag to reorder this modifier.");
        handle.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary) return;
            var data = new DataObject();
            data.SetString(capturedModifierId.ToString(), "modifier-drag");
            handle.DoDragDrop(data, DragEffects.Move);
        };

        var header = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Padding(6, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                handle,
                collapseLabel,
                enabledCheck,
                new StackLayoutItem(nameLabel, expand: true),
                MakeIconButton("^", (_, _) => MoveModifier(capturedTerrainId, capturedModifierId, +1), "Move this modifier up in the stack."),
                MakeIconButton("v", (_, _) => MoveModifier(capturedTerrainId, capturedModifierId, -1), "Move this modifier down in the stack."),
                MakeIconButton("Dup", (_, _) =>
                {
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc != null)
                        _controller.DuplicateModifier(doc, capturedTerrainId, capturedModifierId);
                }, "Duplicate this modifier."),
                MakeIconButton("🗑", (_, _) =>
                {
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc != null)
                        _controller.RemoveModifier(doc, capturedTerrainId, capturedModifierId);
                }, "Delete this modifier.")
            }
        };

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

        return new GroupBox { Content = card };
    }

    private Control CreateModifierBody(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 4), Padding = new Padding(6, 4) };

        switch (modifier)
        {
            case TriangulateModifierDefinition triangulate:
                layout.AddRow(CreateSourceEditor("Points", triangulate.Points,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((TriangulateModifierDefinition)item).Points)),
                    doc => _controller.GetSelectedPointObjectIds(doc),
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Breaklines", triangulate.Breaklines,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((TriangulateModifierDefinition)item).Breaklines)),
                    doc => _controller.GetSelectedCurveObjectIds(doc),
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateNumericEditor("Tolerance", triangulate.Tolerance, value =>
                    MutateModifier(terrain.TerrainId, modifier.Id, item => ((TriangulateModifierDefinition)item).Tolerance = value),
                    help: "Triangulation cleanup tolerance. Default 0 uses Rhino document tolerance. Very low keeps tiny detail; higher values merge near-duplicate points and gaps more aggressively."));
                break;
            case RemeshModifierDefinition remesh:
                layout.AddRow(CreateSourceEditor("Constraints", remesh.Constraints,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((RemeshModifierDefinition)item).Constraints)),
                    doc => _controller.GetSelectedCurveObjectIds(doc),
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
                    doc => _controller.GetSelectedCurveObjectIds(doc),
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Breaklines", smooth.Breaklines,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((SmoothModifierDefinition)item).Breaklines)),
                    doc => _controller.GetSelectedCurveObjectIds(doc),
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
                    doc => _controller.GetSelectedCurveObjectIds(doc),
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
                    doc => _controller.GetSelectedCurveObjectIds(doc),
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Lock Curves", gradePad.LockCurves,
                    apply => MutateModifier(terrain.TerrainId, modifier.Id, item => apply(((GradePadModifierDefinition)item).LockCurves)),
                    doc => _controller.GetSelectedCurveObjectIds(doc),
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
                    doc => _controller.GetSelectedCurveObjectIds(doc),
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
        }

        return layout;
    }

    private GroupBox CreateZoneGroup(TerrainDefinition terrain, CollageZoneDefinition zone)
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
            Text = "Use input Z",
            Checked = zone.UseInputElevationForPriority
        };
        ApplyHelp(useInputElevationCheck, "When enabled, higher planar inputs win where zones overlap in top view.");
        useInputElevationCheck.CheckedChanged += (_, _) =>
            MutateZone(terrain.TerrainId, zone.ZoneId, item => item.UseInputElevationForPriority = useInputElevationCheck.Checked == true);

        var capturedZoneId = zone.ZoneId;
        var capturedTerrainId = terrain.TerrainId;

        var handle = new Label
        {
            Text = "\u22EE\u22EE",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 16,
            Cursor = Cursors.Move
        };
        ApplyHelp(handle, "Drag to reorder this zone. Later zones win when priorities tie.");
        handle.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary) return;
            var data = new DataObject();
            data.SetString(capturedZoneId.ToString(), "zone-drag");
            handle.DoDragDrop(data, DragEffects.Move);
        };

        layout.AddSeparateRow(
            handle,
            enabledCheck,
            nameBox,
            MakeCompactButton("^", (_, _) => MoveZone(capturedTerrainId, capturedZoneId, -1), "Move this zone earlier in the overlap order."),
            MakeCompactButton("v", (_, _) => MoveZone(capturedTerrainId, capturedZoneId, +1), "Move this zone later in the overlap order."),
            MakeCompactButton("🗑", (_, _) => RemoveZone(capturedTerrainId, capturedZoneId), "Delete this zone."),
            null);
        layout.AddRow(CreateZoneLayerEditor(terrain, zone));
        layout.AddSeparateRow(useInputElevationCheck, null);

        return new GroupBox { Text = zone.Name, Content = layout };
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
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Padding(6, 4),
            Items =
            {
                elevationButton,
                slopeButton
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
            enabledCheck,
            MakeCompactButton("🗑", (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                    _controller.RemoveMarker(doc, terrain.TerrainId, marker.Id);
            }),
            null);

        layout.AddRow(CreateSourceEditor("Sources", marker.Sources,
            apply => MutateMarker(terrain.TerrainId, marker.Id, item => apply(item.Sources)),
            doc => _controller.GetSelectedPointObjectIds(doc).Concat(_controller.GetSelectedCurveObjectIds(doc)),
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
        layout.AddRow(CreateNumericEditor("Symbol Scale", marker.BlockScale, value =>
            MutateMarker(terrain.TerrainId, marker.Id, item => item.BlockScale = value),
            help: "Scale factor for the block-instance marker symbol. 1.0 is the default; below 1.0 is smaller; above 1.0 is larger."));

        return new GroupBox { Text = marker.Name, Content = layout };
    }

    private Control CreateSourceEditor(
        string label,
        SourceReferenceSet sourceSet,
        Action<Action<SourceReferenceSet>> mutateSourceSet,
        Func<RhinoDoc, IEnumerable<Guid>> getObjectIds,
        Func<RhinoDoc, IEnumerable<string>> getLayerPaths)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 1)
        };

        var titleLabel = new Label { Text = label, Width = 82, VerticalAlignment = VerticalAlignment.Center };
        ApplyHelp(titleLabel, $"{label} accepts Rhino object picks and selected layers.");
        row.Items.Add(titleLabel);

        if (sourceSet.ObjectIds.Count > 0)
            row.Items.Add(new Label { Text = $"{sourceSet.ObjectIds.Count} obj", VerticalAlignment = VerticalAlignment.Center });

        row.Items.Add(MakeCompactButton("+Sel", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                mutateSourceSet(set => set.ReplaceObjects(getObjectIds(doc)));
        }, "Use the current Rhino selection for this input."));

        row.Items.Add(MakeCompactButton("-Sel", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            var ids = getObjectIds(doc).ToList();
            mutateSourceSet(set => set.RemoveObjects(ids));
        }, "Remove the current Rhino selection from this input."));

        foreach (var layerPath in sourceSet.LayerPaths)
        {
            var capturedPath = layerPath;
            var display = layerPath.Contains("::", StringComparison.Ordinal)
                ? layerPath[(layerPath.LastIndexOf("::", StringComparison.Ordinal) + 2)..]
                : layerPath;
            row.Items.Add(MakeCompactButton($"{display} x", (_, _) =>
                mutateSourceSet(set => set.RemoveLayer(capturedPath)), $"Remove layer '{capturedPath}' from this input."));
        }

        row.Items.Add(MakeCompactButton("+Layers", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                mutateSourceSet(set => set.ReplaceLayers(getLayerPaths(doc)));
        }, "Use the selected Rhino layers for this input."));

        return row;
    }

    private Control CreateNumericEditor(string label, double value, Action<double> onChanged, int decimalPlaces = 3, string? help = null)
    {
        help ??= GetNumericHelp(label);
        var stepper = new NumericStepper
        {
            Value = value,
            DecimalPlaces = decimalPlaces,
            Increment = decimalPlaces == 0 ? 1 : 0.1,
            MinValue = 0
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
                CreateHelpLabel(label, help, 110),
                stepper
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
            Width = 82,
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

        row.Items.Add(MakeCompactButton("Use Layers", (_, _) =>
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

        row.Items.Add(CreateHelpLabel(label, help, 82));
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

        row.Items.Add(MakeCompactButton("Use Layers", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            onCommit(_controller.GetSelectedLayerPaths(doc).FirstOrDefault());
        }, "Assign the first selected Rhino layer."));

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

    private static Button MakeIconButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null)
    {
        var button = new Button { Text = text, Width = 34, Height = 22 };
        if (!string.IsNullOrWhiteSpace(toolTip))
            button.ToolTip = toolTip;
        button.Click += onClick;
        return button;
    }

    private static Button MakeCompactButton(string text, EventHandler<EventArgs> onClick, string? toolTip = null)
    {
        var button = new Button { Text = text, Height = 22 };
        if (!string.IsNullOrWhiteSpace(toolTip))
            button.ToolTip = toolTip;
        button.Click += onClick;
        return button;
    }

    private Label CreateHelpLabel(string text, string help, int width)
    {
        var label = new Label { Text = text, Width = width };
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
        _statusLabel.Text = terrain?.LastBuildMessage ?? "Create a terrain to start.";
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

    private void WireModifierCardDragDrop(GroupBox box, Guid terrainId, Guid modifierId)
    {
        box.AllowDrop = true;

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverModifierId.HasValue && _modifierCardMap.TryGetValue(_dragOverModifierId.Value, out var prev))
                prev.BackgroundColor = Colors.Transparent;
            _dragOverModifierId = modifierId;
            box.BackgroundColor = DragHighlight;
            ClearAllSepHighlights(_modifierSepMap);
            if (_modifierSepMap.TryGetValue(modifierId, out var sep))
                sep.BackgroundColor = SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverModifierId == modifierId)
            {
                box.BackgroundColor = Colors.Transparent;
                _dragOverModifierId = null;
                ClearAllSepHighlights(_modifierSepMap);
            }
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            var idStr = e.Data.GetString("modifier-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            box.BackgroundColor = Colors.Transparent;
            _dragOverModifierId = null;
            ClearAllSepHighlights(_modifierSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t =>
            {
                int fromIdx = t.Modifiers.FindIndex(m => m.Id == sourceId);
                int toIdx   = t.Modifiers.FindIndex(m => m.Id == modifierId);
                if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;
                var item = t.Modifiers[fromIdx];
                t.Modifiers.RemoveAt(fromIdx);
                if (fromIdx < toIdx) toIdx--;
                t.Modifiers.Insert(toIdx, item);
            });
        };
    }

    private void WireModifierSepDragDrop(Panel sep, Guid terrainId, Guid insertBeforeId)
    {
        sep.AllowDrop = true;

        sep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverModifierId.HasValue && _modifierCardMap.TryGetValue(_dragOverModifierId.Value, out var prev))
                prev.BackgroundColor = Colors.Transparent;
            _dragOverModifierId = null;
            ClearAllSepHighlights(_modifierSepMap);
            sep.BackgroundColor = SepHighlight;
        };

        sep.DragLeave += (_, _) => sep.BackgroundColor = Colors.Transparent;

        sep.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            var idStr = e.Data.GetString("modifier-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            sep.BackgroundColor = Colors.Transparent;
            ClearAllSepHighlights(_modifierSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t =>
            {
                int fromIdx = t.Modifiers.FindIndex(m => m.Id == sourceId);
                if (fromIdx < 0) return;
                var item = t.Modifiers[fromIdx];
                t.Modifiers.RemoveAt(fromIdx);

                if (insertBeforeId == Guid.Empty)
                {
                    // tail separator: move to beginning of array (bottom of reversed display)
                    t.Modifiers.Insert(0, item);
                }
                else
                {
                    int toIdx = t.Modifiers.FindIndex(m => m.Id == insertBeforeId);
                    t.Modifiers.Insert(toIdx >= 0 ? toIdx : t.Modifiers.Count, item);
                }
            });
        };
    }

    // ── Drag-and-drop: zone cards ─────────────────────────────────────────

    private void WireZoneCardDragDrop(GroupBox box, Guid terrainId, Guid zoneId)
    {
        box.AllowDrop = true;

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverZoneId.HasValue && _zoneCardMap.TryGetValue(_dragOverZoneId.Value, out var prev))
                prev.BackgroundColor = Colors.Transparent;
            _dragOverZoneId = zoneId;
            box.BackgroundColor = DragHighlight;
            ClearAllSepHighlights(_zoneSepMap);
            if (_zoneSepMap.TryGetValue(zoneId, out var sep))
                sep.BackgroundColor = SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverZoneId == zoneId)
            {
                box.BackgroundColor = Colors.Transparent;
                _dragOverZoneId = null;
                ClearAllSepHighlights(_zoneSepMap);
            }
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            var idStr = e.Data.GetString("zone-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            box.BackgroundColor = Colors.Transparent;
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

    private void WireZoneSepDragDrop(Panel sep, Guid terrainId, Guid insertBeforeId)
    {
        sep.AllowDrop = true;

        sep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverZoneId.HasValue && _zoneCardMap.TryGetValue(_dragOverZoneId.Value, out var prev))
                prev.BackgroundColor = Colors.Transparent;
            _dragOverZoneId = null;
            ClearAllSepHighlights(_zoneSepMap);
            sep.BackgroundColor = SepHighlight;
        };

        sep.DragLeave += (_, _) => sep.BackgroundColor = Colors.Transparent;

        sep.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("zone-drag")) return;
            var idStr = e.Data.GetString("zone-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            sep.BackgroundColor = Colors.Transparent;
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
}
