using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class GradeLineModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "grade-line";
    public override Type DefinitionType => typeof(GradeLineModifierDefinition);
    public override string DisplayName => "Grade Line";
    public override string IconName => "ModGradeLine";
    public override int SortOrder => 7;
    public override string Subtitle => "Grade away from a line";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new GradeLineModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunGradeLineStage(context);

    private static bool IsAsymmetric(ModifierDefinition m) =>
        ((GradeLineModifierDefinition)m).UseAsymmetricSides;

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "Lines", "Design Lines",
            m => ((GradeLineModifierDefinition)m).Lines,
            RhinoObjectType.Curve,
            "Design lines accept Rhino object picks and layers. Curve Z is the finished elevation along the line; the terrain grades away from it."),
        ModifierParam.Slope(
            "SlopeAngle", "Fill Slope",
            m => ((GradeLineModifierDefinition)m).SlopeAngle,
            (m, v) => ((GradeLineModifierDefinition)m).SlopeAngle = v,
            "Fill slope, used where terrain sits below the line. This is the main slope; the cut slope inherits it unless overridden."),
        ModifierParam.OptionalSlope(
            "CutSlopeAngle", "Cut Slope",
            m => ((GradeLineModifierDefinition)m).CutSlopeAngle,
            (m, v) => ((GradeLineModifierDefinition)m).CutSlopeAngle = v,
            m => ((GradeLineModifierDefinition)m).SlopeAngle,
            "Cut slope override, used where terrain sits above the line."),
        ModifierParam.Bool(
            "UseAsymmetricSides", "Asymmetric Sides",
            m => ((GradeLineModifierDefinition)m).UseAsymmetricSides,
            (m, v) => ((GradeLineModifierDefinition)m).UseAsymmetricSides = v,
            "Off: both sides use the slopes above. On: each side of the line takes its own cut and fill slope, which is how a ditch with a steep back and a flat front is drawn.",
            rebuildAfterCommit: true),
        ModifierParam.OptionalSlope(
            "LeftCutSlopeAngle", "Left Cut Slope",
            m => ((GradeLineModifierDefinition)m).LeftCutSlopeAngle,
            (m, v) => ((GradeLineModifierDefinition)m).LeftCutSlopeAngle = v,
            m => ((GradeLineModifierDefinition)m).CutSlopeAngle > 0.0
                ? ((GradeLineModifierDefinition)m).CutSlopeAngle
                : ((GradeLineModifierDefinition)m).SlopeAngle,
            "Cut slope on the left of the line. Blank inherits the shared cut slope.",
            visibleWhen: IsAsymmetric),
        ModifierParam.OptionalSlope(
            "LeftFillSlopeAngle", "Left Fill Slope",
            m => ((GradeLineModifierDefinition)m).LeftFillSlopeAngle,
            (m, v) => ((GradeLineModifierDefinition)m).LeftFillSlopeAngle = v,
            m => ((GradeLineModifierDefinition)m).SlopeAngle,
            "Fill slope on the left of the line. Blank inherits the shared fill slope.",
            visibleWhen: IsAsymmetric),
        ModifierParam.OptionalSlope(
            "RightCutSlopeAngle", "Right Cut Slope",
            m => ((GradeLineModifierDefinition)m).RightCutSlopeAngle,
            (m, v) => ((GradeLineModifierDefinition)m).RightCutSlopeAngle = v,
            m => ((GradeLineModifierDefinition)m).CutSlopeAngle > 0.0
                ? ((GradeLineModifierDefinition)m).CutSlopeAngle
                : ((GradeLineModifierDefinition)m).SlopeAngle,
            "Cut slope on the right of the line. Blank inherits the shared cut slope.",
            visibleWhen: IsAsymmetric),
        ModifierParam.OptionalSlope(
            "RightFillSlopeAngle", "Right Fill Slope",
            m => ((GradeLineModifierDefinition)m).RightFillSlopeAngle,
            (m, v) => ((GradeLineModifierDefinition)m).RightFillSlopeAngle = v,
            m => ((GradeLineModifierDefinition)m).SlopeAngle,
            "Fill slope on the right of the line. Blank inherits the shared fill slope.",
            visibleWhen: IsAsymmetric),
        ModifierParam.Number(
            "MaxDistance", "Max Distance",
            m => ((GradeLineModifierDefinition)m).MaxDistance,
            (m, v) => ((GradeLineModifierDefinition)m).MaxDistance = v,
            "Maximum grading reach away from the line. 0 means unlimited; lower values constrain how far the batter spreads.",
            unit: ParameterUnit.ModelLength),
    };
}
