using System.Text.Json.Serialization;

namespace MoleHill.Rhino.Model;

public sealed class RetainingWallModifierDefinition : ModifierDefinition
{
    private double _maxWallWidth = 1.0;
    private bool _maxWallWidthWasSet;

    public SourceReferenceSet WallCurves { get; set; } = new();

    public double MaxWallWidth
    {
        get => _maxWallWidth;
        set
        {
            _maxWallWidth = value;
            _maxWallWidthWasSet = true;
        }
    }

    [JsonPropertyName("tolerance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double LegacyTolerance
    {
        get => 0.0;
        set
        {
            if (!_maxWallWidthWasSet && value > 0.0)
                _maxWallWidth = value;
        }
    }

    /// <summary>
    /// Pre-schema-30 output layer, kept only so <c>MigrateLayerRouting</c> can carry a customised
    /// path into the document's own layer template. Routing comes from the card's role now.
    /// </summary>
    [JsonPropertyName("outputLayerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOutputLayerPath { get; set; }

    public RetainingWallModifierDefinition()
    {
        Label = "Retaining Wall";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return WallCurves;
    }
}
