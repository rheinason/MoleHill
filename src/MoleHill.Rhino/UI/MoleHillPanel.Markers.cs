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
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;

namespace MoleHill.Rhino.UI;

// Markers tab: marker add buttons, marker editor groups, and marker mutations.
public sealed partial class MoleHillPanel
{
    private void RebuildMarkerLayout(TerrainDefinition? terrain)
    {
        _markerStack.Items.Clear();
        if (terrain == null)
            return;

        _markerStack.Items.Add(new StackLayoutItem(BuildMarkerAddButtons(terrain), HorizontalAlignment.Stretch));
        foreach (var marker in terrain.Markers)
            _markerStack.Items.Add(new StackLayoutItem(CreateMarkerGroup(terrain, marker), HorizontalAlignment.Stretch));
    }

    private Control BuildMarkerAddButtons(TerrainDefinition terrain)
    {
        var buttons = new List<Control>();
        foreach (var descriptor in MarkerTypeRegistry.Markers)
        {
            string kind = descriptor.Kind;
            buttons.Add(MakeButton(descriptor.AddButtonText, (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                    return;

                _controller.AddMarker(doc, terrain.TerrainId, kind);
                RebuildMarkerLayout(_controller.GetSelectedTerrain(doc));
            }, descriptor.AddButtonHelp));
        }

        var buttonRow = new AdaptiveControlGroup(4, buttons.ToArray());

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Padding(8, 8, 8, 4),
            Items =
            {
                new Label { Text = "MARKERS", TextColor = UiTheme.MutedText },
                buttonRow
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
                var formatDropDown = CreateValueFormatDropDown(
                    elevation.Format,
                    format => MutateMarker(terrain.TerrainId, marker.Id, item => ((ElevationMarkerDefinition)item).Format = format, scheduleRebuild: true),
                    "Number of decimal places shown in elevation marker labels.");
                layout.AddSeparateRow(new Label { Text = "Decimals", Width = UiMetrics.ShortLabel }, formatDropDown, null);
                break;
            case SlopeMarkerDefinition slope:
                var slopeFormatDropDown = CreateValueFormatDropDown(
                    slope.Format,
                    format => MutateMarker(terrain.TerrainId, marker.Id, item => ((SlopeMarkerDefinition)item).Format = format, scheduleRebuild: true),
                    "Number of decimal places shown in slope marker labels.");
                var percentCheck = new CheckBox { Text = "Percent", Checked = slope.AsPercent };
                percentCheck.CheckedChanged += (_, _) => MutateMarker(terrain.TerrainId, marker.Id, item => ((SlopeMarkerDefinition)item).AsPercent = percentCheck.Checked == true, scheduleRebuild: true);
                layout.AddSeparateRow(new Label { Text = "Decimals", Width = UiMetrics.ShortLabel }, slopeFormatDropDown, percentCheck, null);
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
        layout.AddSeparateRow(new Label { Text = "Block Def", Width = UiMetrics.ShortLabel }, blockNameBox, null);
        var scaleRow = CreateNumericEditor("Symbol Scale", marker.BlockScale, value =>
            MutateMarker(terrain.TerrainId, marker.Id, item => item.BlockScale = value),
            help: "Scale factor for the block-instance marker symbol. 1.0 is the default; below 1.0 is smaller; above 1.0 is larger.");
        scaleRow.Enabled = marker.UseBlockInstance;
        blockCheck.CheckedChanged += (_, _) => scaleRow.Enabled = blockCheck.Checked == true;
        layout.AddRow(scaleRow);

        return layout;
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
}
