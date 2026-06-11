namespace MoleHill.Rhino.Model;

public sealed class GradePadModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Boundaries { get; set; } = new();

    public SourceReferenceSet LockCurves { get; set; } = new();

    /// <summary>Main (fill) batter slope in degrees, used where terrain sits below the pad. The cut
    /// slope inherits this value unless <see cref="CutSlopeAngle"/> overrides it.</summary>
    public double SlopeAngle { get; set; } = 33.0;

    /// <summary>Cut-side batter slope override in degrees (terrain above the pad). 0 = inherit <see cref="SlopeAngle"/>.</summary>
    public double CutSlopeAngle { get; set; }

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
