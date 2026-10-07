using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class GradePadModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "grade-pad";
    public override Type DefinitionType => typeof(GradePadModifierDefinition);
    public override string DisplayName => "Grade Pad";
    public override string IconName => "ModGradePad";
    public override int SortOrder => 5;
    public override string Subtitle => "Level pad, batter to daylight";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new GradePadModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunGradePadStage(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var p = (GradePadModifierDefinition)modifier;
        return $"{CountSources(p.Boundaries)} boundaries | Fill {FormatSlopeDegrees(p.SlopeAngle)}";
    }

    public override string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot) =>
        modifier is GradePadModifierDefinition m && NoneResolve(snapshot, m.Boundaries) ? "Not applied — no boundaries selected." : null;

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "Boundaries", "Boundaries",
            m => ((GradePadModifierDefinition)m).Boundaries,
            RhinoObjectType.Curve),
        ModifierParam.Slope(
            "SlopeAngle", "Fill Slope",
            m => ((GradePadModifierDefinition)m).SlopeAngle,
            (m, v) => ((GradePadModifierDefinition)m).SlopeAngle = v,
            "Fill slope, used where terrain sits below the pad. This is the main slope; the cut slope inherits it unless overridden. Boundary curve Z defines the finished pad plane; flatter slopes extend farther."),
        ModifierParam.OptionalSlope(
            "CutSlopeAngle", "Cut Slope",
            m => ((GradePadModifierDefinition)m).CutSlopeAngle,
            (m, v) => ((GradePadModifierDefinition)m).CutSlopeAngle = v,
            m => ((GradePadModifierDefinition)m).SlopeAngle,
            "Cut slope override, used where terrain sits above the pad."),
        ModifierParam.Number(
            "MaxDistance", "Max Distance",
            m => ((GradePadModifierDefinition)m).MaxDistance,
            (m, v) => ((GradePadModifierDefinition)m).MaxDistance = v,
            "Maximum grading reach away from the pad. 0 means unlimited; smaller values stop the batter sooner.",
            unit: ParameterUnit.ModelLength),
        ModifierParam.Bool(
            "GradeThroughBreaklines", "Grade Through Breaklines",
            m => ((GradePadModifierDefinition)m).GradeThroughBreaklines,
            (m, v) => ((GradePadModifierDefinition)m).GradeThroughBreaklines = v,
            "Off: breaklines and graded edges from cards above are lines this pad may not cross. On: the pad regrades across them, and the parts it regraded are dropped."),
    };
}
