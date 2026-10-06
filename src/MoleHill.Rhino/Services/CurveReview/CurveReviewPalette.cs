// One source of colour for the curve inspector: the panel plot and the viewport ribbon read the same ramp.
using System.Drawing;

namespace MoleHill.Rhino.Services;

/// <summary>The quantity the inspector colours the profile by.</summary>
internal enum CurveReviewMetric
{
    Grade,
    Elevation,
    CutFill,
    PlanRadius
}

/// <summary>
/// A metric sampled along the inspected curve, with the ramp that renders it. The panel plot and the
/// viewport conduit both build one of these, so a stretch that reads red in the graph is the same red on
/// the curve: the two surfaces cannot drift because there is only one ramp.
/// </summary>
internal sealed class CurveReviewMetricSeries
{
    /// <summary>Neutral middle of a diverging ramp — reads as "on grade" against both cut and fill.</summary>
    private static readonly Color NeutralColor = Color.FromArgb(150, 150, 156);

    private static readonly Color GradeOkColor = Color.FromArgb(86, 196, 116);
    private static readonly Color GradeWarnColor = Color.FromArgb(240, 196, 72);
    private static readonly Color GradeOverColor = Color.FromArgb(232, 84, 64);
    private static readonly Color ElevationLowColor = Color.FromArgb(46, 86, 140);
    private static readonly Color ElevationMidColor = Color.FromArgb(108, 168, 152);
    private static readonly Color ElevationHighColor = Color.FromArgb(226, 196, 120);
    private static readonly Color CutColor = Color.FromArgb(214, 104, 84);
    private static readonly Color FillColor = Color.FromArgb(92, 140, 214);

    private readonly double[] _values;
    private readonly bool _diverging;
    private readonly double _scale;

    private CurveReviewMetricSeries(
        CurveReviewMetric metric,
        double[] values,
        double minimum,
        double maximum,
        double scale,
        bool diverging,
        string title,
        string lowLabel,
        string highLabel)
    {
        Metric = metric;
        _values = values;
        _diverging = diverging;
        _scale = scale;
        Minimum = minimum;
        Maximum = maximum;
        Title = title;
        LowLabel = lowLabel;
        HighLabel = highLabel;
    }

    public CurveReviewMetric Metric { get; }
    public double Minimum { get; }
    public double Maximum { get; }

    /// <summary>What the ramp under the plot is labelled with, e.g. "|grade|".</summary>
    public string Title { get; }

    public string LowLabel { get; }
    public string HighLabel { get; }
    public int Count => _values.Length;

    /// <summary>True where the series has no reading, e.g. cut/fill off the terrain.</summary>
    public bool IsAvailable => _values.Any(double.IsFinite);

    public double ValueAt(int index) => index >= 0 && index < _values.Length ? _values[index] : double.NaN;

    /// <summary>Colour for the interval between sample <paramref name="index"/> and the next one.</summary>
    public Color IntervalColor(int index)
    {
        double a = ValueAt(index);
        double b = ValueAt(index + 1);
        if (double.IsFinite(a) && double.IsFinite(b))
            return ColorFor(0.5 * (a + b));
        return ColorFor(double.IsFinite(a) ? a : b);
    }

    /// <summary>Colour for a raw metric value, in the series' own units.</summary>
    public Color ColorFor(double value)
    {
        if (!double.IsFinite(value))
            return _diverging ? NeutralColor : GradeOverColor;

        if (_diverging)
        {
            double signed = Math.Clamp(value / Math.Max(_scale, 1e-9), -1.0, 1.0);
            return signed >= 0.0
                ? Blend(NeutralColor, FillColor, signed)
                : Blend(NeutralColor, CutColor, -signed);
        }

        double t = Math.Clamp((value - Minimum) / Math.Max(Maximum - Minimum, 1e-9), 0.0, 1.0);
        return RampAt(t);
    }

    /// <summary>The ramp itself at normalized position <paramref name="t"/>, for drawing the legend bar.</summary>
    public Color RampAt(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        if (_diverging)
            return t < 0.5 ? Blend(CutColor, NeutralColor, t * 2.0) : Blend(NeutralColor, FillColor, (t - 0.5) * 2.0);

        (Color low, Color mid, Color high) = Metric == CurveReviewMetric.Elevation
            ? (ElevationLowColor, ElevationMidColor, ElevationHighColor)
            : (GradeOkColor, GradeWarnColor, GradeOverColor);
        return t < 0.5 ? Blend(low, mid, t * 2.0) : Blend(mid, high, (t - 0.5) * 2.0);
    }

