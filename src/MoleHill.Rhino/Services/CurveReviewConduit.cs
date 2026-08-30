// Draws the live curve inspector overlay: grade-colored ribbon, elevation and grade labels, events and terrain ties.
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

[Flags]
internal enum CurveReviewOverlayParts
{
    None = 0,
    GradeRibbon = 1,
    Labels = 2,
    Events = 4,
    Terrain = 8,
    All = GradeRibbon | Labels | Events | Terrain
}

internal sealed class CurveReviewConduit : DisplayConduit
{
    /// <summary>Base pixel widths, before <see cref="LineWeight"/>. The ribbon is the point of the
    /// overlay, so it is drawn as a ribbon and not as a hairline: at the old 3 px it was indistinguishable
    /// from the curve underneath it and the colour coding could not be read.</summary>
    private const int RibbonWidth = 6;

    private const int RibbonViolationWidth = 9;
    private const int GradeRunWidth = 8;
    private const int RadiusRunWidth = 6;
    private const int DrapeWidth = 3;
    private const int TieWidth = 2;
    private const int EventPointSize = 8;

    private const int TerrainTieCount = 24;
    private static readonly Color GradeOkColor = Color.FromArgb(86, 196, 116);
    private static readonly Color GradeWarnColor = Color.FromArgb(240, 196, 72);
    private static readonly Color GradeOverColor = Color.FromArgb(232, 84, 64);
    private static readonly Color LabelTextColor = Color.White;
    private static readonly Color ElevationDotColor = Color.FromArgb(52, 96, 150);
    private static readonly Color GradeDotColor = Color.FromArgb(58, 58, 62);
    private static readonly Color EventDotColor = Color.FromArgb(126, 76, 166);
    private static readonly Color BreakDotColor = Color.FromArgb(196, 96, 32);
    private static readonly Color TerrainLineColor = Color.FromArgb(150, 150, 150);
    private static readonly Color CutColor = Color.FromArgb(214, 104, 84);
    private static readonly Color FillColor = Color.FromArgb(92, 140, 214);

    private CurveReviewAnalysis? _analysis;

    public CurveReviewOverlayParts Parts { get; set; } = CurveReviewOverlayParts.All;

    /// <summary>Multiplier on every drawn width, from the inspector's Weight control.</summary>
    public double LineWeight { get; set; } = 1.0;

    /// <summary>How many stretch labels to draw at most. Labels are text dots in world space, so on a long
    /// alignment they overlap into an unreadable pile long before they run out — the density control is
    /// what makes them legible rather than merely present.</summary>
    public int MaximumSpanLabels { get; set; } = 16;

    /// <summary>The analysis currently drawn, so the labelling pass reads exactly what is on screen.</summary>
    public CurveReviewAnalysis? CurrentAnalysis => _analysis;

    public void SetAnalysis(CurveReviewAnalysis? analysis) => _analysis = analysis;

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        CurveReviewAnalysis? analysis = _analysis;
        if (analysis != null && analysis.Bounds.IsValid)
            e.IncludeBoundingBox(analysis.Bounds);
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        CurveReviewAnalysis? analysis = _analysis;
        if (analysis == null || analysis.Samples.Count < 2)
            return;

