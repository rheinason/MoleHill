namespace MoleHill.Rhino.Model;

public sealed class InSituStairModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet ReferenceSurface { get; set; } = new();

    [ModelLength]
    public double RiserHeight { get; set; } = 0.15;

    [ModelLength]
    public double MinTreadDepth { get; set; }

    [UnitFree("A slope angle; only the reach scales.")]
    public double SlopeAngle { get; set; } = 33.0;

    [ModelLength]
    public double MaxDistance { get; set; }

    public bool ShowTreadLabels { get; set; }

    [NotBuildInput]
    public int? ComputedSurfaceCount { get; set; }

    [NotBuildInput]
    public string? ComputedTreadDepthSummary { get; set; }

    [NotBuildInput]
    public string? ComputedStepCountSummary { get; set; }

    public InSituStairModifierDefinition()
    {
        Label = "In-Situ Stair";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return ReferenceSurface;
    }
}