    public static CurveReviewMetricSeries Build(CurveReviewAnalysis analysis, CurveReviewMetric metric, string unit) => metric switch
    {
        CurveReviewMetric.Elevation => BuildElevation(analysis),
        CurveReviewMetric.CutFill => BuildCutFill(analysis, unit),
        CurveReviewMetric.PlanRadius => BuildPlanRadius(analysis, unit),
        _ => BuildGrade(analysis)
    };

    private static CurveReviewMetricSeries BuildGrade(CurveReviewAnalysis analysis)
    {
        // Grades are per interval; carry the last one onto the closing sample so every sample has a value.
        var values = new double[analysis.Samples.Count];
        for (int i = 0; i < values.Length; i++)
        {
            int interval = Math.Min(i, analysis.IntervalGrades.Count - 1);
            values[i] = interval >= 0 ? Math.Abs(analysis.IntervalGrades[interval]) : double.NaN;
        }

        double scale = analysis.GradeColorScale;
        return new CurveReviewMetricSeries(
            CurveReviewMetric.Grade, values, 0.0, scale, scale, diverging: false,
            "|grade|", "0", Invariant($"{scale:F0}%"));
    }

    private static CurveReviewMetricSeries BuildElevation(CurveReviewAnalysis analysis)
    {
        double[] values = analysis.Samples.Select(sample => sample.Point.Z).ToArray();
        double minimum = values.Length == 0 ? 0.0 : values.Min();
        double maximum = values.Length == 0 ? 1.0 : values.Max();
        return new CurveReviewMetricSeries(
            CurveReviewMetric.Elevation, values, minimum, maximum, maximum - minimum, diverging: false,
            "elevation", Invariant($"{minimum:F1}"), Invariant($"{maximum:F1}"));
    }

    private static CurveReviewMetricSeries BuildCutFill(CurveReviewAnalysis analysis, string unit)
    {
        double[] values = analysis.Samples
            .Select(sample => sample.HasTerrain ? sample.TerrainDelta : double.NaN)
            .ToArray();
        double extent = values.Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty(1.0).Max();
        extent = Math.Max(extent, 1e-6);
        return new CurveReviewMetricSeries(
            CurveReviewMetric.CutFill, values, -extent, extent, extent, diverging: true,
            "cut / fill", Invariant($"cut {extent:F1}{unit}"), Invariant($"fill {extent:F1}{unit}"));
    }

    private static CurveReviewMetricSeries BuildPlanRadius(CurveReviewAnalysis analysis, string unit)
    {
        // Tightness, not radius: a corner (NaN) and a hairpin both belong at the hot end, and a straight
        // (infinite radius) at the cool end. Colouring raw radius would put "straight" off the top of the
        // ramp and squash every real curve into one bucket.
        double reference = analysis.MinimumRadiusLimit is > 0.0
            ? analysis.MinimumRadiusLimit.Value
            : analysis.PlanRadii.Where(double.IsFinite).DefaultIfEmpty(1.0).Min();
        reference = Math.Max(reference, 1e-9);

        var values = new double[analysis.PlanRadii.Count];
        for (int i = 0; i < values.Length; i++)
        {
            double radius = analysis.PlanRadii[i];
            values[i] = double.IsNaN(radius)
                ? 1.0                                       // a true plan corner is as tight as it gets
                : double.IsPositiveInfinity(radius)
                    ? 0.0                                   // straight
                    : Math.Clamp(reference / radius, 0.0, 1.0);
        }

        return new CurveReviewMetricSeries(
            CurveReviewMetric.PlanRadius, values, 0.0, 1.0, 1.0, diverging: false,
            "plan radius", "straight", Invariant($"R {reference:F0}{unit}"));
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            (int)Math.Round(from.R + ((to.R - from.R) * amount)),
            (int)Math.Round(from.G + ((to.G - from.G) * amount)),
            (int)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static string Invariant(FormattableString value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
