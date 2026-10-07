using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class RetainingWallModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "retaining-wall";
    public override Type DefinitionType => typeof(RetainingWallModifierDefinition);
    public override string DisplayName => "Retaining Wall";
    public override string IconName => "ModRetainingWall";
    public override int SortOrder => 4;
    public override string Subtitle => "Wall breaklines, optional batter";
    public override ModifierDefinition Create(UnitSystem unitSystem) =>
        new RetainingWallModifierDefinition { MaxWallWidth = ModelUnits.FromMeters(1.0, unitSystem) };
    public override void RunBuildStage(ModifierBuildContext context) => RetainingWallStage.Run(context);

    public override string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot) =>
        modifier is RetainingWallModifierDefinition m && NoneResolve(snapshot, m.WallCurves) ? "Not applied — no wall curves selected." : null;

    private static readonly IReadOnlyList<(string Key, string Label)> ModeOptions = new[]
    {
        (RetainingWallModifierDefinition.BreaklineOnlyMode, "Breaklines only"),
        (RetainingWallModifierDefinition.GradeMode, "Grade terrain"),
    };

    private static bool Grades(ModifierDefinition m) =>
        ((RetainingWallModifierDefinition)m).GradesTerrain;

    private static bool GradesAsymmetric(ModifierDefinition m) =>
        Grades(m) && ((RetainingWallModifierDefinition)m).UseAsymmetricSides;

    private static double InheritedCut(ModifierDefinition m)
    {
        var wall = (RetainingWallModifierDefinition)m;
        return wall.CutSlopeAngle > 0.0 ? wall.CutSlopeAngle : wall.SlopeAngle;
    }

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "WallCurves", "Wall Curves",
            m => ((RetainingWallModifierDefinition)m).WallCurves,
            RhinoObjectType.Curve),
        ModifierParam.Number(
            "MaxWallWidth", "Max Wall Width",
            m => ((RetainingWallModifierDefinition)m).MaxWallWidth,
            (m, v) => ((RetainingWallModifierDefinition)m).MaxWallWidth = v,
            "Maximum expected spacing between paired wall rails. Wall cleanup uses an automatic internal tolerance derived from wall width and terrain detail size."),
        ModifierParam.Choice(
            "Mode", "Mode", ModeOptions,
            m => ((RetainingWallModifierDefinition)m).Mode,
            (m, v) => ((RetainingWallModifierDefinition)m).Mode = v ?? RetainingWallModifierDefinition.BreaklineOnlyMode,
            "Breaklines only: the wall rails are forced into the terrain and nothing else changes. Grade terrain: the terrain also batters away from each rail out to daylight. Rail elevations are authoritative in both.",
            rebuildAfterCommit: true),
        ModifierParam.Slope(
            "SlopeAngle", "Fill Slope",
            m => ((RetainingWallModifierDefinition)m).SlopeAngle,
            (m, v) => ((RetainingWallModifierDefinition)m).SlopeAngle = v,
            "Fill slope, used where terrain sits below the rail. The cut slope inherits it unless overridden.",
            visibleWhen: Grades),
        ModifierParam.OptionalSlope(
            "CutSlopeAngle", "Cut Slope",
            m => ((RetainingWallModifierDefinition)m).CutSlopeAngle,
            (m, v) => ((RetainingWallModifierDefinition)m).CutSlopeAngle = v,
            m => ((RetainingWallModifierDefinition)m).SlopeAngle,
            "Cut slope override, used where terrain sits above the rail.",
            visibleWhen: Grades),
        ModifierParam.Bool(
            "UseAsymmetricSides", "Asymmetric Sides",
            m => ((RetainingWallModifierDefinition)m).UseAsymmetricSides,
            (m, v) => ((RetainingWallModifierDefinition)m).UseAsymmetricSides = v,
            "Off: both faces of the wall use the slopes above. On: the toe side and the top side take their own slopes — a wall retaining a pad on one side and daylighting into the bank on the other.",
            visibleWhen: Grades,
            rebuildAfterCommit: true),
        ModifierParam.OptionalSlope(
            "ToeCutSlopeAngle", "Toe Cut Slope",
            m => ((RetainingWallModifierDefinition)m).ToeCutSlopeAngle,
            (m, v) => ((RetainingWallModifierDefinition)m).ToeCutSlopeAngle = v,
            InheritedCut,
            "Cut slope below the wall, running away from the lower rail. Blank inherits the shared cut slope.",
            visibleWhen: GradesAsymmetric),
        ModifierParam.OptionalSlope(
            "ToeFillSlopeAngle", "Toe Fill Slope",
            m => ((RetainingWallModifierDefinition)m).ToeFillSlopeAngle,
            (m, v) => ((RetainingWallModifierDefinition)m).ToeFillSlopeAngle = v,
            m => ((RetainingWallModifierDefinition)m).SlopeAngle,
            "Fill slope below the wall. Blank inherits the shared fill slope.",
            visibleWhen: GradesAsymmetric),
        ModifierParam.OptionalSlope(
            "TopCutSlopeAngle", "Top Cut Slope",
            m => ((RetainingWallModifierDefinition)m).TopCutSlopeAngle,
            (m, v) => ((RetainingWallModifierDefinition)m).TopCutSlopeAngle = v,
            InheritedCut,
            "Cut slope above the wall, running away from the upper rail. Blank inherits the shared cut slope.",
            visibleWhen: GradesAsymmetric),
        ModifierParam.OptionalSlope(
            "TopFillSlopeAngle", "Top Fill Slope",
            m => ((RetainingWallModifierDefinition)m).TopFillSlopeAngle,
            (m, v) => ((RetainingWallModifierDefinition)m).TopFillSlopeAngle = v,
            m => ((RetainingWallModifierDefinition)m).SlopeAngle,
            "Fill slope above the wall. Blank inherits the shared fill slope.",
            visibleWhen: GradesAsymmetric),
        ModifierParam.Number(
            "MaxDistance", "Max Distance",
            m => ((RetainingWallModifierDefinition)m).MaxDistance,
            (m, v) => ((RetainingWallModifierDefinition)m).MaxDistance = v,
            "Maximum grading reach away from a rail. 0 means unlimited; smaller values stop the batter sooner.",
            unit: ParameterUnit.ModelLength,
            visibleWhen: Grades),
    };
}
