using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class AddGeometryModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "add-geometry";
    public override Type DefinitionType => typeof(AddGeometryModifierDefinition);
    public override string DisplayName => "Add Geometry";
    public override string IconName => "ModAddGeometry";
    public override int SortOrder => 1;
    public override string Subtitle => "Add source geometry";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new AddGeometryModifierDefinition();
}
