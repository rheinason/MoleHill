using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

public sealed class PointSlopeLabelAnalysisDefinition : BlockAttributeAnalysisDefinition
{
    public SlopeAnalyzer.SlopeUnit Unit { get; set; } = SlopeAnalyzer.SlopeUnit.Percent;

    public bool FlipDirection { get; set; }

    public PointSlopeLabelAnalysisDefinition()
    {
        Label = "Spot Slope (Points)";
        ValueFormat = "F1";
    }
}
