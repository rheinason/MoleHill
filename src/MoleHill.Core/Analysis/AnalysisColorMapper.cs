namespace MoleHill.Core.Analysis;

/// <summary>Shared color classification for terrain analysis previews.</summary>
public static class AnalysisColorMapper
{
    /// <summary>An interval that would produce more bands than this is coarsened until it does not; a
    /// thousand-swatch legend is unreadable and a thousand-band mesh is indistinguishable from a gradient.</summary>
    public const int MaximumBands = 64;

    public enum Mode
    {
        Gradient = 0,

        /// <summary>Equal-width bands across the range, sized by an interval in the analysis's own unit.</summary>
        Stepped = 1,

        /// <summary>
        /// Each stop's colour holds until the next stop, so the stops themselves are the band edges.
        ///
        /// This is what <see cref="Stepped"/> cannot express: bands of *unequal* width. A slope legend
        /// that reads "anything under 1:3 is fine, then three narrow bands of increasingly not fine" is a
        /// set of thresholds, not an even division, and thresholds are exactly what dragging a stop sets.
        /// </summary>
        Constant = 2
    }

    /// <summary>One stepped band: a half-open value interval painted a single flat colour.</summary>
    public readonly record struct Band(double Low, double High, SlopeAnalyzer.ColorStop Color)
    {
        public double Center => (Low + High) * 0.5;
    }

    /// <summary>
    /// Divides a range into stepped bands. A band is one flat colour — the palette sampled at the band's
    /// centre — which is what makes stepped mode legible: the old behaviour snapped a value to its band
    /// centre and then interpolated the gradient anyway, so "bands" came out as a continuous ramp.
    ///
    /// Bands tile the range exactly. The last band absorbs whatever remainder an interval that does not
    /// divide the span leaves over, rather than extending past <see cref="AnalysisRange.High"/>.
    /// </summary>
    public static IReadOnlyList<Band> ResolveBands(
        AnalysisRange range,
        double requestedInterval,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        AnalysisRange safe = range.EnsureNonDegenerate();
        double interval = ResolveInterval(safe.Low, safe.High, requestedInterval);
        double span = safe.Span;

        int count = (int)Math.Ceiling(span / interval - 1e-9);
        if (count < 1)
            count = 1;
        while (count > MaximumBands)
        {
            interval *= 2.0;
            count = (int)Math.Ceiling(span / interval - 1e-9);
        }

        var bands = new Band[count];
        for (int i = 0; i < count; i++)
        {
            double low = safe.Low + (i * interval);
            double high = i == count - 1 ? safe.High : Math.Min(safe.High, low + interval);
            double centerT = safe.Normalize((low + high) * 0.5);
            bands[i] = new Band(low, high, SamplePalette(centerT, palette));
        }

        return bands;
    }

    /// <summary>Index of the band containing a value; the end bands absorb everything beyond the range.</summary>
    public static int FindBand(IReadOnlyList<Band> bands, double value)
    {
        if (bands.Count == 0)
            return -1;
        if (double.IsNaN(value) || double.IsNegativeInfinity(value) || value <= bands[0].High)
            return 0;
        if (double.IsPositiveInfinity(value) || value >= bands[^1].Low)
            return bands.Count - 1;

        for (int i = 0; i < bands.Count; i++)
        {
            if (value < bands[i].High)
                return i;
        }

        return bands.Count - 1;
    }

    public static SlopeAnalyzer.ColorStop Sample(
        double value,
        double low,
        double high,
        Mode mode,
        double interval,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        return Sample(value, new AnalysisRange(low, high, false), mode, interval, palette);
    }

    /// <summary>
    /// Colour for a single value. In stepped mode this resolves the bands per call, so hot loops that
    /// colour thousands of faces should hoist <see cref="ResolveBands"/> and use
    /// <see cref="SampleBanded"/> instead.
    /// </summary>
    public static SlopeAnalyzer.ColorStop Sample(
        double value,
        AnalysisRange range,
        Mode mode,
        double interval,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        if (palette == null || palette.Count == 0)
            return new SlopeAnalyzer.ColorStop(0, 180, 180, 180);

        AnalysisRange safe = range.EnsureNonDegenerate();
        if (mode == Mode.Stepped)
            return SampleBanded(value, ResolveBands(safe, interval, palette));

        double t = NormalizeForSampling(value, safe);
        return mode == Mode.Constant ? SampleConstant(t, palette) : SamplePalette(t, palette);
    }

