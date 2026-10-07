using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
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
    public override string Subtitle => "Batter away from a design line";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new GradeLineModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunGradeLineStage(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var line = (GradeLineModifierDefinition)modifier;
        int lineCount = CountSources(line.Lines);
        return line.UseAsymmetricSides
            ? $"{lineCount} design lines | asymmetric sides"
            : $"{lineCount} design lines | Fill {AnalysisFormatting.FormatSlopeDegrees(line.SlopeAngle)}";
    }

    public override string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot) =>
        modifier is GradeLineModifierDefinition m && NoneResolve(snapshot, m.Lines) ? "Not applied — no lines selected." : null;

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
            "Maximum grading reach away from the line. 0 means unlimited; smaller values stop the batter sooner.",
            unit: ParameterUnit.ModelLength),
        ModifierParam.Bool(
            "GradeThroughBreaklines", "Grade Through Breaklines",
            m => ((GradeLineModifierDefinition)m).GradeThroughBreaklines,
            (m, v) => ((GradeLineModifierDefinition)m).GradeThroughBreaklines = v,
            "Off: breaklines and graded edges from cards above are lines this grade may not cross. On: it regrades across them, and the parts it regraded are dropped."),
    };
}
