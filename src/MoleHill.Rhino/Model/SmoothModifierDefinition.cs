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

    /// <summary>
    /// <c>"smooth"</c> evens heights everywhere; <c>"round"</c> rounds only the creases (batter crests and
    /// toes) with a vertical curve of <see cref="RoundingLength"/>. Documents from before the mode existed
    /// read as <c>"smooth"</c> through this initializer.
    /// </summary>
    public string Mode { get; set; } = SmoothModes.Smooth;

    /// <summary>Round mode: the dihedral angle at or above which a fold is a crease.</summary>
    [UnitFree("An angle in degrees; angles do not change with model units.")]
    public double CreaseAngle { get; set; } = 10.0;

    /// <summary>Round mode: the plan length of the vertical curve across each crease (half on each side).</summary>
    [ModelLength]
    public double RoundingLength { get; set; }

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
