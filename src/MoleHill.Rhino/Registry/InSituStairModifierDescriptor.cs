using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class InSituStairModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "in-situ-stair";
    public override Type DefinitionType => typeof(InSituStairModifierDefinition);
    public override string DisplayName => "In-Situ Stair";
    public override string IconName => "ModGradePath";
    public override int SortOrder => 7;
    public override string Subtitle => "Support surface + stair Breps";
    public override ModifierDefinition Create(UnitSystem unitSystem) =>
        new InSituStairModifierDefinition { RiserHeight = ModelUnits.FromMeters(0.15, unitSystem) };
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunInSituStairStage(context);

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "ReferenceSurface", "Reference",
            m => ((InSituStairModifierDefinition)m).ReferenceSurface,
            RhinoObjectType.Mesh | RhinoObjectType.Surface | RhinoObjectType.Brep | RhinoObjectType.Extrusion),
        ModifierParam.Number(
            "RiserHeight", "Riser Height",
            m => ((InSituStairModifierDefinition)m).RiserHeight,
            (m, v) => ((InSituStairModifierDefinition)m).RiserHeight = v,
            "Vertical rise per step. The stair modifier derives tread depth from the supplied walkable surface and this riser height.",
            unit: ParameterUnit.ModelLength),
        ModifierParam.Number(
            "MinTreadDepth", "Min Tread Depth",
            m => ((InSituStairModifierDefinition)m).MinTreadDepth,
            (m, v) => ((InSituStairModifierDefinition)m).MinTreadDepth = v,
            "Minimum acceptable derived tread depth. Values greater than 0 color undersized stair solids bright red; 0 disables the warning.",
            unit: ParameterUnit.ModelLength),
        ModifierParam.Slope(
            "SlopeAngle", "Daylight Slope",
            m => ((InSituStairModifierDefinition)m).SlopeAngle,
            (m, v) => ((InSituStairModifierDefinition)m).SlopeAngle = v,
            "Daylight slope where the graded support surface blends back into surrounding terrain."),
        ModifierParam.Number(
            "MaxDistance", "Max Distance",
            m => ((InSituStairModifierDefinition)m).MaxDistance,
            (m, v) => ((InSituStairModifierDefinition)m).MaxDistance = v,
            "Maximum grading reach away from the stair footprint. 0 means unlimited.",
            unit: ParameterUnit.ModelLength),
        ModifierParam.Bool(
            "ShowTreadLabels", "Show Tread Labels",
            m => ((InSituStairModifierDefinition)m).ShowTreadLabels,
            (m, v) => ((InSituStairModifierDefinition)m).ShowTreadLabels = v,
            "Show one viewport tread-depth label per interpreted stair surface."),
        ModifierParam.ReadOnly(
            "ComputedTreadDepthSummary", "Tread Depth",
            m => ((InSituStairModifierDefinition)m).ComputedTreadDepthSummary ?? "(build to compute)",
            "Derived horizontal tread depth summary across the interpreted stair surfaces."),
        ModifierParam.ReadOnly(
            "ComputedStepCountSummary", "Step Count",
            m => ((InSituStairModifierDefinition)m).ComputedStepCountSummary ?? "(build to compute)",
            "Generated tread-count summary across the interpreted stair surfaces."),
    };
}
