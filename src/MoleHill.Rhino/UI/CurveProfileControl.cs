// The inspector's elevation profile: a read-only chart coloured by the selected metric, with a scrub cursor.
using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The elevation profile chart. It reports and it scrubs; it does not edit. Colour comes from
/// <see cref="CurveReviewMetricSeries"/>, the same ramp the viewport conduit draws the curve with, so the
/// graph and the model always agree about what red means.
/// </summary>
internal sealed class CurveProfileControl : Drawable
{
    private const int Left = 40;
    private const int Right = 12;
    private const int Top = 12;

    /// <summary>Station axis labels plus the legend ramp below them.</summary>
    private const int Bottom = 46;

    private const int LegendHeight = 8;
    private static readonly Color TerrainColor = Color.FromArgb(125, 132, 140);

    private CurveReviewAnalysis? _analysis;
    private CurveReviewMetricSeries? _series;
    private CurveReviewMetric _metric = CurveReviewMetric.Grade;
    private string _unit = string.Empty;
    private double? _hoverStation;

    public CurveProfileControl()
    {
        Height = 190;
        MinimumSize = new Size(300, 170);
        BackgroundColor = UiTheme.RampSurface;
        Cursor = Cursors.Crosshair;
        ToolTip = "Move along the profile to scrub the curve in the viewport.";
    }

    /// <summary>Raised as the pointer moves over the plot, so the conduit can slide its viewport marker.</summary>
    public event EventHandler<double?>? HoverStationChanged;

    public CurveReviewMetric Metric
    {
        get => _metric;
        set
        {
            if (_metric == value)
                return;
            _metric = value;
            RebuildSeries();
            Invalidate();
        }
    }

    /// <summary>Model unit abbreviation, used only in the legend's end labels.</summary>
    public string Unit
    {
        get => _unit;
        set
        {
            _unit = value ?? string.Empty;
            RebuildSeries();
            Invalidate();
        }
    }

    public void SetAnalysis(CurveReviewAnalysis? analysis)
    {
        _analysis = analysis;
        RebuildSeries();
        Invalidate();
    }

    private void RebuildSeries() => _series = _analysis == null
        ? null
        : CurveReviewMetricSeries.Build(_analysis, _metric, _unit);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.FillRectangle(UiTheme.RampSurface, 0, 0, Width, Height);
        CurveReviewAnalysis? analysis = _analysis;
        CurveReviewMetricSeries? series = _series;
        if (analysis == null || series == null || analysis.Samples.Count < 2 || PlotWidth <= 0 || PlotHeight <= 0)
        {
            DrawCentered(g, "Pick a curve to inspect", UiTheme.MutedText);
            return;
        }

