using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class RemeshModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "remesh";
    public override Type DefinitionType => typeof(RemeshModifierDefinition);
    public override string DisplayName => "Remesh";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new RemeshModifierDefinition();
}
