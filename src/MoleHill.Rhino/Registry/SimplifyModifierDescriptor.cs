using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

/// <summary>Defines the Simplify card, its mode-specific parameters, and its build-stage dispatch.</summary>
internal sealed class SimplifyModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "simplify";
    public override Type DefinitionType => typeof(SimplifyModifierDefinition);
    public override string DisplayName => "Simplify";
    public override string IconName => "ModSimplify";
    public override int SortOrder => 1;
    public override string Subtitle => "Certified surface reduction";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new SimplifyModifierDefinition
    {
        MaximumDeviation = ModelUnits.FromMeters(0.05, unitSystem)
    };
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunSimplifyStage(context);

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Choice(
            "Mode", "Mode",
            new[]
            {
                (SimplifyModifierDefinition.MaximumDeviationMode, "Maximum deviation"),
                (SimplifyModifierDefinition.TargetVertexCountMode, "Target vertex count"),
                (SimplifyModifierDefinition.RetainPercentageMode, "Retain percentage")
            },
            m => ((SimplifyModifierDefinition)m).Mode,
            (m, value) => ((SimplifyModifierDefinition)m).Mode = value ?? SimplifyModifierDefinition.MaximumDeviationMode,
            "Choose a certified vertical-error bound, an output vertex cap, or a percentage converted to a floor-rounded vertex cap. Count modes report achieved error but do not impose an error tolerance.",
            rebuildAfterCommit: true),
        ModifierParam.Number(
            "MaximumDeviation", "Maximum Deviation",
            m => ((SimplifyModifierDefinition)m).MaximumDeviation,
            (m, value) => ((SimplifyModifierDefinition)m).MaximumDeviation = value,
            "Largest permitted vertical difference from the terrain immediately before this modifier. The complete triangle overlay is verified; boundaries and persistent grading/wall constraints are retained. If no smaller mesh can meet the bound, the incoming mesh is kept. Later modifiers may change the surface, and Earthworks can report a small volume difference because this is not a volume-conservation constraint.",
            min: 0.0,
            unit: ParameterUnit.ModelLength,
            visibleWhen: m => ((SimplifyModifierDefinition)m).Mode == SimplifyModifierDefinition.MaximumDeviationMode),
        ModifierParam.Number(
            "TargetVertexCount", "Target Vertices",
            m => ((SimplifyModifierDefinition)m).TargetVertexCount,
            (m, value) => ((SimplifyModifierDefinition)m).TargetVertexCount =
                double.IsFinite(value) ? (int)Math.Clamp(Math.Floor(value), 0, int.MaxValue) : 0,
            "Maximum output vertex count, including boundary, constraint, and triangulator-inserted vertices. If mandatory geometry alone exceeds the cap, the incoming mesh is kept with a diagnostic.",
            min: 3.0,
            decimalPlaces: 0,
            step: 1.0,
            unit: ParameterUnit.None,
            visibleWhen: m => ((SimplifyModifierDefinition)m).Mode == SimplifyModifierDefinition.TargetVertexCountMode),
        ModifierParam.Number(
            "RetainPercentage", "Retain",
            m => ((SimplifyModifierDefinition)m).RetainPercentage,
            (m, value) => ((SimplifyModifierDefinition)m).RetainPercentage = value,
            "Percentage of incoming used vertices to retain. The cap is floor(input vertices × percentage / 100); 100% keeps the input unchanged, and a cap below mandatory geometry fails cleanly.",
            min: 0.0,
            max: 100.0,
            decimalPlaces: 2,
            unit: ParameterUnit.Percent,
            visibleWhen: m => ((SimplifyModifierDefinition)m).Mode == SimplifyModifierDefinition.RetainPercentageMode)
    };
}
