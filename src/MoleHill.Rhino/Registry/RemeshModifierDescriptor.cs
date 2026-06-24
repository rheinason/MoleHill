using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class RemeshModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "remesh";
    public override Type DefinitionType => typeof(RemeshModifierDefinition);
    public override string DisplayName => "Remesh";
    public override string IconName => "ModRemesh";
    public override int SortOrder => 2;
    public override string Subtitle => "Constraint-preserving remesh";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new RemeshModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunRemeshStage(context);
}
