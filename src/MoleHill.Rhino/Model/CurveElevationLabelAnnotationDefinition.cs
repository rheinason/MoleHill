namespace MoleHill.Rhino.Model;

public sealed class CurveElevationLabelAnnotationDefinition : BlockAttributeAnnotationDefinition
{
    public double Interval { get; set; } = 10.0;

    public CurveElevationLabelAnnotationDefinition()
    {
        Label = "Spot Heights (Curve)";
        ValueFormat = "F2";
    }
}
