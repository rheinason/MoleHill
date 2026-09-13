namespace MoleHill.Rhino.Model;

/// <summary>
/// What one analysis or annotation measured on the last build, for the panel to read back without
/// recomputing. Runtime display state: it is a flat bag of per-family fields rather than a hierarchy,
/// because the stage runner treats every family alike and only the owning card reads its own fields.
/// </summary>
public sealed class TerrainAnalysisSummary
{
    public Guid AnalysisId { get; set; }

    public double SurfaceArea { get; set; }

    public double SlopeMinPercent { get; set; }

    public double SlopeMaxPercent { get; set; }

    public double SlopeAveragePercent { get; set; }

    public double SlopeDisplayLowPercent { get; set; }

    public double SlopeDisplayHighPercent { get; set; }

    /// <summary>Faces an aspect analysis found too flat to have a direction.</summary>
    public int AspectFlatFaceCount { get; set; }

    /// <summary>Total faces the aspect analysis looked at, so the flat count can be read as a share.</summary>
    public int AspectFaceCount { get; set; }

    /// <summary>
    /// Plan-area-weighted circular mean bearing, in degrees clockwise from the document's north, or null
    /// when there is no mean direction — every face flat, or a symmetric mound whose aspects cancel.
    ///
    /// Nullable, not NaN. This type is persisted with the terrain, and <c>System.Text.Json</c> refuses to
    /// write a non-finite double: a NaN default here stopped *every* terrain carrying *any* analysis
    /// summary from saving, because each summary carries this field whether or not it measured aspect.
    /// Nothing on this type may default to a non-finite value —
    /// <c>TerrainSummarySerializationTests</c> pins that.
    /// </summary>
    public double? AspectDominantBearing { get; set; }

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

    /// <summary>Catchments the drainage routing resolved, after any sliver merging.</summary>
    public int CatchmentBasinCount { get; set; }

    /// <summary>
    /// Catchments with no outlet — closed depressions. Reported here so the catchment card can say that
    /// the terrain holds water somewhere even before a ponding card exists to measure it.
    /// </summary>
    public int CatchmentSinkCount { get; set; }

    /// <summary>
    /// Plan area of the largest catchment, or null when nothing was routed.
    ///
    /// Nullable rather than zero: an empty terrain and a terrain whose largest catchment is genuinely
    /// tiny are different facts, and a zero here would read as the latter. Nullable rather than NaN for
    /// the reason at <see cref="AspectDominantBearing"/> — a non-finite default stops the whole document
    /// saving.
    /// </summary>
    public double? CatchmentLargestArea { get; set; }

    /// <summary>Faces the routing treated as level ground, so the flat threshold can be judged.</summary>
    public int CatchmentFlatFaceCount { get; set; }

    /// <summary>Depressions deep enough to report on the last build.</summary>
    public int PondCount { get; set; }

    /// <summary>
    /// Total impounded volume across every reported pond, or null when none was measured.
    ///
    /// Nullable, not zero: "no depression was found" and "a depression was found that holds nothing" are
    /// different answers, and only one of them is reassuring. Nullable rather than NaN for the reason at
    /// <see cref="AspectDominantBearing"/>.
    /// </summary>
    public double? PondTotalVolume { get; set; }

    /// <summary>Deepest standing water anywhere, or null when nothing was reported.</summary>
    public double? PondMaxDepth { get; set; }

    /// <summary>Total water-surface plan area, or null when nothing was reported.</summary>
    public double? PondTotalArea { get; set; }

    /// <summary>Delta contour curves the cut/fill analysis drew on its last build.</summary>
    public int CutFillDeltaContourCount { get; set; }

    /// <summary>Balance-line curves — the zero crossing of the cut/fill delta — drawn on the last build.</summary>
    public int CutFillBalanceCurveCount { get; set; }

    public double SampleMinValue { get; set; }

    public double SampleMaxValue { get; set; }

    public double SampleAverageValue { get; set; }
}
