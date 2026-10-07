namespace MoleHill.Rhino.Model;

public sealed class LongitudinalSectionAnnotationDefinition : TerrainSectionAnnotationDefinitionBase
{
    [ModelLength]
    public double SampleInterval { get; set; } = 1.0;

    public bool ShowElevationGrid { get; set; } = true;

    [ModelLength]
    public double ElevationGridInterval { get; set; }

    public bool ShowStationLabels { get; set; } = true;

    [ModelLength]
    public double StationLabelInterval { get; set; }

    public bool ShowBaseline { get; set; } = true;

    public LongitudinalSectionAnnotationDefinition()
    {
        Label = "Section Along Curve";
    }
}
