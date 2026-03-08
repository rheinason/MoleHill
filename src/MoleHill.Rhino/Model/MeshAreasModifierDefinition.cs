namespace MoleHill.Rhino.Model;

public sealed class MeshAreasModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Boundaries { get; set; } = new();

    public double MaxArea { get; set; }

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
