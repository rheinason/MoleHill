namespace MoleHill.Rhino.Model;

public sealed class ContourAnalysisDefinition : AnalysisDefinition
{
    public double Interval { get; set; } = 1.0;

    public double StartZ { get; set; }

    public string? OutputLayerPath { get; set; }

    public int? ColorArgb { get; set; }

    /// <summary>When true, elevation text is placed along generated contour curves.</summary>
    public bool ShowLabels { get; set; }

    /// <summary>Spacing between repeated labels along a contour. 0 = one label per contour curve.</summary>
    public double LabelInterval { get; set; }

    /// <summary>Text height for contour labels (model units).</summary>
    public double LabelTextHeight { get; set; } = 1.0;

    /// <summary>Label only every Nth contour level (index contours). 1 = label every level.</summary>
    public int LabelEveryNth { get; set; } = 1;

    /// <summary>Numeric format string applied to contour elevation labels.</summary>
    public string LabelFormat { get; set; } = "F2";

    public ContourAnalysisDefinition()
    {
        Label = "Contours";
    }
}
