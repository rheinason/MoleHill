using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class GradePathModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "grade-path";
    public override Type DefinitionType => typeof(GradePathModifierDefinition);
    public override string DisplayName => "Grade Path";
    public override string IconName => "ModGradePath";
    public override int SortOrder => 6;
    public override string Subtitle => "Road corridor, batter to daylight";
    public override ModifierDefinition Create(UnitSystem unitSystem) =>
        new GradePathModifierDefinition { Width = ModelUnits.FromMeters(2.0, unitSystem) };
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunGradePathStage(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var path = (GradePathModifierDefinition)modifier;
        int paths = CountSources(path.Paths);
        if (!path.UseVariableWidth)
            return $"{paths} centerlines | Width {path.Width:G4}";
        return $"{paths} centerlines | variable width, {CountSources(path.WidthEdges)} width edges | Width {path.Width:G4} fallback";
    }

    public override string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot) =>
        modifier is GradePathModifierDefinition m && NoneResolve(snapshot, m.Paths) ? "Not applied — no paths selected." : null;

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "Paths", "Centerlines",
            m => ((GradePathModifierDefinition)m).Paths,
            RhinoObjectType.Curve,
            "Path curves accept Rhino object picks and layers. Curve Z defines the finished road elevation profile."),
        ModifierParam.Number(
            "Width", "Width",
            m => ((GradePathModifierDefinition)m).Width,
            (m, v) => ((GradePathModifierDefinition)m).Width = v,
            "Finished path width. This is the flat or controlled-width core before side grading starts.",
            unit: ParameterUnit.ModelLength),
        ModifierParam.Bool(
            "UseVariableWidth", "Variable Width",
            m => ((GradePathModifierDefinition)m).UseVariableWidth,
            (m, v) => ((GradePathModifierDefinition)m).UseVariableWidth = v,
            "Off: the corridor keeps the constant Width above. On: nearby plan curves take over each side, and Width becomes the fallback where no edge is matched."),
        ModifierParam.Sources(
            "WidthEdges", "Width Edges",
            m => ((GradePathModifierDefinition)m).WidthEdges,
            RhinoObjectType.Curve,
            "Roughly parallel plan curves. Each curve is matched uniquely to a centerline side; its Z is ignored and remapped from the centerline.",
            visibleWhen: static m => ((GradePathModifierDefinition)m).UseVariableWidth),
        ModifierParam.Number(
            "MaxEdgeDistance", "Edge Match Distance",
            m => ((GradePathModifierDefinition)m).MaxEdgeDistance,
            (m, v) => ((GradePathModifierDefinition)m).MaxEdgeDistance = v,
            "Maximum plan distance for matching a width edge to a centerline. A larger value helps match edges on wide sites; set to 0 to use the automatic four-times-Width fallback.",
            unit: ParameterUnit.ModelLength,
            visibleWhen: static m => ((GradePathModifierDefinition)m).UseVariableWidth),
        ModifierParam.Slope(
            "SlopeAngle", "Fill Slope",
            m => ((GradePathModifierDefinition)m).SlopeAngle,
            (m, v) => ((GradePathModifierDefinition)m).SlopeAngle = v,
            "Fill slope, used where terrain sits below the road. This is the main slope; the cut slope inherits it unless overridden. Flatter slopes spread the batter farther."),
        ModifierParam.OptionalSlope(
            "CutSlopeAngle", "Cut Slope",
            m => ((GradePathModifierDefinition)m).CutSlopeAngle,
            (m, v) => ((GradePathModifierDefinition)m).CutSlopeAngle = v,
            m => ((GradePathModifierDefinition)m).SlopeAngle,
            "Cut slope override, used where terrain sits above the road."),
        ModifierParam.Number(
            "MaxDistance", "Max Distance",
            m => ((GradePathModifierDefinition)m).MaxDistance,
            (m, v) => ((GradePathModifierDefinition)m).MaxDistance = v,
            "Maximum grading reach away from the path. 0 means unlimited; smaller values stop the batter sooner.",
            unit: ParameterUnit.ModelLength),
        ModifierParam.Bool(
            "GradeThroughBreaklines", "Grade Through Breaklines",
            m => ((GradePathModifierDefinition)m).GradeThroughBreaklines,
            (m, v) => ((GradePathModifierDefinition)m).GradeThroughBreaklines = v,
            "Off: breaklines and graded edges from cards above are lines this path may not cross; it stops at them. On: it regrades across them, and the parts it regraded are dropped."),
    };
}
