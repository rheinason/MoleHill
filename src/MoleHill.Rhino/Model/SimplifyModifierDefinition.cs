namespace MoleHill.Rhino.Model;

/// <summary>Reduces the incoming terrain to a certified deviation or requested vertex cap.</summary>
public sealed class SimplifyModifierDefinition : ModifierDefinition
{
    public const string MaximumDeviationMode = "maximum-deviation";
    public const string TargetVertexCountMode = "target-count";
    public const string RetainPercentageMode = "retain-percentage";

    public string Mode { get; set; } = MaximumDeviationMode;

    public double MaximumDeviation { get; set; }

    public int TargetVertexCount { get; set; } = 50_000;

    public double RetainPercentage { get; set; } = 50.0;

    public SimplifyModifierDefinition()
    {
        Label = "Simplify";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield break;
    }
}
