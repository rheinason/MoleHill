using System.Text.Json.Serialization;
using MoleHill.Shared;
namespace MoleHill.Rhino.Model;

public abstract class BlockAttributeAnnotationDefinition : AnnotationDefinition
{
    public SourceReferenceSet Sources { get; set; } = new();

    /// <summary>
    /// Pre-schema-30 output layer, kept only so <c>MigrateLayerRouting</c> can carry a customised
    /// path into the document's own layer template. Routing comes from the card's role now.
    /// </summary>
    [JsonPropertyName("outputLayerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOutputLayerPath { get; set; }

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

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        Sources ??= new SourceReferenceSet();
        BlockScale = Math.Max(0.01, BlockScale);
        AttributePrefix ??= string.Empty;
        AttributeSuffix ??= string.Empty;
        ValueFormat ??= string.Empty;
    }
}
