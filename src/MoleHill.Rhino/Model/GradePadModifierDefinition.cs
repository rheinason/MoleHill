namespace MoleHill.Rhino.Model;

public sealed class GradePadModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Boundaries { get; set; } = new();

    public SourceReferenceSet LockCurves { get; set; } = new();

    public double SlopeAngle { get; set; } = 33.0;

    public double MaxDistance { get; set; }

    public double MaxArea { get; set; }

    public double MinAngle { get; set; }

    public GradePadModifierDefinition()
    {
        Label = "Grade Pad";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Boundaries;
        yield return LockCurves;
    }
}
