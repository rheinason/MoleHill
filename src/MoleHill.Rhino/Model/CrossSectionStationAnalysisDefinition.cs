namespace MoleHill.Rhino.Model;

public sealed class CrossSectionStationAnalysisDefinition : TerrainSectionAnalysisDefinitionBase
{
    public double StationInterval { get; set; } = 10.0;

    public double CrossSectionWidth { get; set; } = 10.0;

    public int GridColumns { get; set; } = 4;

    public double GridCellWidth { get; set; }

    public double GridCellHeight { get; set; }

    public bool LabelStations { get; set; } = true;

    public bool ShowCutLinesOnTerrain { get; set; } = true;

    public bool ShowElevationGrid { get; set; }

    public double ElevationGridInterval { get; set; }

    public CrossSectionStationAnalysisDefinition()
    {
        Label = "Cross-Sections";
    }
}
