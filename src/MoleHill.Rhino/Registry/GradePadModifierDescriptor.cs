using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal sealed class GradePadModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "grade-pad";
    public override Type DefinitionType => typeof(GradePadModifierDefinition);
    public override string DisplayName => "Grade Pad";
    public override string IconName => "ModGradePad";
    public override int SortOrder => 5;
    public override string Subtitle => "Pad + daylight grading";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new GradePadModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunGradePadStage(context);

    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.Sources(
            "Boundaries", "Boundaries",
            m => ((GradePadModifierDefinition)m).Boundaries,
            RhinoObjectType.Curve),
        ParameterDescriptor.Number(
            "SlopeAngle", "Fill Slope",
            m => ((GradePadModifierDefinition)m).SlopeAngle,
            (m, v) => ((GradePadModifierDefinition)m).SlopeAngle = v,
            "Fill slope in degrees, used where terrain sits below the pad. This is the main slope; the cut slope inherits it unless overridden. Boundary curve Z defines the finished pad plane; lower values are flatter and extend farther."),
        ParameterDescriptor.OptionalNumber(
            "CutSlopeAngle", "Cut Slope",
            m => ((GradePadModifierDefinition)m).CutSlopeAngle,
            (m, v) => ((GradePadModifierDefinition)m).CutSlopeAngle = v,
            m => ((GradePadModifierDefinition)m).SlopeAngle,
            "Cut slope override in degrees, used where terrain sits above the pad. Leave blank to use the fill slope."),
        ParameterDescriptor.Number(
            "MaxDistance", "Max Distance",
            m => ((GradePadModifierDefinition)m).MaxDistance,
            (m, v) => ((GradePadModifierDefinition)m).MaxDistance = v,
            "Maximum grading reach. 0 means unlimited; smaller values keep the effect close to the pad."),
    };
}
