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
                new (string, string)[] { ("Count", "Total count"), ("PerArea", "Per area"), ("Spacing", "Min spacing"), ("EdgeToEdge", "Edge-to-edge") },
                definition => ((ScatterObjectDefinition)definition).DensityMode.ToString(),
                (definition, value) => ((ScatterObjectDefinition)definition).DensityMode = Enum.Parse<ScatterDensityMode>(value!),
                "Choose whether scatter is controlled by count, density, or spacing.",
                rebuildAfterCommit: true,
                optionsFor: definition => IsCurve(definition)
                    ? new (string, string)[] { ("Count", "Total count"), ("Spacing", "Centre spacing"), ("EdgeToEdge", "Edge-to-edge") }
                    : new (string, string)[] { ("Count", "Total count"), ("PerArea", "Per area"), ("Spacing", "Min spacing") }),
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
            ObjectParam.Choice(
                "blockOrder",
                "Block order",
                new (string, string)[] { ("Random", "Random"), ("Sequence", "Sequence") },
                definition => ((ScatterObjectDefinition)definition).BlockOrder.ToString(),
                (definition, value) => ((ScatterObjectDefinition)definition).BlockOrder = Enum.Parse<ScatterBlockOrder>(value!),
                "Random: choose by weight. Sequence: cycle the block list in order.",
                visibleWhen: definition => IsCurve(definition) && definition is ScatterObjectDefinition scatter && scatter.Blocks.Count > 1),
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
        return parameters;
    }
}
