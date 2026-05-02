namespace MoleHill.Rhino.Model;

public sealed class InSituStairModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet ReferenceSurface { get; set; } = new();

    public double RiserHeight { get; set; } = 0.15;

    public double MinTreadDepth { get; set; }

    public double SlopeAngle { get; set; } = 33.0;

    public double MaxDistance { get; set; }

    public bool ShowTreadLabels { get; set; }

    public int? ComputedSurfaceCount { get; set; }

    public string? ComputedTreadDepthSummary { get; set; }

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
