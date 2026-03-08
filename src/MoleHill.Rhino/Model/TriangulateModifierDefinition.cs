namespace MoleHill.Rhino.Model;

public sealed class TriangulateModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Points { get; set; } = new();

    public SourceReferenceSet Breaklines { get; set; } = new();

    public double Tolerance { get; set; }

    public TriangulateModifierDefinition()
    {
        Label = "Triangulate";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Points;
        yield return Breaklines;
    }
}