        if (Parts.HasFlag(CurveReviewOverlayParts.Terrain))
            DrawTerrainRelation(e, analysis);
        if (Parts.HasFlag(CurveReviewOverlayParts.GradeRibbon))
            DrawGradeRibbon(e, analysis);
        if (Parts.HasFlag(CurveReviewOverlayParts.Labels))
            DrawLabels(e, analysis);
        if (Parts.HasFlag(CurveReviewOverlayParts.Events))
            DrawEvents(e, analysis);
        DrawViolations(e, analysis);
    }

    /// <summary>Recolors the curve interval by interval so the whole grade profile reads at a glance.</summary>
    private void DrawGradeRibbon(DrawEventArgs e, CurveReviewAnalysis analysis)
    {
        double scale = analysis.GradeColorScale;
        double? limit = analysis.MaximumGradeLimit;
        int width = Scale(RibbonWidth);
        int violationWidth = Scale(RibbonViolationWidth);
        for (int i = 0; i < analysis.IntervalGrades.Count; i++)
        {
            double grade = analysis.IntervalGrades[i];
            if (double.IsNaN(grade))
                continue;

            bool exceeds = limit is > 0.0 && Math.Abs(grade) > limit.Value;
            e.Display.DrawLine(
                analysis.Samples[i].Point,
                analysis.Samples[i + 1].Point,
                exceeds ? GradeOverColor : ResolveGradeColor(Math.Abs(grade), scale),
                exceeds ? violationWidth : width);
        }
    }

    /// <summary>A base width scaled by the inspector's weight, clamped to what the pipeline accepts.</summary>
    private int Scale(int baseWidth)
    {
        double weight = double.IsFinite(LineWeight) && LineWeight > 0.0 ? LineWeight : 1.0;
        return (int)Math.Clamp(Math.Round(baseWidth * weight), 1, 32);
    }

    private void DrawLabels(DrawEventArgs e, CurveReviewAnalysis analysis)
    {
        int step = Math.Max(1, (int)Math.Ceiling(analysis.Spans.Count / (double)Math.Max(1, MaximumSpanLabels)));
        double? limit = analysis.MaximumGradeLimit;
        PushAnnotationDepth(e);
        try
        {
            for (int i = 0; i < analysis.Spans.Count; i += step)
            {
                CurveReviewSpan span = analysis.Spans[i];
                bool exceeds = limit is > 0.0 && Math.Abs(span.Grade) > limit.Value;
                e.Display.DrawDot(
                    span.Label,
                    $"{span.Grade:+0.00;-0.00;0.00}%  L {span.PlanLength:F1}",
                    exceeds ? GradeOverColor : GradeDotColor,
                    LabelTextColor);
            }

            // Elevations at span boundaries, offset half a stride from the grade labels. Sampling both on
            // the same stride stacked an elevation dot against every grade dot; interleaving them lets the
            // two readings alternate along the curve instead of fighting for the same patch of screen.
            int elevationOffset = step / 2;
            for (int i = elevationOffset; i < analysis.Spans.Count; i += step)
                e.Display.DrawDot(analysis.Spans[i].Start, $"{analysis.Spans[i].Start.Z:F2}", ElevationDotColor, LabelTextColor);

            // The two ends always carry an elevation, whatever the stride skipped.
            if (analysis.Spans.Count > 0)
            {
                CurveReviewSpan first = analysis.Spans[0];
                CurveReviewSpan last = analysis.Spans[^1];
                e.Display.DrawDot(first.Start, $"{first.Start.Z:F2}", ElevationDotColor, LabelTextColor);
                e.Display.DrawDot(last.End, $"{last.End.Z:F2}", ElevationDotColor, LabelTextColor);
            }
        }
        finally
        {
            PopAnnotationDepth(e);
        }
    }

    private void DrawEvents(DrawEventArgs e, CurveReviewAnalysis analysis)
    {
        PushAnnotationDepth(e);
        try
        {
            foreach (CurveReviewEvent item in analysis.Events)
            {
                Color color = item.Kind switch
                {
                    CurveReviewEventKind.VerticalBreak => BreakDotColor,
                    CurveReviewEventKind.TerrainGap => TerrainLineColor,
                    CurveReviewEventKind.SharpRadius => GradeOverColor,
                    _ => EventDotColor
                };
                e.Display.DrawPoint(item.Point, PointStyle.RoundControlPoint, Scale(EventPointSize), color);
                e.Display.DrawDot(item.Point, item.Label, color, LabelTextColor);
            }
        }
        finally
        {
            PopAnnotationDepth(e);
        }
    }

    /// <summary>Violating stretches always draw, so a failing curve is obvious even with overlays trimmed back.</summary>
    private void DrawViolations(DrawEventArgs e, CurveReviewAnalysis analysis)
    {
        foreach (CurveReviewRun run in analysis.GradeExceedances)
            e.Display.DrawPolyline(run.Path, GradeOverColor, Scale(GradeRunWidth));
        foreach (CurveReviewRun run in analysis.RadiusViolations)
            e.Display.DrawPolyline(run.Path, GradeOverColor, Scale(RadiusRunWidth));

        if (!Parts.HasFlag(CurveReviewOverlayParts.Labels))
            return;

        PushAnnotationDepth(e);
        try
        {
            double gradeLimit = analysis.MaximumGradeLimit ?? 0.0;
            foreach (CurveReviewRun run in analysis.GradeExceedances)
            {
                e.Display.DrawDot(
                    run.PeakPoint,
                    $"{run.PeakValue:F2}% > {gradeLimit:F2}%   {run.StartStation:F1}-{run.EndStation:F1} (L {run.PlanLength:F1})",
                    GradeOverColor,
                    LabelTextColor);
            }

            double radiusLimit = analysis.MinimumRadiusLimit ?? 0.0;
            foreach (CurveReviewRun run in analysis.RadiusViolations)
            {
                e.Display.DrawDot(
                    run.PeakPoint,
                    $"R {run.PeakValue:F1} < {radiusLimit:F1}   {run.StartStation:F1}-{run.EndStation:F1}",
                    GradeOverColor,
                    LabelTextColor);
            }
        }
        finally
        {
            PopAnnotationDepth(e);
        }
    }

    /// <summary>Drapes the terrain under the curve and ties the two together so cut and fill read directly.</summary>
    private void DrawTerrainRelation(DrawEventArgs e, CurveReviewAnalysis analysis)
    {
        if (!analysis.HasTerrain)
            return;

        int drapeWidth = Scale(DrapeWidth);
        int tieWidth = Scale(TieWidth);
        var drape = new List<Point3d>();
        foreach (CurveReviewSample sample in analysis.Samples)
        {
            if (sample.HasTerrain)
            {
                drape.Add(sample.TerrainPoint);
                continue;
            }

            if (drape.Count > 1)
                e.Display.DrawPolyline(drape, TerrainLineColor, drapeWidth);
            drape.Clear();
        }

        if (drape.Count > 1)
            e.Display.DrawPolyline(drape, TerrainLineColor, drapeWidth);

        int step = Math.Max(1, analysis.Samples.Count / TerrainTieCount);
        for (int i = 0; i < analysis.Samples.Count; i += step)
        {
            CurveReviewSample sample = analysis.Samples[i];
            if (!sample.HasTerrain || Math.Abs(sample.TerrainDelta) <= 1e-6)
                continue;

            e.Display.DrawLine(sample.TerrainPoint, sample.Point, sample.TerrainDelta > 0.0 ? FillColor : CutColor, tieWidth);
        }

        PushAnnotationDepth(e);
        try
        {
            if (analysis.MaximumFill > 0.0 && analysis.MaximumFillPoint.IsValid)
            {
                e.Display.DrawDot(
                    analysis.MaximumFillPoint,
                    $"fill {analysis.MaximumFill:F2} @ {analysis.MaximumFillStation:F1}",
                    FillColor,
                    LabelTextColor);
            }

            if (analysis.MaximumCut > 0.0 && analysis.MaximumCutPoint.IsValid)
            {
                e.Display.DrawDot(
                    analysis.MaximumCutPoint,
                    $"cut {analysis.MaximumCut:F2} @ {analysis.MaximumCutStation:F1}",
                    CutColor,
                    LabelTextColor);
            }
        }
        finally
        {
            PopAnnotationDepth(e);
        }
    }

    private static Color ResolveGradeColor(double absoluteGrade, double scale)
    {
        double t = scale <= 0.0 ? 0.0 : Math.Clamp(absoluteGrade / scale, 0.0, 1.0);
        return t < 0.5
            ? Blend(GradeOkColor, GradeWarnColor, t * 2.0)
            : Blend(GradeWarnColor, GradeOverColor, (t - 0.5) * 2.0);
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    private static void PushAnnotationDepth(DrawEventArgs e)
    {
        e.Display.PushDepthTesting(false);
        e.Display.PushDepthWriting(false);
    }

    private static void PopAnnotationDepth(DrawEventArgs e)
    {
        e.Display.PopDepthWriting();
        e.Display.PopDepthTesting();
    }
}
