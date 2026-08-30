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

    /// <summary>
    /// Low end of the range currently mapped across the palette, in the analysis's own display unit
    /// (percent/degrees/ratio for slope, model length for elevation and cut/fill). Null before the first
    /// preview colouring. Refreshed whenever the preview mesh is recoloured — including colour-setting
    /// edits that deliberately skip a rebuild — so the legend never describes a stale range.
    /// </summary>
    public double? DisplayRangeLow { get; set; }

    /// <summary>High end of the mapped range. See <see cref="DisplayRangeLow"/>.</summary>
    public double? DisplayRangeHigh { get; set; }

    /// <summary>
    /// The analysed values' distribution across the mapped range, as bars scaled so the tallest is 1.0.
    /// Drawn behind the ramp on the analysis card, which is what turns dragging Min/Max from guesswork
    /// into aiming at the data. Null for analyses that produce no per-face field (contours, waterflow,
    /// sections) — those cards simply draw no histogram.
    /// </summary>
    public double[]? DistributionBins { get; set; }

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

    public int SectionTerrainCount { get; set; }

    public int SectionCutRegionCount { get; set; }

    public int SectionFillRegionCount { get; set; }

    public int WaterflowBoundaryCount { get; set; }

    public int WaterflowSinkCount { get; set; }

    public int WaterflowRejectedCount { get; set; }

    public double SampleMinValue { get; set; }

    public double SampleMaxValue { get; set; }

    public double SampleAverageValue { get; set; }
}
