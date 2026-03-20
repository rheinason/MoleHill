namespace MoleHill.Rhino.Model;

public sealed class SlopeAnalysisDefinition : AnalysisDefinition
{
    public SlopeAnalysisDefinition()
    {
        Label = "Slope";
        RangeLow = 0.0;
        RangeHigh = 0.0;
    }
}
