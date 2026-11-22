using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.UI;

// Zones tab: zone toolbar/card/body builders, layer assignment, color override, and reordering.
public sealed partial class MoleHillPanel
{
    private void RebuildZonesLayout(TerrainDefinition? terrain)
    {
        _zoneCards.Clear(terrain?.TerrainId ?? Guid.Empty);
        _zoneCards.Add(BuildZonesToolbar(terrain));
        if (terrain == null)
            return;

        _zoneCards.AddNote("Later zones win. Use input Z when stacked inputs should resolve by elevation.");
        foreach (var zone in terrain.Zones)
            _zoneCards.AddCard(zone.ZoneId, CreateZoneCard(terrain, zone), UiTheme.ZoneStripColor);

        _zoneCards.AddTail();
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
                            Name = AnalysisFormatting.GetLeafLayerName(layerPath),
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
                            Name = AnalysisFormatting.GetLeafLayerName(layerPath),
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
            Spacing = UiMetrics.SpaceLarge,
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
        Guid terrainId = terrain.TerrainId, id = zone.ZoneId;

        return CreateCard(new CardSpec
        {
            Stack = _zoneCards,
            Id = id,
            Name = zone.Name,
            NameHelp = "Friendly zone name shown in the panel and on generated output objects.",
            Rename = text => MutateZone(terrainId, id, item => item.Name = text, scheduleRebuild: false),
            IsEnabled = zone.IsEnabled,
            EnableHelp = "Disable a zone without deleting it.",
            SetEnabled = value => MutateZone(terrainId, id, item => item.IsEnabled = value),
            TypeName = "Zone",
            TypeIcon = CreateZoneColorSwatch(terrain, zone),
            Summary = GetZoneCollapsedSummary(zone),
            Description = zone.UseInputElevationForPriority ? "Higher inputs win overlaps" : "Later zones win ties",
            HandleHelp = "Drag to reorder this zone. Later zones win when priorities tie.",
            Status = CreateCardStatusLabel(zone.IsEnabled ? "Enabled" : "Disabled"),
            Delete = (() => RemoveZone(terrainId, id), "Delete this zone."),
            Body = () => CreateZoneBody(terrain, zone)
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
            RhinoObjectType.Curve | RhinoObjectType.Hatch,
            doc => _controller.GetSelectedLayerPaths(doc),
            "Assign the closed curves, hatches and layers that define this zone's area."));
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
        int argb = AnalysisFormatting.ResolveLayerColorArgb(layerPath) ?? unchecked((int)0xFFB4B4B4);
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
            Padding = new Padding(UiMetrics.SpaceHairline),
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
                : AnalysisFormatting.ResolveLayerColorArgb(zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))) ?? unchecked((int)0xFF808080);
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
            Spacing = UiMetrics.SpaceMedium,
            Padding = new Padding(UiMetrics.SpaceLarge),
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
            parts.Add(AnalysisFormatting.GetLeafLayerName(layerPath));

        parts.Add(zone.UseInputElevationForPriority ? "Elevation priority" : "Stack order");
        parts.Add($"{CountReferences(zone.Boundaries)} refs");
        return string.Join(" | ", parts);
    }
}
