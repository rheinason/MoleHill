using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class SmoothModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "smooth";
    public override Type DefinitionType => typeof(SmoothModifierDefinition);
    public override string DisplayName => "Smooth";
    public override string IconName => "ModSmooth";
    public override int SortOrder => 3;
    public override string Subtitle => "Z-only smoothing";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new SmoothModifierDefinition();
}
