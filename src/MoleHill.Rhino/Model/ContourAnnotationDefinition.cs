using System.Text.Json.Serialization;
namespace MoleHill.Rhino.Model;

public sealed class ContourAnnotationDefinition : AnnotationDefinition
{
    [ModelLength]
    public double Interval { get; set; } = 1.0;

    [ModelLength]
    public double StartZ { get; set; }

    /// <summary>
    /// Pre-schema-30 output layer, kept only so <c>MigrateLayerRouting</c> can carry a customised
    /// path into the document's own layer template. Routing comes from the card's role now.
    /// </summary>
    [JsonPropertyName("outputLayerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOutputLayerPath { get; set; }

    /// <summary>Every Nth contour level is a major (index) contour. 5 is the common survey convention:
    /// a major line every 5 intervals. 1 makes every contour major.</summary>
    public int MajorEveryNth { get; set; } = 5;

    /// <summary>When true, major and minor contours go to separate <c>::Contours::Major</c> and
    /// <c>::Contours::Minor</c> sublayers so per-layer print width and linetype express the hierarchy —
    /// the single most important convention in a terrain drawing. Documents saved before schema 27 keep
    /// their flat single-layer routing.</summary>
    public bool SeparateMajorMinorLayers { get; set; } = true;

    public int? ColorArgb { get; set; }

    /// <summary>When true, elevation text is placed along generated contour curves.</summary>
    public bool ShowLabels { get; set; }

    /// <summary>Spacing between repeated labels along a contour. 0 = one label per contour curve.</summary>
    [ModelLength]
    public double LabelInterval { get; set; }

    /// <summary>Text height for contour labels (model units).</summary>
    [ModelLength]
    public double LabelTextHeight { get; set; } = 1.0;

    /// <summary>Label only every Nth contour level (index contours). 1 = label every level.</summary>
    public int LabelEveryNth { get; set; } = 1;

    /// <summary>Numeric format string applied to contour elevation labels.</summary>
    public string LabelFormat { get; set; } = "F2";

    public ContourAnnotationDefinition()
    {
        Label = "Contours";
    }
}
