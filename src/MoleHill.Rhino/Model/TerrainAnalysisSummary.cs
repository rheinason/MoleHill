namespace MoleHill.Rhino.Model;

public sealed class TerrainAnalysisSummary
{
    public double SurfaceArea { get; set; }

    public double SlopeMinPercent { get; set; }

    public double SlopeMaxPercent { get; set; }

    public double SlopeAveragePercent { get; set; }

    public double CutVolume { get; set; }

    public double FillVolume { get; set; }

    public double NetVolume { get; set; }

    public bool EarthworkIsEstimated { get; set; } = true;
}
