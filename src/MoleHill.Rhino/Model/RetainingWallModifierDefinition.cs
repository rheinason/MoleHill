using System.Text.Json.Serialization;

namespace MoleHill.Rhino.Model;

public sealed class RetainingWallModifierDefinition : ModifierDefinition
{
    private double _maxWallWidth = 1.0;
    private bool _maxWallWidthWasSet;

    /// <summary>Breaklines only — the historical behaviour, and still the default.</summary>
    public const string BreaklineOnlyMode = "breaklines";

    /// <summary>Insert the rails as breaklines, then batter the terrain away from each rail.</summary>
    public const string GradeMode = "grade";

    public SourceReferenceSet WallCurves { get; set; } = new();

    /// <summary>
    /// Defaults to the breakline-only behaviour, so a document saved before grading existed
    /// deserializes with no <c>mode</c> key and keeps exactly the result it had.
    /// </summary>
    public string Mode { get; set; } = BreaklineOnlyMode;

    /// <summary>Main (fill) batter slope in degrees for the graded mode.</summary>
    public double SlopeAngle { get; set; } = 33.0;

    /// <summary>Cut-side batter slope override in degrees. 0 = inherit <see cref="SlopeAngle"/>.</summary>
    public double CutSlopeAngle { get; set; }

    /// <summary>Opt-in switch for different batters on the toe and top sides.</summary>
    public bool UseAsymmetricSides { get; set; }

    /// <summary>Toe-side (lower rail) overrides in degrees. 0 = inherit the shared pair.</summary>
    public double ToeCutSlopeAngle { get; set; }

    public double ToeFillSlopeAngle { get; set; }

    /// <summary>Top-side (upper rail) overrides in degrees. 0 = inherit the shared pair.</summary>
    public double TopCutSlopeAngle { get; set; }

    public double TopFillSlopeAngle { get; set; }

    public double MaxDistance { get; set; }

    /// <summary>Derived from <see cref="Mode"/>, so it is never written to the document.</summary>
    [JsonIgnore]
    public bool GradesTerrain =>
        string.Equals(Mode, GradeMode, StringComparison.OrdinalIgnoreCase);

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
