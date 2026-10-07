namespace MoleHill.Rhino.Model;

public abstract class ReferenceComparisonAnalysisDefinition : AnalysisDefinition
{
    public SourceReferenceSet Reference { get; set; } = new();

    public SourceReferenceSet Boundary { get; set; } = new();

    /// <summary>
    /// Another MoleHill terrain's finished mesh to treat as existing ground. Optional: <see cref="Reference"/>
    /// can supply the reference instead, and takes precedence when both are set — requiring the user to bake
    /// a comparison terrain to raw Rhino geometry first was an unnecessary detour when it already lives in
    /// the same document. Neither is required: with no reference at all, the comparison falls back to this
    /// terrain's own base triangulation, which is enough to estimate what its modifier stack moved.
    /// </summary>
    public Guid? ReferenceTerrainId { get; set; }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Reference;
        yield return Boundary;
    }

    public override void NormalizeAfterLoad()
    {
        Reference ??= new SourceReferenceSet();
        Boundary ??= new SourceReferenceSet();
    }
}
