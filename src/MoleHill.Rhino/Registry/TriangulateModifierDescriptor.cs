using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class TriangulateModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "triangulate";
    public override Type DefinitionType => typeof(TriangulateModifierDefinition);
    public override string DisplayName => "Triangulate";
    public override string IconName => "ModTriangulate";
    public override bool CanCreateFromMenu => false; // pinned base modifier
    public override string Subtitle => "Terrain geometry";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new TriangulateModifierDefinition();
}
