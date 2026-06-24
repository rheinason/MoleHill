using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class GradePadModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "grade-pad";
    public override Type DefinitionType => typeof(GradePadModifierDefinition);
    public override string DisplayName => "Grade Pad";
    public override string IconName => "ModGradePad";
    public override int SortOrder => 5;
    public override string Subtitle => "Pad + daylight grading";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new GradePadModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunGradePadStage(context);
}
