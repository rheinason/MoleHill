namespace MoleHill.Rhino.Model;

/// <summary>Runtime summary of the resolved mesh output for one named zone.</summary>
public sealed class ZoneAnalysisSummary
{
    public Guid ZoneId { get; set; }

    public double PlanArea { get; set; }

    public double SurfaceArea { get; set; }

    public double ElevationMinZ { get; set; }

    public double ElevationAverageZ { get; set; }

    public double ElevationMaxZ { get; set; }

    public double SlopeMinPercent { get; set; }

    public double SlopeAveragePercent { get; set; }

    public double SlopeMaxPercent { get; set; }

    public int TriangleCount { get; set; }

    public int OutputCount { get; set; }

    public bool HasEarthwork { get; set; }

    public bool EarthworkIsEstimated { get; set; }

    public double CutVolume { get; set; }

    public double FillVolume { get; set; }

    public double NetVolume => CutVolume - FillVolume;
}
