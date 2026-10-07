namespace MoleHill.Rhino.Model;

public sealed class CrossSectionStationAnnotationDefinition : TerrainSectionAnnotationDefinitionBase
{
    [ModelLength]
    public double StationInterval { get; set; } = 10.0;

    [ModelLength]
    public double CrossSectionWidth { get; set; } = 10.0;

    public int GridColumns { get; set; } = 4;

    [ModelLength]
    public double GridCellWidth { get; set; }

    [ModelLength]
    public double GridCellHeight { get; set; }

    public bool LabelStations { get; set; } = true;

    public bool ShowCutLinesOnTerrain { get; set; } = true;

    public bool ShowElevationGrid { get; set; }

    [ModelLength]
    public double ElevationGridInterval { get; set; }

    public CrossSectionStationAnnotationDefinition()
    {
        Label = "Cross-Sections";
    }
}
