using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class GradePadModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "grade-pad";
    public override Type DefinitionType => typeof(GradePadModifierDefinition);
    public override string DisplayName => "Grade Pad";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new GradePadModifierDefinition();
}
