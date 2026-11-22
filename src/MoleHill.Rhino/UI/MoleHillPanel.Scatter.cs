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

    private void BuildScatterSections(DynamicLayout layout, TerrainDefinition terrain, ScatterObjectDefinition scatter,
        IReadOnlyList<ParameterDescriptor<TerrainObjectDefinition>> parameters)
    {
        // Sections are declared by the Scatter descriptor (Distribution, Block Mix, Placement, Filters, Preview).
        string kind = ObjectTypeRegistry.ForType(scatter.GetType())?.Kind ?? "scatter";
        AddSchemaRows(layout, kind, (TerrainObjectDefinition)scatter, parameters,
            parameter => BuildObjectSchemaRow(terrain, scatter, parameter));

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
            Orientation = Orientation.Vertical, HorizontalContentAlignment = HorizontalAlignment.Stretch, Spacing = UiMetrics.SpaceSmall,
            Items = { numeric, new Label { Text = summary?.RegionArea is double regionArea
                ? $"Gross-area estimate: {Math.Round(scatter.PerAreaDensity * regionArea):N0} before filters"
                : "Build to estimate the planting area.", Wrap = WrapMode.Word, TextColor = UiTheme.MutedText } }
        };
    }
}
