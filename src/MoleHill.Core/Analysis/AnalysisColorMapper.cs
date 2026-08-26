namespace MoleHill.Core.Analysis;

/// <summary>Shared color classification for terrain analysis previews.</summary>
public static class AnalysisColorMapper
{
    public enum Mode
    {
        Gradient = 0,
        Stepped = 1
    }

    public static SlopeAnalyzer.ColorStop Sample(
        double value,
        double low,
        double high,
        Mode mode,
        double interval,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        if (palette == null || palette.Count == 0)
            return new SlopeAnalyzer.ColorStop(0, 180, 180, 180);

        if (!double.IsFinite(low) || !double.IsFinite(high) || high <= low)
            high = low + 1.0;

        if (double.IsNaN(value))
            return palette[0];
        if (double.IsInfinity(value))
            return palette[^1];

        double classified = value;
        if (mode == Mode.Stepped && double.IsFinite(interval) && interval > 0.0)
        {
            double band = Math.Floor((value - low) / interval);
            classified = low + ((band + 0.5) * interval);
        }

        double t = Math.Clamp((classified - low) / (high - low), 0.0, 1.0);
        for (int i = 1; i < palette.Count; i++)
        {
            var current = palette[i];
            if (t > current.Position)
                continue;

            var previous = palette[i - 1];
            double span = current.Position - previous.Position;
            double local = span <= 1e-9 ? 1.0 : (t - previous.Position) / span;
            return Interpolate(previous, current, local);
        }

        return palette[^1];
    }

    public static double ResolveInterval(double low, double high, double requested)
    {
        if (double.IsFinite(requested) && requested > 0.0)
            return requested;

        double span = Math.Abs(high - low);
        if (!double.IsFinite(span) || span <= 0.0)
            return 1.0;

        double rough = span / 8.0;
        double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(rough)));
        double normalized = rough / magnitude;
        double nice = normalized <= 1.0 ? 1.0 : normalized <= 2.0 ? 2.0 : normalized <= 5.0 ? 5.0 : 10.0;
        return nice * magnitude;
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
