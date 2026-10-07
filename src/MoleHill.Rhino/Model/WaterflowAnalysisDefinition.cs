using System.Text.Json.Serialization;
namespace MoleHill.Rhino.Model;

/// <summary>Traces terrain-conforming downhill paths from point sources.</summary>
public sealed class WaterflowAnalysisDefinition : AnalysisDefinition
{
    public SourceReferenceSet Sources { get; set; } = new();

    /// <summary>Maximum plan length for each path. Zero continues to the edge or a local sink.</summary>
    [ModelLength]
    public double MaxLength { get; set; }

    /// <summary>
    /// Pre-schema-30 output layer, kept only so <c>MigrateLayerRouting</c> can carry a customised
    /// path into the document's own layer template. Routing comes from the card's role now.
    /// </summary>
    [JsonPropertyName("outputLayerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOutputLayerPath { get; set; }

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
