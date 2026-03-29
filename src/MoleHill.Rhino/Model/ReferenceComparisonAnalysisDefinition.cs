namespace MoleHill.Rhino.Model;

public abstract class ReferenceComparisonAnalysisDefinition : AnalysisDefinition
{
    public SourceReferenceSet Reference { get; set; } = new();

    public SourceReferenceSet Boundary { get; set; } = new();

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Reference;
        yield return Boundary;
    }
}
