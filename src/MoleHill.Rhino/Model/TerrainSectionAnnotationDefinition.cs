namespace MoleHill.Rhino.Model;

public sealed class TerrainSectionAnnotationDefinition : TerrainSectionAnnotationDefinitionBase
{
    [ModelLength]
    public double StationTickInterval { get; set; }

    [ModelLength]
    public double ElevationGridInterval { get; set; }

    public bool ShowStationTicks { get; set; }

    public bool ShowElevationGrid { get; set; }

    public bool ShowStationLabels { get; set; }

    public TerrainSectionAnnotationDefinition()
    {
        Label = "Section Cut";
    }
}
