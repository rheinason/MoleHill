using ObjectParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.TerrainObjectDefinition>;

namespace MoleHill.Rhino.Registry;

/// <summary>The rotation/scale/seed/offset rows every terrain-object type shares, declared once.</summary>
internal static class ObjectParameterCatalog
{
    public static IReadOnlyList<ObjectParam> Common { get; } = new[]
    {
        ObjectParam.Sources(
            "sources",
            "Sources",
            definition => definition.Sources,
            0,
            "Rhino objects and layers whose instances should be placed."),
        ObjectParam.Number(
            "headingDegrees", "Heading", definition => definition.HeadingDegrees,
            (definition, value) => definition.HeadingDegrees = value,
            "Additional rotation from the object's remembered heading. Replaces the previous adjustment.",
            min: null, unit: ParameterUnit.Degrees, liveEdit: true, liveScrub: true),
        ObjectParam.Number(
            "placementScale", "Scale", definition => definition.PlacementScale,
            (definition, value) => definition.PlacementScale = value,
            "Uniform multiple of the source size. Replaces the previous adjustment.",
            min: 0.01, unit: ParameterUnit.None, liveEdit: true, liveScrub: true),
        ObjectParam.Slider(
            "randomRotationMinDegrees",
            "Rotate Min",
            definition => definition.RandomRotationMinDegrees,
            (definition, value) => definition.RandomRotationMinDegrees = value,
            0.0,
            360.0,
            "Minimum random rotation in degrees.",
            0.0,
            360.0,
            1,
            unit: ParameterUnit.Degrees,
            liveScrub: true),
        ObjectParam.Slider(
            "randomRotationMaxDegrees",
            "Rotate Max",
            definition => definition.RandomRotationMaxDegrees,
            (definition, value) => definition.RandomRotationMaxDegrees = value,
            0.0,
            360.0,
            "Maximum random rotation in degrees.",
            0.0,
            360.0,
            1,
            unit: ParameterUnit.Degrees,
            liveScrub: true),
        ObjectParam.Slider(
            "randomScaleMin",
            "Scale Min",
            definition => definition.RandomScaleMin,
            (definition, value) => definition.RandomScaleMin = value,
            0.25,
            2.0,
            "Minimum random uniform scale.",
            0.01,
            null,
            3,
            liveScrub: true),
        ObjectParam.Slider(
            "randomScaleMax",
            "Scale Max",
            definition => definition.RandomScaleMax,
            (definition, value) => definition.RandomScaleMax = value,
            0.25,
            2.0,
            "Maximum random uniform scale.",
            0.01,
            null,
            3,
            liveScrub: true),
        ObjectParam.Number(
            "randomSeed",
            "Seed",
            definition => definition.RandomSeed,
            (definition, value) => definition.RandomSeed = (int)Math.Round(value),
            "Stable random seed for this object card.",
            decimalPlaces: 0,
            liveEdit: true,
            liveScrub: true),
        ObjectParam.Number(
            "zOffset",
            "Offset",
            definition => definition.ZOffset,
            (definition, value) => definition.ZOffset = value,
            "Lift or sink along World Z for Plant, or the fitted terrain normal for Orient.",
            min: null,
            unit: ParameterUnit.ModelLength,
            liveEdit: true,
            liveScrub: true),
        ObjectParam.ReadOnly(
            "bindings",
            "Bindings",
            definition => $"{definition.Sources.ObjectIds.Count + definition.Sources.LayerPaths.Count} source refs",
            "Explicit picks plus watched layers drive the objects in this definition."),
    };

    public static IReadOnlyList<ObjectParam> CommonTransforms => Common
        .Where(parameter => parameter.Key.StartsWith("random", StringComparison.Ordinal) || parameter.Key == "zOffset").ToArray();
}
