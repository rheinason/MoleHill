namespace MoleHill.Rhino.Model;

public sealed class CurveElevationLabelAnalysisDefinition : BlockAttributeAnalysisDefinition
{
    public double Interval { get; set; } = 10.0;

    public CurveElevationLabelAnalysisDefinition()
    {
        Label = "Curve Elevation";
        ValueFormat = "F2";
    }
}
