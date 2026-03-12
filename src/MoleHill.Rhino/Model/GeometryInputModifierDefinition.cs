namespace MoleHill.Rhino.Model;

public abstract class GeometryInputModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Points { get; set; } = new();

    public SourceReferenceSet Breaklines { get; set; } = new();

    public SourceReferenceSet Contours { get; set; } = new();

    public SourceReferenceSet Boundary { get; set; } = new();

    public double Tolerance { get; set; }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Points;
        yield return Breaklines;
        yield return Contours;
        yield return Boundary;
    }
}
