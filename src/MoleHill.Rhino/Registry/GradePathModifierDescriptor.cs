using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal sealed class GradePathModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "grade-path";
    public override Type DefinitionType => typeof(GradePathModifierDefinition);
    public override string DisplayName => "Grade Path";
    public override string IconName => "ModGradePath";
    public override int SortOrder => 6;
    public override string Subtitle => "Path corridor grading";
    public override ModifierDefinition Create(UnitSystem unitSystem) =>
        new GradePathModifierDefinition { Width = ModelUnits.FromMeters(2.0, unitSystem) };
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunGradePathStage(context);

    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.Sources(
            "Paths", "Paths",
            m => ((GradePathModifierDefinition)m).Paths,
            RhinoObjectType.Curve,
            "Path curves accept Rhino object picks and layers. Curve Z defines the finished road elevation profile."),
        ParameterDescriptor.Number(
            "Width", "Width",
            m => ((GradePathModifierDefinition)m).Width,
            (m, v) => ((GradePathModifierDefinition)m).Width = v,
            "Finished path width. This is the flat or controlled-width core before side grading starts."),
        ParameterDescriptor.Number(
            "SlopeAngle", "Fill Slope",
            m => ((GradePathModifierDefinition)m).SlopeAngle,
            (m, v) => ((GradePathModifierDefinition)m).SlopeAngle = v,
            "Fill slope in degrees, used where terrain sits below the road. This is the main slope; the cut slope inherits it unless overridden. Lower values spread the shoulder farther."),
        ParameterDescriptor.OptionalNumber(
            "CutSlopeAngle", "Cut Slope",
            m => ((GradePathModifierDefinition)m).CutSlopeAngle,
            (m, v) => ((GradePathModifierDefinition)m).CutSlopeAngle = v,
            m => ((GradePathModifierDefinition)m).SlopeAngle,
            "Cut slope override in degrees, used where terrain sits above the road. Leave blank to use the fill slope."),
        ParameterDescriptor.Number(
            "MaxDistance", "Max Distance",
            m => ((GradePathModifierDefinition)m).MaxDistance,
            (m, v) => ((GradePathModifierDefinition)m).MaxDistance = v,
            "Maximum grading reach away from the path. 0 means unlimited; lower values constrain the shoulder length."),
    };
}
