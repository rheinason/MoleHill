using Eto.Forms;
using MoleHill.Core.Scattering;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.UI;

// Scatter's section layout, zone selection, density display and completed-result quantities.
public sealed partial class MoleHillPanel
{
    private readonly Dictionary<(Guid Card, string Section), bool> _scatterSectionExpansion = new();

    private void BuildScatterSections(DynamicLayout layout, TerrainDefinition terrain, ScatterObjectDefinition scatter,
        IReadOnlyList<ParameterDescriptor<TerrainObjectDefinition>> parameters)
    {
        string Section(string key) => key switch
        {
            "blocks" => "Block Mix",
            "sourceMode" or "scatterSource" or "useTerrainZones" or "zoneSelection" or "pattern" or
                "densityMode" or "densityUnit" or "perAreaDensity" or "spacing" or "edgeGap" or "count" or "alongJitter" or "blockOrder" => "Distribution",
            "exclusions" or "exclusionClearance" or "boundarySetback" or "boundaryFeather" or "keepWholeBlocksInside" or
                "slopeFilterEnabled" or "slopeMinDegrees" or "slopeMaxDegrees" or "elevationFilterEnabled" or "elevationMin" or "elevationMax" => "Filters",
            "previewMode" or "previewCap" => "Preview",
            _ => "Placement"
        };
        foreach (string section in new[] { "Distribution", "Block Mix", "Placement", "Filters", "Preview" })
        {
            var content = UiLayouts.CardBody();
            foreach (var parameter in parameters.Where(parameter => Section(parameter.Key) == section))
                if (parameter.VisibleWhen == null || parameter.VisibleWhen(scatter))
                    content.AddRow(BuildObjectSchemaRow(terrain, scatter, parameter));
            var key = (scatter.Id, section);
            bool expanded = _scatterSectionExpansion.TryGetValue(key, out bool saved) ? saved : section is "Distribution" or "Block Mix";
            string header = section;
            string? filterSummary = null;
            if (section == "Filters")
            {
                var active = new List<string>();
                if (scatter.Exclusions.HasReferences) active.Add("Exclusions");
                if (scatter.BoundarySetback > 0) active.Add("Setback");
                if (scatter.BoundaryFeather > 0) active.Add("Feather");
                if (scatter.KeepWholeBlocksInside) active.Add("Whole blocks");
                if (scatter.SlopeFilterEnabled) active.Add("Slope");
                if (scatter.ElevationFilterEnabled) active.Add("Elevation");
                header += active.Count > 0 ? $" · {active.Count} active" : " · None";
                if (active.Count > 0) filterSummary = string.Join(", ", active);
            }
            var expander = new Expander { Header = header, Content = content, Expanded = expanded };
            expander.ExpandedChanged += (_, _) =>
            {
                _scatterSectionExpansion[key] = expander.Expanded;
            };
            layout.AddRow(expander);
            if (filterSummary != null)
                layout.AddRow(new Label { Text = filterSummary, Wrap = WrapMode.Word, TextColor = UiTheme.MutedText });
        }

        var doc = RhinoDoc.ActiveDoc;
        ScatterBuildSummary? summary = doc == null ? null : _controller.GetScatterSummaries(doc, terrain.TerrainId)
            .FirstOrDefault(item => item.DefinitionId == scatter.Id);
        string requested = scatter.DensityMode == ScatterDensityMode.Count ? $"Requested {scatter.Count:N0} · " : string.Empty;
        layout.AddRow(CreateReadOnlyValueRow("Result", summary == null ? "Build to update quantities" :
            summary.Message ?? $"{requested}{summary.Sampled:N0} samples → {summary.Placed:N0} placed",
            "Counts come from the current completed build. Filters remove samples; they do not refill the requested count."));
        if (summary != null && summary.Message == null)
            layout.AddRow(new Expander
            {
                Header = "Why samples were removed",
                Content = new Label { Text = summary.Breakdown, Wrap = WrapMode.Word },
                Expanded = summary.Placed == 0
            });
        layout.AddRow(MakeInlineButton("Bake this scatter", (_, _) =>
        {
            if (RhinoDoc.ActiveDoc is { } activeDoc)
                _controller.BakeScatter(activeDoc, terrain.TerrainId, scatter.Id);
        }, "Bake the full result for this card. Replace Previous Bakes replaces only this card's tracked instances."));
    }

