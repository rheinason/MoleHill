namespace MoleHill.Rhino.Model;

public sealed class RemeshModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Constraints { get; set; } = new();

    public double EdgeLength { get; set; }

    public double MaxArea { get; set; }

    public double MinAngle { get; set; } = 20.0;

    public RemeshModifierDefinition()
    {
        Label = "Remesh";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Constraints;
    }
}
