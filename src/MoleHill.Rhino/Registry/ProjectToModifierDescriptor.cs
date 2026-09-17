using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class ProjectToModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "project-to";
    public override Type DefinitionType => typeof(ProjectToModifierDefinition);
    public override string DisplayName => "Project To";
    public override string IconName => "ModProjectTo";
    public override int SortOrder => 9;
    public override string Subtitle => "Blend vertically to a target";

    public override ModifierDefinition Create(UnitSystem unitSystem) => new ProjectToModifierDefinition
    {
        FeatherDistance = ModelUnits.FromMeters(1.0, unitSystem)
    };

    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunProjectToStage(context);

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "TargetMesh", "Target Mesh",
            m => ((ProjectToModifierDefinition)m).TargetMesh,
            RhinoObjectType.Mesh,
            "One Rhino mesh to project toward. Assigning it clears the terrain target."),
        ModifierParam.Sources(
            "Boundaries", "Boundaries",
            m => ((ProjectToModifierDefinition)m).Boundaries,
            RhinoObjectType.Curve,
            "Optional closed plan curves. Nested loops alternate included and excluded regions, so inner loops make donut holes."),
        ModifierParam.Slider(
            "Strength", "Strength",
            m => ((ProjectToModifierDefinition)m).Strength,
            (m, v) => ((ProjectToModifierDefinition)m).Strength = v,
            softMin: 0.0, softMax: 1.0, hardMin: 0.0, hardMax: 1.0,
            decimalPlaces: 3, liveScrub: true,
            help: "How far each covered terrain vertex moves toward the target elevation."),
        ModifierParam.Number(
            "FeatherDistance", "Feather",
            m => ((ProjectToModifierDefinition)m).FeatherDistance,
            (m, v) => ((ProjectToModifierDefinition)m).FeatherDistance = v,
            "Distance inside every boundary edge over which the effect rises smoothly from zero to full strength.",
            min: 0.0, unit: ParameterUnit.ModelLength),
    };
}
