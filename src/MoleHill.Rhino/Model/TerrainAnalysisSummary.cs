namespace MoleHill.Rhino.Model;

public sealed class TerrainAnalysisSummary
{
    public Guid AnalysisId { get; set; }

    public double SurfaceArea { get; set; }

    public double SlopeMinPercent { get; set; }

    public double SlopeMaxPercent { get; set; }

    public double SlopeAveragePercent { get; set; }

    public double SlopeDisplayLowPercent { get; set; }

    public double SlopeDisplayHighPercent { get; set; }

    public double ElevationMinZ { get; set; }

    public double ElevationMaxZ { get; set; }

    public double CutFillDisplayAbsMax { get; set; }

    public double CutVolume { get; set; }

    public double FillVolume { get; set; }

    public double NetVolume { get; set; }

    public bool EarthworkIsEstimated { get; set; } = true;

    public int ContourCurveCount { get; set; }

    public int ContourLevelCount { get; set; }

    public double ContourFirstLevel { get; set; }

    public double ContourLastLevel { get; set; }

    public int GeneratedOutputCount { get; set; }

    public int SampleSourceCount { get; set; }

    public int WaterflowBoundaryCount { get; set; }

    public int WaterflowSinkCount { get; set; }

    public int WaterflowRejectedCount { get; set; }

    public double SampleMinValue { get; set; }

    public double SampleMaxValue { get; set; }

    public double SampleAverageValue { get; set; }
}
