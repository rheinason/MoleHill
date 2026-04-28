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

    public string? OutputLayerPath { get; set; } = TerrainDefinition.DefaultAuxiliaryLayerPath;

    public RetainingWallModifierDefinition()
    {
        Label = "Retaining Wall";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return WallCurves;
    }
}
