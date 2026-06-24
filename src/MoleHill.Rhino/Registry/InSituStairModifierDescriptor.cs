using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class InSituStairModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "in-situ-stair";
    public override Type DefinitionType => typeof(InSituStairModifierDefinition);
    public override string DisplayName => "In-Situ Stair";
    public override ModifierDefinition Create(UnitSystem unitSystem) =>
        new InSituStairModifierDefinition { RiserHeight = ModelUnits.FromMeters(0.15, unitSystem) };
}
