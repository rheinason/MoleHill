namespace MoleHill.Rhino.Model;

public sealed class SmoothModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Boundaries { get; set; } = new();

    public SourceReferenceSet Breaklines { get; set; } = new();

    public int Iterations { get; set; } = 1;

    [UnitFree("A 0 to 1 blend weight; unitless.")]
    public double Strength { get; set; } = 0.2;

    [UnitFree("A 0 to 1 blend weight; unitless.")]
    public double BreaklineFixity { get; set; } = 1.0;

    public SmoothModifierDefinition()
    {
        Label = "Smooth";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Boundaries;
        yield return Breaklines;
    }
}
