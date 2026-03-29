using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

public sealed class SlopeAnalysisDefinition : AnalysisDefinition
{
    public SlopeAnalyzer.SlopeUnit Unit { get; set; } = SlopeAnalyzer.SlopeUnit.Percent;

    public SlopeAnalysisDefinition()
    {
        Label = "Slope";
        RangeLow = 0.0;
        RangeHigh = 0.0;
    }
}
