using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class GradePathModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "grade-path";
    public override Type DefinitionType => typeof(GradePathModifierDefinition);
    public override string DisplayName => "Grade Path";
    public override string IconName => "ModGradePath";
    public override int SortOrder => 6;
    public override string Subtitle => "Path corridor grading";
    public override ModifierDefinition Create(UnitSystem unitSystem) =>
        new GradePathModifierDefinition { Width = ModelUnits.FromMeters(2.0, unitSystem) };
}
