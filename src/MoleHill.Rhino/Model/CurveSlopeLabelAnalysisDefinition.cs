using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

public sealed class CurveSlopeLabelAnalysisDefinition : BlockAttributeAnalysisDefinition
{
    public double Interval { get; set; } = 10.0;

    public SlopeAnalyzer.SlopeUnit Unit { get; set; } = SlopeAnalyzer.SlopeUnit.Percent;

    public bool FlipDirection { get; set; }

    public CurveSlopeLabelAnalysisDefinition()
    {
        Label = "Spot Slope (Curve)";
        ValueFormat = "F1";
    }
}