    private Control CreateScatterZoneSelector(TerrainDefinition terrain, ScatterObjectDefinition scatter)
    {
        var layout = UiLayouts.CardBody();
        foreach (var zone in terrain.Zones)
        {
            Guid id = zone.ZoneId;
            var check = new CheckBox { Text = zone.IsEnabled ? zone.Name : zone.Name + " (disabled)",
                Checked = scatter.ZoneIds.Contains(id), Enabled = zone.IsEnabled };
            check.CheckedChanged += (_, _) => MutateObjectDefinition(terrain.TerrainId, scatter.Id, item =>
            {
                var target = (ScatterObjectDefinition)item;
                if (check.Checked == true && !target.ZoneIds.Contains(id)) target.ZoneIds.Add(id);
                else if (check.Checked != true) target.ZoneIds.Remove(id);
            });
            layout.AddRow(check);
        }
        int missing = scatter.ZoneIds.Count(id => terrain.Zones.All(zone => zone.ZoneId != id));
        if (missing > 0)
            layout.AddRow(new Label { Text = $"{missing} selected zone(s) no longer exist.", Wrap = WrapMode.Word });
        if (terrain.Zones.Count == 0)
            layout.AddRow(new Label { Text = "Add terrain zones first.", Wrap = WrapMode.Word });
        return layout;
    }

    private Control CreateScatterDensityEditor(TerrainDefinition terrain, ScatterObjectDefinition scatter,
        ParameterDescriptor<TerrainObjectDefinition> parameter)
    {
        var doc = RhinoDoc.ActiveDoc;
        ModelUnitContext units = doc == null ? default : ModelUnitContext.FromDocument(doc);
        double area = scatter.DensityUnit switch
        {
            ScatterDensityUnit.SquareMeter => units.FromSquareMeters(1),
            ScatterDensityUnit.HundredSquareMeters => units.FromSquareMeters(100),
            ScatterDensityUnit.Hectare => units.FromSquareMeters(10000),
            _ => 1
        };
        if (!(area > 0) || !double.IsFinite(area)) area = 1;
        string suffix = scatter.DensityUnit switch
        {
            ScatterDensityUnit.SquareMeter => "plants/m²",
            ScatterDensityUnit.HundredSquareMeters => "plants/100 m²",
            ScatterDensityUnit.Hectare => "plants/ha",
            _ => $"plants/{units.Abbreviation}²"
        };
        var numeric = CreateNumericEditor("Density", scatter.PerAreaDensity * area,
            value => MutateObjectDefinition(terrain.TerrainId, scatter.Id,
                item => ((ScatterObjectDefinition)item).PerAreaDensity = value / area,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            decimalPlaces: 4, help: parameter.Help, minValue: 0, liveEdit: true, unitSuffix: suffix);
        ScatterBuildSummary? summary = doc == null ? null : _controller.GetScatterSummaries(doc, terrain.TerrainId).FirstOrDefault(item => item.DefinitionId == scatter.Id);
        return new StackLayout
        {
            Orientation = Orientation.Vertical, HorizontalContentAlignment = HorizontalAlignment.Stretch, Spacing = 3,
            Items = { numeric, new Label { Text = summary?.RegionArea is double regionArea
                ? $"Gross-area estimate: {Math.Round(scatter.PerAreaDensity * regionArea):N0} before filters"
                : "Build to estimate the planting area.", Wrap = WrapMode.Word, TextColor = UiTheme.MutedText } }
        };
    }
}