    /// <summary>Colour for a value against pre-resolved bands.</summary>
    public static SlopeAnalyzer.ColorStop SampleBanded(double value, IReadOnlyList<Band> bands)
    {
        if (bands.Count == 0)
            return new SlopeAnalyzer.ColorStop(0, 180, 180, 180);

        return bands[FindBand(bands, value)].Color;
    }

    /// <summary>
    /// The band width to use: an explicit positive request wins, otherwise a readable automatic step of
    /// roughly an eighth of the span, rounded to a 1/2/5/10 number.
    /// </summary>
    public static double ResolveInterval(double low, double high, double requested)
    {
        if (double.IsFinite(requested) && requested > 0.0)
            return requested;

        double span = Math.Abs(high - low);
        if (!double.IsFinite(span) || span <= 0.0)
            return 1.0;

        return AnalysisRange.NiceStep(span / 8.0);
    }

    /// <summary>Position of a value in 0..1, with the signed infinities pinned to their end of the ramp.</summary>
    private static double NormalizeForSampling(double value, AnalysisRange range)
    {
        if (double.IsNaN(value) || double.IsNegativeInfinity(value))
            return 0.0;
        if (double.IsPositiveInfinity(value))
            return 1.0;

        return range.Normalize(value);
    }

    /// <summary>
    /// Colour for a value against whatever the mode resolved to: pre-resolved bands for
    /// <see cref="Mode.Stepped"/>, otherwise the palette read smoothly or as held steps. Every colouring
    /// path goes through here so a new mode cannot be added to one of them and forgotten in the others.
    /// </summary>
    public static SlopeAnalyzer.ColorStop SampleResolved(
        double value,
        AnalysisRange range,
        Mode mode,
        IReadOnlyList<Band>? bands,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        if (bands != null)
            return SampleBanded(value, bands);

        double t = NormalizeForSampling(value, range);
        return mode == Mode.Constant ? SampleConstant(t, palette) : SamplePalette(t, palette);
    }

    /// <summary>
    /// The palette held constant between stops: the colour of the last stop at or below
    /// <paramref name="t"/>. No interpolation, so a stop is a hard threshold.
    /// </summary>
    public static SlopeAnalyzer.ColorStop SampleConstant(double t, IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        if (palette == null || palette.Count == 0)
            return new SlopeAnalyzer.ColorStop(0, 180, 180, 180);

        double clamped = Math.Clamp(t, 0.0, 1.0);
        var held = palette[0];
        for (int i = 0; i < palette.Count; i++)
        {
            if (palette[i].Position > clamped)
                break;

            held = palette[i];
        }

        return held;
    }

    /// <summary>Pre-resolves the bands a mode needs, or null when it colours straight from the palette.</summary>
    public static IReadOnlyList<Band>? ResolveBandsFor(
        AnalysisRange range,
        Mode mode,
        double interval,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette) =>
        mode == Mode.Stepped ? ResolveBands(range, interval, palette) : null;

    /// <summary>Interpolates the palette at a normalized position.</summary>
    public static SlopeAnalyzer.ColorStop SamplePalette(double t, IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        if (palette == null || palette.Count == 0)
            return new SlopeAnalyzer.ColorStop(0, 180, 180, 180);

        double clamped = Math.Clamp(t, 0.0, 1.0);
        for (int i = 1; i < palette.Count; i++)
        {
            var current = palette[i];
            if (clamped > current.Position)
                continue;

            var previous = palette[i - 1];
            double span = current.Position - previous.Position;
            double local = span <= 1e-9 ? 1.0 : (clamped - previous.Position) / span;
            return Interpolate(previous, current, local);
        }

        return palette[^1];
    }

    private static SlopeAnalyzer.ColorStop Interpolate(
        SlopeAnalyzer.ColorStop a,
        SlopeAnalyzer.ColorStop b,
        double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        return new SlopeAnalyzer.ColorStop(
            0.0,
            (byte)Math.Round(a.R + ((b.R - a.R) * clamped)),
            (byte)Math.Round(a.G + ((b.G - a.G) * clamped)),
            (byte)Math.Round(a.B + ((b.B - a.B) * clamped)));
    }
}
