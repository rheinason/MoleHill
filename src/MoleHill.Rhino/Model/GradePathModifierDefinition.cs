namespace MoleHill.Rhino.Model;

public sealed class GradePathModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Paths { get; set; } = new();

    public double Width { get; set; } = 2.0;

    public double SlopeAngle { get; set; } = 33.0;

    /// <summary>Fill-side batter slope in degrees (terrain below the road). 0 = same as the cut slope.</summary>
    public double FillSlopeAngle { get; set; }

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
