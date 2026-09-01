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
    public override string Subtitle => "Wall breaklines";
    public override ModifierDefinition Create(UnitSystem unitSystem) =>
        new RetainingWallModifierDefinition { MaxWallWidth = ModelUnits.FromMeters(1.0, unitSystem) };
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunRetainingWallStage(context);

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
    };
}
