namespace MoleHill.Rhino.Model;

public sealed class ContourAnalysisDefinition : AnalysisDefinition
{
    public double Interval { get; set; } = 1.0;

    public double StartZ { get; set; }

    public string? OutputLayerPath { get; set; }

    public int? ColorArgb { get; set; }

    public ContourAnalysisDefinition()
    {
        Label = "Contours";
    }
}