        ResolveElevationRange(analysis, out double minimumZ, out double maximumZ);
        PaintGrid(g, analysis, minimumZ, maximumZ);
        PaintViolations(g, analysis);
        PaintTerrain(g, analysis, minimumZ, maximumZ);
        PaintProfile(g, analysis, series, minimumZ, maximumZ);
        PaintEvents(g, analysis, minimumZ, maximumZ);
        PaintCursor(g, analysis, minimumZ, maximumZ);
        g.DrawRectangle(UiTheme.RampDivider, Left, Top, PlotWidth, PlotHeight);
        PaintLegend(g, series);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_analysis != null && InsidePlot(e.Location))
        {
            _hoverStation = StationAt(e.Location.X);
            HoverStationChanged?.Invoke(this, _hoverStation);
            Invalidate();
        }
        else if (_hoverStation.HasValue)
        {
            _hoverStation = null;
            HoverStationChanged?.Invoke(this, null);
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverStation.HasValue)
        {
            _hoverStation = null;
            HoverStationChanged?.Invoke(this, null);
            Invalidate();
        }

        base.OnMouseLeave(e);
    }

    private int PlotWidth => Math.Max(0, Width - Left - Right);
    private int PlotHeight => Math.Max(0, Height - Top - Bottom);

    private void PaintGrid(Graphics g, CurveReviewAnalysis analysis, double minimumZ, double maximumZ)
    {
        // SystemFonts.Default returns a shared Eto font; do not dispose it from a paint pass.
        Font font = SystemFonts.Default(SystemFonts.Default().Size - 1f);
        for (int i = 0; i <= 4; i++)
        {
            float y = Top + (PlotHeight * i / 4f);
            g.DrawLine(UiTheme.RampDivider, Left, y, Left + PlotWidth, y);
            double z = maximumZ - ((maximumZ - minimumZ) * i / 4.0);
            g.DrawText(font, UiTheme.MutedText, 3, y - 7, Invariant($"{z:F1}"));
        }

        for (int i = 0; i <= 4; i++)
        {
            float x = Left + (PlotWidth * i / 4f);
            g.DrawLine(UiTheme.RampDivider, x, Top, x, Top + PlotHeight);
            g.DrawText(font, UiTheme.MutedText, x - 8, Top + PlotHeight + 4, Invariant($"{analysis.PlanLength * i / 4.0:F0}"));
        }
    }

    private void PaintViolations(Graphics g, CurveReviewAnalysis analysis)
    {
        IEnumerable<CurveReviewRun> gradeRuns = analysis.MaximumGradeLimit.HasValue
            ? analysis.GradeExceedances
            : Array.Empty<CurveReviewRun>();
        IEnumerable<CurveReviewRun> radiusRuns = analysis.MinimumRadiusLimit.HasValue
            ? analysis.RadiusViolations
            : Array.Empty<CurveReviewRun>();
        var band = new Color(0.86f, 0.32f, 0.25f, 0.16f);
        foreach (CurveReviewRun run in gradeRuns.Concat(radiusRuns))
        {
            float x0 = XAt(run.StartStation, analysis.PlanLength);
            float x1 = XAt(run.EndStation, analysis.PlanLength);
            g.FillRectangle(band, new RectangleF(x0, Top, Math.Max(1f, x1 - x0), PlotHeight));
        }
    }

    private void PaintTerrain(Graphics g, CurveReviewAnalysis analysis, double minimumZ, double maximumZ)
    {
        PointF? previous = null;
        foreach (CurveReviewSample sample in analysis.Samples)
        {
            if (!sample.HasTerrain)
            {
                previous = null;
                continue;
            }

            var point = new PointF(XAt(sample.Station, analysis.PlanLength), YAt(sample.TerrainZ, minimumZ, maximumZ));
            if (previous.HasValue)
                g.DrawLine(TerrainColor, previous.Value, point);
            previous = point;
        }
    }

    private void PaintProfile(
        Graphics g,
        CurveReviewAnalysis analysis,
        CurveReviewMetricSeries series,
        double minimumZ,
        double maximumZ)
    {
        for (int i = 0; i < analysis.Samples.Count - 1; i++)
        {
            CurveReviewSample a = analysis.Samples[i];
            CurveReviewSample b = analysis.Samples[i + 1];
            using var pen = new Pen(ToEto(series.IntervalColor(i)), 2);
            g.DrawLine(
                pen,
                new PointF(XAt(a.Station, analysis.PlanLength), YAt(a.Point.Z, minimumZ, maximumZ)),
                new PointF(XAt(b.Station, analysis.PlanLength), YAt(b.Point.Z, minimumZ, maximumZ)));
        }
    }

    private void PaintEvents(Graphics g, CurveReviewAnalysis analysis, double minimumZ, double maximumZ)
    {
        foreach (CurveReviewEvent item in analysis.Events)
        {
            float x = XAt(item.Station, analysis.PlanLength);
            float y = YAt(item.Point.Z, minimumZ, maximumZ);
            Color color = item.Kind switch
            {
                CurveReviewEventKind.VerticalBreak => Color.FromArgb(196, 96, 32),
                CurveReviewEventKind.SharpRadius or CurveReviewEventKind.PlanCorner => Color.FromArgb(232, 84, 64),
                CurveReviewEventKind.TerrainGap => TerrainColor,
                _ => UiTheme.ActiveBadge
            };
            g.FillEllipse(color, x - 3, y - 3, 6, 6);
        }
    }

    private void PaintCursor(Graphics g, CurveReviewAnalysis analysis, double minimumZ, double maximumZ)
    {
        if (!_hoverStation.HasValue)
            return;

        double station = _hoverStation.Value;
        int index = CurveReviewAnalyzer.NearestSampleIndex(analysis.Samples, station);
        float x = XAt(station, analysis.PlanLength);
        float y = YAt(analysis.Samples[index].Point.Z, minimumZ, maximumZ);
        g.DrawLine(UiTheme.MutedText, x, Top, x, Top + PlotHeight);
        g.FillEllipse(UiTheme.PrimaryText, x - 4, y - 4, 8, 8);

        double grade = index < analysis.IntervalGrades.Count && double.IsFinite(analysis.IntervalGrades[index])
            ? analysis.IntervalGrades[index]
            : 0.0;
        string readout = Invariant(
            $"Sta {analysis.Samples[index].Station:F1}   Z {analysis.Samples[index].Point.Z:F2}   grade {grade:+0.00;-0.00;0.00}%");
        PaintReadout(g, readout, x);
    }

    /// <summary>The scrub readout rides the cursor but stays inside the plot, so it never clips at the ends.</summary>
    private void PaintReadout(Graphics g, string text, float cursorX)
    {
        // SystemFonts.Default returns a shared Eto font; do not dispose it from a paint pass.
        Font font = SystemFonts.Default(SystemFonts.Default().Size - 1f);
        SizeF size = g.MeasureString(font, text);
        float width = size.Width + 10f;
        float x = Math.Clamp(cursorX - (width * 0.5f), Left + 1, Left + PlotWidth - width - 1);
        var box = new RectangleF(x, Top + 3, width, size.Height + 4f);
        g.FillRectangle(new Color(0f, 0f, 0f, 0.55f), box);
        g.DrawRectangle(UiTheme.RampDivider, box);
        g.DrawText(font, Color.FromArgb(238, 238, 238), x + 5f, Top + 5f, text);
    }

    /// <summary>The ramp under the plot: what the profile's colours mean, in the metric's own units.</summary>
    private void PaintLegend(Graphics g, CurveReviewMetricSeries series)
    {
        float top = Height - LegendHeight - 12f;
        // SystemFonts.Default returns a shared Eto font; do not dispose it from a paint pass.
        Font font = SystemFonts.Default(SystemFonts.Default().Size - 1f);
        SizeF titleSize = g.MeasureString(font, series.Title);
        float barLeft = Left + titleSize.Width + 6f;
        SizeF highSize = g.MeasureString(font, series.HighLabel);
        SizeF lowSize = g.MeasureString(font, series.LowLabel);
        float barRight = Left + PlotWidth - highSize.Width - 4f;
        barLeft += lowSize.Width + 4f;
        g.DrawText(font, UiTheme.MutedText, Left, top - 2f, series.Title);
        g.DrawText(font, UiTheme.MutedText, Left + titleSize.Width + 6f, top - 2f, series.LowLabel);
        g.DrawText(font, UiTheme.MutedText, barRight + 4f, top - 2f, series.HighLabel);
        if (barRight <= barLeft)
            return;

        for (float x = barLeft; x < barRight; x += 1f)
        {
            Color color = ToEto(series.RampAt((x - barLeft) / (barRight - barLeft)));
            g.FillRectangle(color, x, top, 1f, LegendHeight);
        }

        g.DrawRectangle(UiTheme.RampDivider, barLeft, top, barRight - barLeft, LegendHeight);
    }

    private void ResolveElevationRange(CurveReviewAnalysis analysis, out double minimum, out double maximum)
    {
        minimum = analysis.Samples.Min(sample => sample.HasTerrain ? Math.Min(sample.Point.Z, sample.TerrainZ) : sample.Point.Z);
        maximum = analysis.Samples.Max(sample => sample.HasTerrain ? Math.Max(sample.Point.Z, sample.TerrainZ) : sample.Point.Z);
        double padding = Math.Max((maximum - minimum) * 0.08, 0.5);
        minimum -= padding;
        maximum += padding;
    }

    private float XAt(double station, double length) =>
        Left + (float)(Math.Clamp(station / Math.Max(length, 1e-9), 0.0, 1.0) * PlotWidth);

    private float YAt(double elevation, double minimum, double maximum) =>
        Top + (float)((maximum - elevation) / Math.Max(maximum - minimum, 1e-9) * PlotHeight);

    private double StationAt(float x) => _analysis == null
        ? 0.0
        : Math.Clamp((x - Left) / Math.Max(1.0, PlotWidth), 0.0, 1.0) * _analysis.PlanLength;

    private bool InsidePlot(PointF point) =>
        point.X >= Left && point.X <= Left + PlotWidth && point.Y >= Top && point.Y <= Top + PlotHeight;

    private static Color ToEto(System.Drawing.Color color) => Color.FromArgb(color.R, color.G, color.B);

    private void DrawCentered(Graphics g, string text, Color color)
    {
        // SystemFonts.Default returns a shared Eto font; do not dispose it from a paint pass.
        Font font = SystemFonts.Default();
        SizeF size = g.MeasureString(font, text);
        g.DrawText(font, color, (Width - size.Width) * 0.5f, (Height - size.Height) * 0.5f, text);
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
