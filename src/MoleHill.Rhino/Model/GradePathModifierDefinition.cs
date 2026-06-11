namespace MoleHill.Rhino.Model;

public sealed class GradePathModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Paths { get; set; } = new();

    public double Width { get; set; } = 2.0;

    /// <summary>Main (fill) batter slope in degrees, used where terrain sits below the road. The cut
    /// slope inherits this value unless <see cref="CutSlopeAngle"/> overrides it.</summary>
    public double SlopeAngle { get; set; } = 33.0;

    /// <summary>Cut-side batter slope override in degrees (terrain above the road). 0 = inherit <see cref="SlopeAngle"/>.</summary>
    public double CutSlopeAngle { get; set; }

    public double MaxDistance { get; set; }

    public GradePathModifierDefinition()
    {
        Label = "Grade Path";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Paths;
    }
}
