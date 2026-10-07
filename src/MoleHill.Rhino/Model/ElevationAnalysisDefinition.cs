namespace MoleHill.Rhino.Model;

[ModelLengthMembers("RangeLow", "RangeHigh", "ColorInterval")]
public sealed class ElevationAnalysisDefinition : AnalysisDefinition
{
    public ElevationAnalysisDefinition()
    {
        Label = "Elevation";
        RangeLow = 0.0;
        RangeHigh = 0.0;
    }
}
