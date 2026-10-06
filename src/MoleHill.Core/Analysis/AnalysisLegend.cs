namespace MoleHill.Core.Analysis;

// The key to a coloured analysis — gradient strip or swatches — built from the same range and palette the
// mesh is painted with. Drawn by the Rhino Legend annotation.

/// <summary>How a legend reads: a continuous strip with ticks, or a list of swatches.</summary>
public enum AnalysisLegendStyle
{
    /// <summary>A smooth ramp, labelled with round values at their place along it.</summary>
    Gradient = 0,

    /// <summary>One flat swatch per band or category, each with its own label.</summary>
    Swatches = 1
}

/// <summary>One swatch: a flat colour and what it means.</summary>
public readonly record struct AnalysisLegendEntry(SlopeAnalyzer.ColorStop Color, string Label);

/// <summary>A label on a gradient strip. <see cref="Position"/> runs 0..1 from the low end.</summary>
public readonly record struct AnalysisLegendTick(double Position, string Label);

/// <summary>
/// What a key to a coloured analysis says, independent of how it is drawn.
///
/// Built from exactly the inputs the mesh is coloured from — the resolved range, the colour mode, the
/// interval and the palette — through <see cref="AnalysisColorMapper"/>, so a drawn legend cannot describe
/// bands the terrain does not have. Swatch entries run from the low end of the range to the high end.
/// </summary>
public sealed class AnalysisLegend
{
    public required AnalysisLegendStyle Style { get; init; }

    /// <summary>Swatches, low to high. Empty for a gradient.</summary>
    public IReadOnlyList<AnalysisLegendEntry> Entries { get; init; } = Array.Empty<AnalysisLegendEntry>();

    /// <summary>The strip's colours, sampled evenly along it with positions 0..1. Empty for swatches.</summary>
    public IReadOnlyList<SlopeAnalyzer.ColorStop> Gradient { get; init; } = Array.Empty<SlopeAnalyzer.ColorStop>();

    /// <summary>Round values along a gradient strip. Empty for swatches.</summary>
    public IReadOnlyList<AnalysisLegendTick> Ticks { get; init; } = Array.Empty<AnalysisLegendTick>();
}

/// <summary>Builds <see cref="AnalysisLegend"/>s. Pure: no geometry, no document, no units of its own.</summary>
public static class AnalysisLegendBuilder
{
    /// <summary>
    /// Colours sampled along a gradient strip. A ramp has at most <see cref="ColorRamp.MaximumStops"/>
    /// stops, and linear interpolation between this many samples is indistinguishable from the palette.
    /// </summary>
    public const int GradientSamples = 64;

    /// <summary>
    /// The key to a ramp-coloured analysis.
    ///
    /// <list type="bullet">
    /// <item><see cref="AnalysisColorMapper.Mode.Gradient"/> draws as a strip with round ticks.</item>
    /// <item><see cref="AnalysisColorMapper.Mode.Stepped"/> lists the bands the mesh was painted with.</item>
    /// <item><see cref="AnalysisColorMapper.Mode.Constant"/> lists one swatch per stop, its edges being the
    /// stops themselves — the unequal thresholds that mode exists to express.</item>
    /// </list>
    ///
    /// The end swatches are labelled open-ended ("&lt; 5", "≥ 20"): values past the range still draw,
    /// clamped to the end colours, so a closed label on the last band would be a claim the mesh does not
    /// honour.
    /// </summary>
    public static AnalysisLegend ForRamp(
        AnalysisRange range,
        AnalysisColorMapper.Mode mode,
        double interval,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette,
        Func<double, string> formatValue)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(formatValue);

        AnalysisRange safe = range.EnsureNonDegenerate();
        return mode switch
        {
            AnalysisColorMapper.Mode.Stepped => Swatches(
                AnalysisColorMapper.ResolveBands(safe, interval, palette)
                    .Select(band => (band.Low, band.High, band.Color))
                    .ToList(),
                formatValue),
            AnalysisColorMapper.Mode.Constant => Swatches(ConstantBands(safe, palette), formatValue),
            _ => Gradient(safe, palette, formatValue)
        };
    }

    /// <summary>The key to a categorical colouring: each swatch named, in the order given.</summary>
    public static AnalysisLegend ForCategories(IEnumerable<AnalysisLegendEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new AnalysisLegend
        {
            Style = AnalysisLegendStyle.Swatches,
            Entries = entries.ToList()
        };
    }

    private static AnalysisLegend Gradient(
        AnalysisRange range,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette,
        Func<double, string> formatValue)
    {
        var samples = new SlopeAnalyzer.ColorStop[GradientSamples];
        for (int i = 0; i < GradientSamples; i++)
        {
            double t = i / (double)(GradientSamples - 1);
            SlopeAnalyzer.ColorStop colour = AnalysisColorMapper.SamplePalette(t, palette);
            samples[i] = new SlopeAnalyzer.ColorStop(t, colour.R, colour.G, colour.B);
        }

        var ticks = AnalysisRange.BuildTicks(range)
            .Select(value => new AnalysisLegendTick(range.Normalize(value), formatValue(value)))
            .ToList();

        return new AnalysisLegend
        {
            Style = AnalysisLegendStyle.Gradient,
            Gradient = samples,
            Ticks = ticks
        };
    }

    /// <summary>
    /// The bands <see cref="AnalysisColorMapper.SampleConstant"/> paints: each stop's colour held from its
    /// position to the next stop's. Below the first stop the first colour holds, so the first band always
    /// starts at the bottom of the range. Coincident stops leave no band of their own — the later one is
    /// the one painted, exactly as the sampler resolves them.
    /// </summary>
    private static List<(double Low, double High, SlopeAnalyzer.ColorStop Color)> ConstantBands(
        AnalysisRange range,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        var bands = new List<(double, double, SlopeAnalyzer.ColorStop)>();
        if (palette.Count == 0)
            return bands;

        // Stable by position: OrderBy keeps the given order among equal positions, and the sampler reads
        // the palette in that order.
        var stops = palette.OrderBy(stop => stop.Position).ToList();
        for (int i = 0; i < stops.Count; i++)
        {
            double lowT = i == 0 ? 0.0 : stops[i].Position;
            double highT = i == stops.Count - 1 ? 1.0 : stops[i + 1].Position;
            if (highT - lowT <= 1e-9)
                continue;

            bands.Add((range.Low + (lowT * range.Span), range.Low + (highT * range.Span), stops[i]));
        }

        return bands;
    }

    private static AnalysisLegend Swatches(
        IReadOnlyList<(double Low, double High, SlopeAnalyzer.ColorStop Color)> bands,
        Func<double, string> formatValue)
    {
        var entries = new List<AnalysisLegendEntry>(bands.Count);
        for (int i = 0; i < bands.Count; i++)
        {
            (double low, double high, SlopeAnalyzer.ColorStop colour) = bands[i];
            string label = bands.Count == 1
                ? $"{formatValue(low)} – {formatValue(high)}"
                : i == 0
                    ? $"< {formatValue(high)}"
                    : i == bands.Count - 1
                        ? $"≥ {formatValue(low)}"
                        : $"{formatValue(low)} – {formatValue(high)}";
            entries.Add(new AnalysisLegendEntry(colour, label));
        }

        return new AnalysisLegend
        {
            Style = AnalysisLegendStyle.Swatches,
            Entries = entries
        };
    }
}
