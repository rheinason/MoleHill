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

// Zones tab: zone toolbar/card/body builders, layer assignment, color override, and reordering.
public sealed partial class MoleHillPanel
{
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
            var zoneStrip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = UiTheme.ZoneStripColor };
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

    private Control BuildZonesToolbar(TerrainDefinition? terrain)
    {
        var addButton = MakeToolbarButton("Add Zone", (_, _) => { }, "Add a zone definition.");
        ApplyHelp(addButton, "Add a blank zone, pick layers from a list, or create zones from the layers selected in Rhino's Layers panel.");
        addButton.Enabled = terrain != null;
        if (terrain != null)
        {
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

            var addFromModalItem = new ButtonMenuItem { Text = "From Layers..." };
            addFromModalItem.Click += (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                    return;

                var selectedTerrain = _controller.GetSelectedTerrain(doc);
                var used = (selectedTerrain?.Zones ?? Enumerable.Empty<CollageZoneDefinition>())
                    .SelectMany(zone => zone.Boundaries.LayerPaths)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var chosen = ZoneLayerPickerDialog.ShowDialog(doc, used);
                if (chosen == null || chosen.Count == 0)
                    return;

                MutateSelectedTerrain(selected =>
                {
                    var existing = selected.Zones
                        .SelectMany(zone => zone.Boundaries.LayerPaths)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var layerPath in chosen)
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
            menu.Items.Add(addFromModalItem);

            var addFromLayersItem = new ButtonMenuItem { Text = "From Selected Layers" };
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

    private Panel CreateZoneCard(TerrainDefinition terrain, CollageZoneDefinition zone)
    {
        bool collapsed = _collapsedZones.Contains(zone.ZoneId);
        var collapseLabel = CreateCollapseChevron(collapsed);

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

        var handle = CreateReorderHandle(capturedZoneId, "zone-drag", "Drag to reorder this zone. Later zones win when priorities tie.");

        var iconPlate = CreateZoneColorSwatch(terrain, zone);

        Control titleBlock = CreateCardTitleBlock(
            collapsed,
            zone.Name,
            GetZoneCollapsedSummary(zone),
            nameBox,
            zone.UseInputElevationForPriority
                ? "Higher inputs win overlaps"
                : "Later zones win ties");

        var badge = CreateCardStatusLabel(zone.IsEnabled ? "Enabled" : "Disabled");

        var deleteButton = MakeDeleteIconButton(
            () => RemoveZone(capturedTerrainId, capturedZoneId),
            "Delete this zone.");
        void ToggleCollapsed(bool ctrlHeld) => ToggleCardCollapsed(
            _collapsedZones,
            capturedZoneId,
            ctrlHeld,
            t => t.Zones.Select(item => item.ZoneId),
            RebuildZonesLayout);

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = new Control[] { badge },
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
        var layout = UiLayouts.CardBody();
        var useInputElevationCheck = new CheckBox
        {
            Text = "Priority by elevation",
            Checked = zone.UseInputElevationForPriority
        };
        ApplyHelp(useInputElevationCheck, "When enabled, zones with higher source geometry win where two zones overlap. Useful when compositing objects at different elevations (e.g., a raised platform on flat terrain).");
        useInputElevationCheck.CheckedChanged += (_, _) =>
            MutateZone(terrain.TerrainId, zone.ZoneId, item => item.UseInputElevationForPriority = useInputElevationCheck.Checked == true);

        AddZoneSummaryRows(layout, terrain, zone);

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

    private void AddZoneSummaryRows(DynamicLayout layout, TerrainDefinition terrain, CollageZoneDefinition zone)
    {
        layout.AddSeparateRow(new Label
        {
            Text = "LAST BUILD",
            TextColor = UiTheme.MutedText,
            Font = new Font(SystemFont.Bold)
        }, null);

        var doc = RhinoDoc.ActiveDoc;
        ZoneAnalysisSummary? summary = doc == null
            ? null
            : _controller.GetZoneAnalysisResults(doc, terrain.TerrainId)
                .FirstOrDefault(item => item.ZoneId == zone.ZoneId);
        if (summary == null)
        {
            layout.AddRow(CreateSelectableSummaryEditor(
                "Summary",
                "Rebuild required",
                "Zone quantities are populated after the last completed final build.",
                minHeight: 42));
            return;
        }

        if (summary.OutputCount == 0)
        {
            layout.AddRow(CreateReadOnlyValueRow(
                "Summary",
                "No resolved output",
                "The zone has no mesh output in the last completed build."));
            return;
        }

        layout.AddRow(CreateReadOnlyValueRow("Plan area", FormatZoneArea(summary.PlanArea), "Projected XY area of the resolved zone output."));
        layout.AddRow(CreateReadOnlyValueRow("Surface area", FormatZoneArea(summary.SurfaceArea), "3D surface area of the resolved zone output."));
        layout.AddRow(CreateReadOnlyValueRow(
            "Elevation",
            $"{FormatZoneLength(summary.ElevationMinZ)} / {FormatZoneLength(summary.ElevationAverageZ)} / {FormatZoneLength(summary.ElevationMaxZ)}",
            "Minimum / area-weighted average / maximum elevation."));
        layout.AddRow(CreateReadOnlyValueRow(
            "Slope",
            $"{summary.SlopeMinPercent:F1}% / {summary.SlopeAveragePercent:F1}% / {summary.SlopeMaxPercent:F1}%",
            "Minimum / area-weighted average / maximum slope."));
        layout.AddRow(CreateReadOnlyValueRow(
            "Mesh output",
            $"{summary.OutputCount:N0} object(s) · {summary.TriangleCount:N0} triangle(s)",
            "Resolved zone mesh output from the last completed build."));

        string earthworkText;
        if (summary.HasEarthwork)
        {
            earthworkText = $"Cut {FormatVolume(summary.CutVolume)} / Fill {FormatVolume(summary.FillVolume)} / Net {FormatVolume(summary.NetVolume)}{(summary.EarthworkIsEstimated ? " (estimated)" : "") }";
        }
        else
        {
            bool earthworkEnabled = terrain.Analyses.OfType<EarthworkAnalysisDefinition>().Any(item => item.IsEnabled);
            earthworkText = earthworkEnabled
                ? "Unavailable — zone has no resolved output in the last build"
                : "Unavailable — enable Earthworks analysis";
        }
        layout.AddRow(CreateSelectableSummaryEditor(
            "Earthworks",
            earthworkText,
            "Cut/fill is measured against the enabled Earthworks reference and uses the resolved zone output."));
    }

    private static string FormatZoneArea(double value)
    {
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        if (!unitContext.IsSupported)
            unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
        return unitContext.FormatArea(value);
    }

    private static string FormatZoneLength(double value)
    {
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        if (!unitContext.IsSupported)
            unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
        return unitContext.FormatLength(value);
    }

    /// <summary>The zone's resolved display color: the override when set, else the first source layer's
    /// color, else a neutral grey. Mirrors the build-time color resolution so the card matches output.</summary>
    private static Color ResolveZoneSwatchColor(CollageZoneDefinition zone)
    {
        if (zone.UseColorOverride)
            return ToEtoColor(System.Drawing.Color.FromArgb(zone.ColorArgb));

        string? layerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        int argb = ResolveLayerColorArgb(layerPath) ?? unchecked((int)0xFFB4B4B4);
        return ToEtoColor(System.Drawing.Color.FromArgb(argb));
    }

    private Control CreateZoneColorSwatch(TerrainDefinition terrain, CollageZoneDefinition zone)
    {
        var capturedTerrainId = terrain.TerrainId;
        var capturedZoneId = zone.ZoneId;

        var swatch = new Panel
        {
            Width = 26,
            Height = 18,
            BackgroundColor = ResolveZoneSwatchColor(zone)
        };
        var bordered = new Panel
        {
            Padding = new Padding(1),
            BackgroundColor = UiTheme.MutedText,
            Content = swatch
        };
        ApplyHelp(bordered, zone.UseColorOverride
            ? "Zone color override. Click to change it or revert to the source-layer color."
            : "Zone color, taken from its source layer. Click to set an override color.");
        swatch.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                ShowZoneColorPopup(swatch, capturedTerrainId, capturedZoneId);
        };
        return bordered;
    }

    private void ShowZoneColorPopup(Control anchor, Guid terrainId, Guid zoneId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        var zone = _controller.GetSelectedTerrain(doc)?.Zones.FirstOrDefault(item => item.ZoneId == zoneId);
        if (zone == null)
            return;

        var popup = CreateLayerPickerPopup(doc);
        var preview = new Panel { Height = 22, BackgroundColor = ResolveZoneSwatchColor(zone) };

        void ApplyOverride()
        {
            int initialArgb = zone.UseColorOverride
                ? zone.ColorArgb
                : ResolveLayerColorArgb(zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))) ?? unchecked((int)0xFF808080);
            var colorDialog = new ColorDialog { Color = ToEtoColor(System.Drawing.Color.FromArgb(initialArgb)) };
            if (colorDialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok)
                return;

            MutateZone(terrainId, zoneId, item =>
            {
                item.UseColorOverride = true;
                item.ColorArgb = ToArgb(colorDialog.Color);
            }, scheduleRebuild: true);
            if (!popup.IsDisposed)
                popup.Close();
            RebuildZonesLayout(_controller.GetSelectedTerrain(doc));
        }

        void UseSourceLayer()
        {
            MutateZone(terrainId, zoneId, item => item.UseColorOverride = false, scheduleRebuild: true);
            if (!popup.IsDisposed)
                popup.Close();
            RebuildZonesLayout(_controller.GetSelectedTerrain(doc));
        }

        var chooseButton = MakeInlineButton("Choose Color...", (_, _) => ApplyOverride(), "Pick an explicit override color for this zone.");
        var byLayerButton = MakeInlineButton("Use Source Layer", (_, _) => UseSourceLayer(), "Clear the override and color this zone by its source layer.");

        popup.Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Padding = new Padding(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(preview, HorizontalAlignment.Stretch),
                new StackLayoutItem(chooseButton, HorizontalAlignment.Stretch),
                new StackLayoutItem(byLayerButton, HorizontalAlignment.Stretch)
            }
        };
        PositionLayerPickerPopup(popup, anchor, new Size(200, 130));
        popup.Show();
    }

    private Control CreateZoneLayerEditor(TerrainDefinition terrain, CollageZoneDefinition zone)
    {
        string? layerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        string bakedLayer = TerrainBuildService.GetBakedLayerPath(layerPath) ?? "None";

        var assignedLayerLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(layerPath) ? "No layer" : EllipsizeText(GetLeafLayerName(layerPath), 18),
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.None
        };
        ApplyHelp(assignedLayerLabel, layerPath ?? "No input layer assigned.");
        var useCurrentButton = MakeInlineButton("Current", (_, _) =>
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

        var clearButton = MakeInlineButton("Clear", (_, _) =>
        {
            MutateZone(terrain.TerrainId, zone.ZoneId, item =>
            {
                item.Boundaries.ObjectIds.Clear();
                item.Boundaries.ReplaceLayers(Array.Empty<string>());
            });
        }, "Remove the assigned input layer.");
        var bakedLayerLabel = new Label
        {
            Text = EllipsizeText($"Bake -> {bakedLayer}", 22),
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = UiTheme.MutedText,
            Wrap = WrapMode.None,
            ToolTip = $"Bake -> {bakedLayer}"
        };
        ApplyHelp(bakedLayerLabel, "Generated zone meshes preview using the source layer color and bake under this output layer.");
        var buttonRow = CreateResponsiveControlGroup(4, useCurrentButton, browseButton, clearButton);

        var editor = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(assignedLayerLabel, expand: true),
                buttonRow,
                bakedLayerLabel
            }
        };
        return new PropertyRow(
            CreateHelpLabel("Layer", "Zones are driven by Rhino layers. The baked output layer is generated automatically.", 0),
            editor,
            expandWidget: true);
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
}
