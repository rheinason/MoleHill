namespace MoleHill.Rhino.Model;

public sealed class ProjectToModifierDefinition : ModifierDefinition
{
    /// <summary>A single Rhino mesh target. Takes precedence over <see cref="TargetTerrainId"/>.</summary>
    public SourceReferenceSet TargetMesh { get; set; } = new();

    /// <summary>A single MoleHill terrain target, used when no Rhino mesh is assigned.</summary>
    public Guid? TargetTerrainId { get; set; }

    /// <summary>Closed XY loops interpreted with even-odd containment, including nested donut holes.</summary>
    public SourceReferenceSet Boundaries { get; set; } = new();

    public double Strength { get; set; } = 1.0;

    public double FeatherDistance { get; set; }

    public ProjectToModifierDefinition()
    {
        Label = "Project To";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return TargetMesh;
        yield return Boundaries;
    }

    public override void NormalizeAfterLoad()
    {
        base.NormalizeAfterLoad();
        TargetMesh ??= new SourceReferenceSet();
        TargetMesh.ReplaceLayers(Array.Empty<string>());
        Boundaries ??= new SourceReferenceSet();
        Strength = Math.Clamp(Strength, 0.0, 1.0);
        FeatherDistance = Math.Max(0.0, FeatherDistance);
        if (TargetTerrainId == Guid.Empty)
            TargetTerrainId = null;
        if (TargetMesh.HasReferences)
            TargetTerrainId = null;
    }
}
