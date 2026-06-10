namespace MoleHill.Rhino.Model;

public sealed class GradePadModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Boundaries { get; set; } = new();

    public SourceReferenceSet LockCurves { get; set; } = new();

    public double SlopeAngle { get; set; } = 33.0;

    /// <summary>Fill-side batter slope in degrees (terrain below the pad). 0 = same as the cut slope.</summary>
    public double FillSlopeAngle { get; set; }

    public double MaxDistance { get; set; }

    public GradePadModifierDefinition()
    {
        Label = "Grade Pad";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Boundaries;
    }
}
