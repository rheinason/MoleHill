namespace MoleHill.Rhino.Model;

/// <summary>Traces terrain-conforming downhill paths from point sources.</summary>
public sealed class WaterflowAnalysisDefinition : AnalysisDefinition
{
    public SourceReferenceSet Sources { get; set; } = new();

    /// <summary>Maximum plan length for each path. Zero continues to the edge or a local sink.</summary>
    public double MaxLength { get; set; }

    public string? OutputLayerPath { get; set; }

    public int? ColorArgb { get; set; }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
    }

    public WaterflowAnalysisDefinition()
    {
        Label = "Waterflow from Points";
    }
}
