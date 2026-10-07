namespace MoleHill.Rhino.Model;

public sealed class MeshAreasModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Boundaries { get; set; } = new();

    [ModelArea]
    public double MaxArea { get; set; }

    [UnitFree("An angle in degrees; angles do not change with model units.")]
    public double MinAngle { get; set; }

    public MeshAreasModifierDefinition()
    {
        Label = "Mesh Areas";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Boundaries;
    }
}
