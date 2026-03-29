namespace MoleHill.Rhino.Model;

public abstract class BlockAttributeAnalysisDefinition : AnalysisDefinition
{
    public SourceReferenceSet Sources { get; set; } = new();

    public string? OutputLayerPath { get; set; }

    public int? ColorArgb { get; set; }

    public string? BlockDefinitionName { get; set; }

    public double BlockScale { get; set; } = 1.0;

    public string AttributePrefix { get; set; } = string.Empty;

    public string AttributeSuffix { get; set; } = string.Empty;

    public string ValueFormat { get; set; } = "F1";

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
    }
}
