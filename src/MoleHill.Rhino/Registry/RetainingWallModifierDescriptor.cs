using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

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
}
