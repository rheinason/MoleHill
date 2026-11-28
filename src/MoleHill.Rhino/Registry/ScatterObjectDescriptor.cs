using MoleHill.Core.Scattering;
using MoleHill.Rhino.Model;
using ObjectParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.TerrainObjectDefinition>;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal sealed class ScatterObjectDescriptor : ObjectTypeDescriptor
{
    public override string Kind => "scatter";
    public override Type DefinitionType => typeof(ScatterObjectDefinition);
    public override string DisplayName => "Scatter";
    public override string IconLabel => "S";
    public override string? IconName => "ObjScatter";
    public override string Subtitle => "Scatter blocks across boundaries";
    public override int AccentArgb => unchecked((int)0xFF8E44AD); // 142,68,173
    public override int SortOrder => 2;
    public override TerrainObjectDefinition Create() => new ScatterObjectDefinition();

    public override IReadOnlyList<ObjectParam> Parameters { get; } = BuildParameters();

    private static IReadOnlyList<ObjectParam> BuildParameters()
    {
        static bool IsCurve(TerrainObjectDefinition definition) =>
            definition is ScatterObjectDefinition scatter && scatter.SourceMode == ScatterSourceMode.Curve;
        static bool IsRegion(TerrainObjectDefinition definition) =>
            definition is ScatterObjectDefinition scatter && scatter.SourceMode == ScatterSourceMode.Region;
        static bool IsDensity(TerrainObjectDefinition definition, ScatterDensityMode mode) =>
            definition is ScatterObjectDefinition scatter && scatter.DensityMode == mode;

        var parameters = new List<ObjectParam>
        {
            ObjectParam.Choice(
                "sourceMode",
                "Source",
                new (string, string)[] { ("Region", "Within region"), ("Curve", "Along curve") },
                definition => ((ScatterObjectDefinition)definition).SourceMode.ToString(),
                (definition, value) =>
                {
                    var scatter = (ScatterObjectDefinition)definition;
                    scatter.SourceMode = Enum.Parse<ScatterSourceMode>(value!);
                    if (scatter.SourceMode == ScatterSourceMode.Curve && scatter.DensityMode == ScatterDensityMode.PerArea)
                        scatter.DensityMode = ScatterDensityMode.Count;
                    if (scatter.SourceMode == ScatterSourceMode.Region && scatter.DensityMode == ScatterDensityMode.EdgeToEdge)
                        scatter.DensityMode = ScatterDensityMode.Spacing;
                },
                "Region: fill inside closed boundaries. Curve: distribute along open curves.",
                rebuildAfterCommit: true),
            ObjectParam.Sources(
                "scatterSource",
                "Source",
                definition => IsCurve(definition)
                    ? ((ScatterObjectDefinition)definition).Paths
                    : ((ScatterObjectDefinition)definition).Boundaries,
                RhinoObjectType.Curve,
                "Closed boundaries or open curves and/or layers for the scatter.",
                visibleWhen: definition => IsCurve(definition) || !((ScatterObjectDefinition)definition).UseTerrainZones,
                labelFor: definition => IsCurve(definition) ? "Curves" : "Boundaries"),
            ObjectParam.Bool("useTerrainZones", "Use terrain zones",
                definition => ((ScatterObjectDefinition)definition).UseTerrainZones,
                (definition, value) => ((ScatterObjectDefinition)definition).UseTerrainZones = value,
                "Use the selected zones' resolved areas, including holes and zone priority.",
                rebuildAfterCommit: true, visibleWhen: IsRegion),
            ObjectParam.ReadOnly("zoneSelection", "Zones", definition => "Select planting zones",
                "Select one or more enabled zones.", visibleWhen: definition => IsRegion(definition) && ((ScatterObjectDefinition)definition).UseTerrainZones),
            ObjectParam.Sources("exclusions", "Exclusions", definition => ((ScatterObjectDefinition)definition).Exclusions,
                RhinoObjectType.Curve, "Closed curves and layers to keep empty.", visibleWhen: IsRegion),
            ObjectParam.Number("exclusionClearance", "Exclusion clearance",
                definition => ((ScatterObjectDefinition)definition).ExclusionClearance,
                (definition, value) => ((ScatterObjectDefinition)definition).ExclusionClearance = value,
                "Additional distance kept clear around exclusion boundaries.", unit: ParameterUnit.ModelLength, liveScrub: true, visibleWhen: IsRegion),
            ObjectParam.Number("boundarySetback", "Boundary setback",
                definition => ((ScatterObjectDefinition)definition).BoundarySetback,
                (definition, value) => ((ScatterObjectDefinition)definition).BoundarySetback = value,
                "Distance kept empty inside the planting region boundary.", unit: ParameterUnit.ModelLength, liveScrub: true, visibleWhen: IsRegion),
            ObjectParam.Number("boundaryFeather", "Boundary feather",
                definition => ((ScatterObjectDefinition)definition).BoundaryFeather,
                (definition, value) => ((ScatterObjectDefinition)definition).BoundaryFeather = value,
                "Width over which planting gradually increases from empty to full density after the setback.", unit: ParameterUnit.ModelLength, liveScrub: true, visibleWhen: IsRegion),
            // Design review Step 61: along a curve, the mix itself is either weighted or a repeating sequence,
            // so the choice heads the Block Mix rather than sitting among the distribution settings.
            ObjectParam.Choice(
                "blockOrder",
                "Order",
                new (string, string)[] { ("Random", "Random"), ("Sequence", "Sequence") },
                definition => ((ScatterObjectDefinition)definition).BlockOrder.ToString(),
                (definition, value) => ((ScatterObjectDefinition)definition).BlockOrder = Enum.Parse<ScatterBlockOrder>(value!),
                "How each spot along the curve picks its block.",
                visibleWhen: definition => IsCurve(definition) && definition is ScatterObjectDefinition scatter && scatter.Blocks.Count > 1,
                optionHelp: new Dictionary<string, string>
                {
                    ["Random"] = "Each spot picks a block at random, in proportion to the weights.",
                    ["Sequence"] = "Blocks take turns in the order listed, repeating. Drag a row to reorder; untick a block to leave it out.",
                },
                fullRow: true),
            ObjectParam.BlockMix(
                "blocks",
                "Block Mix",
                "Weighted block definitions to scatter."),
            ObjectParam.Bool(
                "keepWholeBlocksInside",
                "Keep whole blocks inside",
                definition => ((ScatterObjectDefinition)definition).KeepWholeBlocksInside,
                (definition, value) => ((ScatterObjectDefinition)definition).KeepWholeBlocksInside = value,
                "Keep transformed block bounds inside a boundary in plan. Filters the requested samples; the final count may be lower.",
                visibleWhen: IsRegion),
            ObjectParam.Choice(
                "pattern",
                "Pattern",
                new (string, string)[] { ("Random", "Random"), ("Grid", "Grid"), ("JitteredGrid", "Jittered Grid"), ("PoissonDisk", "Poisson") },
                definition => ((ScatterObjectDefinition)definition).Pattern.ToString(),
                (definition, value) => ((ScatterObjectDefinition)definition).Pattern = Enum.Parse<ScatterPattern>(value!),
                "How instances are arranged inside the boundary.",
                visibleWhen: IsRegion),
            ObjectParam.Choice(
                "densityMode",
                "Density Mode",
                new (string, string)[] { ("Count", "Count"), ("PerArea", "Per Area"), ("Spacing", "Min Spacing"), ("EdgeToEdge", "Edge to Edge") },
                definition => ((ScatterObjectDefinition)definition).DensityMode.ToString(),
                (definition, value) => ((ScatterObjectDefinition)definition).DensityMode = Enum.Parse<ScatterDensityMode>(value!),
                "Choose whether scatter is controlled by count, density, or spacing.",
                rebuildAfterCommit: true,
                // Design review Step 37: three choices per source, shown as segments.
                optionsFor: definition => IsCurve(definition)
                    ? new (string, string)[] { ("Count", "Count"), ("Spacing", "Centre Spacing"), ("EdgeToEdge", "Edge to Edge") }
                    : new (string, string)[] { ("Count", "Count"), ("PerArea", "Per Area"), ("Spacing", "Min Spacing") },
                segmented: true),
            ObjectParam.Choice("densityUnit", "Density unit",
                new (string, string)[] { ("SquareMeter", "Plants / m²"), ("HundredSquareMeters", "Plants / 100 m²"), ("Hectare", "Plants / hectare"), ("ModelArea", "Plants / square model unit") },
                definition => ((ScatterObjectDefinition)definition).DensityUnit.ToString(),
                (definition, value) => ((ScatterObjectDefinition)definition).DensityUnit = Enum.Parse<ScatterDensityUnit>(value!),
                "Changes the displayed unit without changing the planting density.", rebuildAfterCommit: true,
                visibleWhen: definition => IsDensity(definition, ScatterDensityMode.PerArea)),
            ObjectParam.Number(
                "perAreaDensity",
                "Per Area",
                definition => ((ScatterObjectDefinition)definition).PerAreaDensity,
                (definition, value) => ((ScatterObjectDefinition)definition).PerAreaDensity = value,
                "Instances per square model unit.",
                min: 0.0,
                decimalPlaces: 4,
                liveEdit: true,
                liveScrub: true,
                visibleWhen: definition => IsDensity(definition, ScatterDensityMode.PerArea)),
            ObjectParam.Number(
                "spacing",
                "Spacing",
                definition => ((ScatterObjectDefinition)definition).Spacing,
                (definition, value) => ((ScatterObjectDefinition)definition).Spacing = value,
                "Minimum centre-to-centre distance between instances.",
                min: 0.0,
                liveEdit: true,
                liveScrub: true,
                visibleWhen: definition => IsDensity(definition, ScatterDensityMode.Spacing)),
            ObjectParam.Number(
                "edgeGap",
                "Edge Gap",
                definition => ((ScatterObjectDefinition)definition).EdgeGap,
                (definition, value) => ((ScatterObjectDefinition)definition).EdgeGap = value,
                "Gap left between block footprints.",
                min: 0.0,
                liveEdit: true,
                liveScrub: true,
                visibleWhen: definition => IsDensity(definition, ScatterDensityMode.EdgeToEdge)),
            ObjectParam.Slider(
                "count",
                "Count",
                definition => ((ScatterObjectDefinition)definition).Count,
                (definition, value) => ((ScatterObjectDefinition)definition).Count = value,
                1.0,
                1000.0,
                "Total number of instances to scatter.",
                hardMin: 0.0,
                decimalPlaces: 0,
                liveScrub: true,
                visibleWhen: definition => IsDensity(definition, ScatterDensityMode.Count)),
            ObjectParam.Slider(
                "alongJitter",
                "Randomness",
                definition => ((ScatterObjectDefinition)definition).AlongJitter,
                (definition, value) => ((ScatterObjectDefinition)definition).AlongJitter = value,
                0.0,
                1.0,
                "Along-curve randomness: 0 = even, 1 = up to half a spacing step.",
                hardMin: 0.0,
                hardMax: 1.0,
                decimalPlaces: 2,
                liveScrub: true,
                visibleWhen: definition => IsCurve(definition) && !IsDensity(definition, ScatterDensityMode.EdgeToEdge)),
            ObjectParam.Slider(
                "jitterXy",
                "XY Jitter",
                definition => ((ScatterObjectDefinition)definition).JitterXy,
                (definition, value) => ((ScatterObjectDefinition)definition).JitterXy = value,
                0.0,
                5.0,
                "Random XY offset radius applied to each on-curve point.",
                hardMin: 0.0,
                liveScrub: true,
                visibleWhen: IsCurve),
            ObjectParam.Bool(
                "alignToTangent",
                "Align to tangent",
                definition => ((ScatterObjectDefinition)definition).AlignToTangent,
                (definition, value) => ((ScatterObjectDefinition)definition).AlignToTangent = value,
                "Orient instances to follow the curve direction.",
                rebuildAfterCommit: false),
            ObjectParam.Bool(
                "alignToSlope",
                "Align to slope",
                definition => ((ScatterObjectDefinition)definition).AlignToSlope,
                (definition, value) => ((ScatterObjectDefinition)definition).AlignToSlope = value,
                "Orient instances to the terrain normal."),
            ObjectParam.Bool(
                "slopeFilterEnabled",
                "Slope filter",
                definition => ((ScatterObjectDefinition)definition).SlopeFilterEnabled,
                (definition, value) => ((ScatterObjectDefinition)definition).SlopeFilterEnabled = value,
                "Only place instances within the slope range below.",
                rebuildAfterCommit: true),
            ObjectParam.SlopeSlider(
                "slopeMinDegrees",
                "Slope Min",
                definition => ((ScatterObjectDefinition)definition).SlopeMinDegrees,
                (definition, value) => ((ScatterObjectDefinition)definition).SlopeMinDegrees = value,
                "Flattest terrain that still gets an instance.",
                liveScrub: true,
                visibleWhen: definition => definition is ScatterObjectDefinition scatter && scatter.SlopeFilterEnabled),
            ObjectParam.SlopeSlider(
                "slopeMaxDegrees",
                "Slope Max",
                definition => ((ScatterObjectDefinition)definition).SlopeMaxDegrees,
                (definition, value) => ((ScatterObjectDefinition)definition).SlopeMaxDegrees = value,
                "Steepest terrain that still gets an instance.",
                liveScrub: true,
                visibleWhen: definition => definition is ScatterObjectDefinition scatter && scatter.SlopeFilterEnabled),
            ObjectParam.Bool(
                "elevationFilterEnabled",
                "Elevation filter",
                definition => ((ScatterObjectDefinition)definition).ElevationFilterEnabled,
                (definition, value) => ((ScatterObjectDefinition)definition).ElevationFilterEnabled = value,
                "Only place instances within the elevation range below.",
                rebuildAfterCommit: true),
            ObjectParam.Number(
                "elevationMin",
                "Elevation Min",
                definition => ((ScatterObjectDefinition)definition).ElevationMin,
                (definition, value) => ((ScatterObjectDefinition)definition).ElevationMin = value,
                "Minimum terrain elevation.",
                min: null,
                liveEdit: true,
                liveScrub: true,
                visibleWhen: definition => definition is ScatterObjectDefinition scatter && scatter.ElevationFilterEnabled),
            ObjectParam.Number(
                "elevationMax",
                "Elevation Max",
                definition => ((ScatterObjectDefinition)definition).ElevationMax,
                (definition, value) => ((ScatterObjectDefinition)definition).ElevationMax = value,
                "Maximum terrain elevation.",
                min: null,
                liveEdit: true,
                liveScrub: true,
                visibleWhen: definition => definition is ScatterObjectDefinition scatter && scatter.ElevationFilterEnabled),
        };

        parameters.AddRange(ObjectParameterCatalog.CommonTransforms);
        parameters.Add(ObjectParam.Choice(
            "previewMode",
            "Preview",
            new (string, string)[] { ("Points", "Point cloud"), ("ShapePoints", "Shape points"), ("BoundingBox", "Bounding boxes"), ("Instances", "Real (capped)"), ("RenderInstances", "Enscape / native blocks (experimental)") },
            definition => ((ScatterObjectDefinition)definition).PreviewMode.ToString(),
            (definition, value) => ((ScatterObjectDefinition)definition).PreviewMode = Enum.Parse<ScatterPreviewMode>(value!),
            "How the scatter draws while editing. Enscape / native blocks (experimental) creates temporary document instances up to Preview Cap, so renderers that only see document objects can show them; it has not yet been verified inside Enscape. Bake always produces all instances."));
        parameters.Add(ObjectParam.Number(
            "previewCap",
            "Preview Cap",
            definition => ((ScatterObjectDefinition)definition).PreviewCap,
            (definition, value) => ((ScatterObjectDefinition)definition).PreviewCap = (int)Math.Round(value),
            "Maximum scatter items drawn in live preview. Set to 0 for no cap.",
            min: 0.0,
            decimalPlaces: 0,
            liveEdit: true,
            liveScrub: true));
        return Sectioned(parameters);
    }

    // The card reads where, what, then how (design review: Scatter follows where, what, then how).
    private static readonly (string Key, string Title, bool Expanded)[] SectionOrder =
    {
        ("distribution", "Distribution", true),
        ("blockMix", "Block Mix", true),
        ("placement", "Placement", false),
        ("filters", "Filters", false),
        ("preview", "Preview", false),
    };

    private static string SectionOf(string key) => key switch
    {
        "blocks" or "blockOrder" => "blockMix",
        "sourceMode" or "scatterSource" or "useTerrainZones" or "zoneSelection" or "pattern" or
            "densityMode" or "densityUnit" or "perAreaDensity" or "spacing" or "edgeGap" or "count" or "alongJitter" => "distribution",
        "exclusions" or "exclusionClearance" or "boundarySetback" or "boundaryFeather" or "keepWholeBlocksInside" or
            "slopeFilterEnabled" or "slopeMinDegrees" or "slopeMaxDegrees" or "elevationFilterEnabled" or "elevationMin" or "elevationMax" => "filters",
        "previewMode" or "previewCap" => "preview",
        _ => "placement",
    };

    /// <summary>The rows in section order, each group opened by its declared section.</summary>
    private static IReadOnlyList<ObjectParam> Sectioned(List<ObjectParam> parameters)
    {
        var result = new List<ObjectParam>(parameters.Count + SectionOrder.Length);
        foreach (var (key, title, expanded) in SectionOrder)
        {
            Func<TerrainObjectDefinition, string?>? summary = key switch
            {
                "filters" => ActiveFilters,
                "blockMix" => BlockCount,
                _ => null,
            };
            result.Add(ObjectParam.Section(key, title, expanded, summary: summary));
            result.AddRange(parameters.Where(parameter => SectionOf(parameter.Key) == key));
        }

        return result;
    }

    /// <summary>A closed Block Mix still says how many blocks it mixes (design review Step 60).</summary>
    private static string? BlockCount(TerrainObjectDefinition definition) =>
        definition is ScatterObjectDefinition scatter
            ? $"{scatter.Blocks.Count} {(scatter.Blocks.Count == 1 ? "block" : "blocks")}"
            : null;

    /// <summary>Names the filters in force, so a closed Filters section still says what it removes.</summary>
    private static string? ActiveFilters(TerrainObjectDefinition definition)
    {
        if (definition is not ScatterObjectDefinition scatter)
            return null;

        var active = new List<string>();
        if (scatter.Exclusions.HasReferences) active.Add("Exclusions");
        if (scatter.BoundarySetback > 0) active.Add("Setback");
        if (scatter.BoundaryFeather > 0) active.Add("Feather");
        if (scatter.KeepWholeBlocksInside) active.Add("Whole blocks");
        if (scatter.SlopeFilterEnabled) active.Add("Slope");
        if (scatter.ElevationFilterEnabled) active.Add("Elevation");
        return active.Count == 0 ? "None" : string.Join(", ", active);
    }
}
