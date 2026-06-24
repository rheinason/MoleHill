using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class RetainingWallModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "retaining-wall";
    public override Type DefinitionType => typeof(RetainingWallModifierDefinition);
    public override string DisplayName => "Retaining Wall";
    public override ModifierDefinition Create(UnitSystem unitSystem) =>
        new RetainingWallModifierDefinition { MaxWallWidth = ModelUnits.FromMeters(1.0, unitSystem) };
}
