// Draws the live curve inspector overlay in front of the scene: metric ribbon, labels, events and terrain ties.
using System.Drawing;
using System.Globalization;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

[Flags]
internal enum CurveReviewOverlayParts
{
    None = 0,
    Ribbon = 1,
    Labels = 2,
    Events = 4,
    Terrain = 8,
    All = Ribbon | Labels | Events | Terrain
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
    private CurveReviewMetricSeries? _series;
    private CurveReviewMetric _metric = CurveReviewMetric.Grade;
    private string _unit = string.Empty;

    public double? HoveredStation { get; set; }

    /// <summary>The quantity the ribbon is coloured by. Shared with the panel plot so both read alike.</summary>
    public CurveReviewMetric Metric
    {
        get => _metric;
        set
        {
            _metric = value;
            RebuildSeries();
        }
    }

    /// <summary>Model unit abbreviation, passed through to the metric series' labels.</summary>
    public string Unit
    {
        get => _unit;
        set
        {
            _unit = value ?? string.Empty;
            RebuildSeries();
        }
    }

    public CurveReviewOverlayParts Parts { get; set; } = CurveReviewOverlayParts.All;

    /// <summary>Multiplier on every drawn width, from the inspector's Weight control.</summary>
    public double LineWeight { get; set; } = 1.0;

    /// <summary>How many stretch labels to draw at most. Labels are text dots in world space, so on a long
    /// alignment they overlap into an unreadable pile long before they run out — the density control is
    /// what makes them legible rather than merely present.</summary>
    public int MaximumSpanLabels { get; set; } = 16;

    /// <summary>The analysis currently drawn, so the labelling pass reads exactly what is on screen.</summary>
    public CurveReviewAnalysis? CurrentAnalysis => _analysis;

    public void SetAnalysis(CurveReviewAnalysis? analysis)
    {
        _analysis = analysis;
        RebuildSeries();
    }

    private void RebuildSeries() => _series = _analysis == null
        ? null
        : CurveReviewMetricSeries.Build(_analysis, _metric, _unit);

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        CurveReviewAnalysis? analysis = _analysis;
        if (analysis != null && analysis.Bounds.IsValid)
            e.IncludeBoundingBox(analysis.Bounds);
    }

    /// <summary>
    /// The overlay draws in the overlay channel, and this is load-bearing. A MoleHill terrain preview is
    /// itself drawn by a conduit (<see cref="TerrainDisplayConduit"/>): its surfaces in
    /// <c>PostDrawObjects</c> and its linework in <c>DrawForeground</c>. Two conduits sharing one channel
    /// paint in registration order, so the terrain painted straight over this overlay — and turning depth
    /// testing off cannot save you from a later painter. Drawing in a channel the terrain conduit does not
    /// use puts the inspector on top whatever the registration order happens to be.
    /// </summary>
    protected override void DrawOverlay(DrawEventArgs e)
    {
        CurveReviewAnalysis? analysis = _analysis;
        if (analysis == null || analysis.Samples.Count < 2)
            return;

        // The whole overlay is annotation, not geometry: an inspected curve that grades the terrain lies
        // in the mesh it generated, so a depth-tested ribbon, drape and scrub marker are swallowed by the
        // very surface the inspector exists to report on. Draw the entire pass in front of the scene.
        PushAnnotationDepth(e);
        try
        {
            if (Parts.HasFlag(CurveReviewOverlayParts.Terrain))
                DrawTerrainRelation(e, analysis);
            if (Parts.HasFlag(CurveReviewOverlayParts.Ribbon))
                DrawRibbon(e, analysis);
            var occupied = new List<Point3d>();
            double collisionDistance = Math.Max(analysis.Bounds.Diagonal.Length * 0.025, 1e-3);
            if (Parts.HasFlag(CurveReviewOverlayParts.Labels))
                DrawLabels(e, analysis, occupied, collisionDistance);
            if (Parts.HasFlag(CurveReviewOverlayParts.Events))
                DrawEvents(e, analysis, occupied, collisionDistance);
            DrawViolations(e, analysis, occupied, collisionDistance);
            DrawScrub(e, analysis);
        }
        finally
        {
            PopAnnotationDepth(e);
        }
    }

    /// <summary>Recolors the curve interval by interval so the whole metric profile reads at a glance.</summary>
    private void DrawRibbon(DrawEventArgs e, CurveReviewAnalysis analysis)
    {
        CurveReviewMetricSeries? series = _series;
        if (series == null)
            return;

        double? limit = analysis.MaximumGradeLimit;
        int width = Scale(RibbonWidth);
        int violationWidth = Scale(RibbonViolationWidth);
        for (int i = 0; i < analysis.IntervalGrades.Count; i++)
        {
            double grade = analysis.IntervalGrades[i];
            if (double.IsNaN(grade))
                continue;

            // An over-limit stretch stays red whatever the ribbon is coloured by: the warning outranks
            // the metric, or switching to Elevation would quietly hide the failing run.
            bool exceeds = limit is > 0.0 && Math.Abs(grade) > limit.Value;
            e.Display.DrawLine(
                analysis.Samples[i].Point,
                analysis.Samples[i + 1].Point,
                exceeds ? GradeOverColor : series.IntervalColor(i),
                exceeds ? violationWidth : width);
        }
    }

    /// <summary>A base width scaled by the inspector's weight, clamped to what the pipeline accepts.</summary>
    private int Scale(int baseWidth)
    {
        double weight = double.IsFinite(LineWeight) && LineWeight > 0.0 ? LineWeight : 1.0;
        return (int)Math.Clamp(Math.Round(baseWidth * weight), 1, 32);
    }

    private void DrawLabels(
        DrawEventArgs e,
        CurveReviewAnalysis analysis,
        List<Point3d> occupied,
        double collisionDistance)
    {
        int step = Math.Max(1, (int)Math.Ceiling(analysis.Spans.Count / (double)Math.Max(1, MaximumSpanLabels)));
        double? limit = analysis.MaximumGradeLimit;
        for (int i = 0; i < analysis.Spans.Count; i += step)
        {
            CurveReviewSpan span = analysis.Spans[i];
            bool exceeds = limit is > 0.0 && Math.Abs(span.Grade) > limit.Value;
            Point3d labelPoint = OffsetLabelPoint(analysis, span.Label, span.End - span.Start, i % 2 == 0 ? 1.0 : -1.0);
            if (!TryOccupy(occupied, labelPoint, collisionDistance))
                continue;
            e.Display.DrawLine(span.Label, labelPoint, exceeds ? GradeOverColor : GradeDotColor, Scale(1));
            e.Display.DrawDot(
                labelPoint,
                Format($"{span.Grade:+0.00;-0.00;0.00}%  L {span.PlanLength:F1}"),
                exceeds ? GradeOverColor : GradeDotColor,
                LabelTextColor);
        }

        // Elevations at span boundaries, offset half a stride from the grade labels. Sampling both on
        // the same stride stacked an elevation dot against every grade dot; interleaving them lets the
        // two readings alternate along the curve instead of fighting for the same patch of screen.
        int elevationOffset = step / 2;
        for (int i = elevationOffset; i < analysis.Spans.Count; i += step)
        {
            CurveReviewSpan span = analysis.Spans[i];
            Point3d labelPoint = OffsetLabelPoint(analysis, span.Start, span.End - span.Start, i % 2 == 0 ? -1.5 : 1.5);
            if (!TryOccupy(occupied, labelPoint, collisionDistance))
                continue;
            e.Display.DrawLine(span.Start, labelPoint, ElevationDotColor, Scale(1));
            e.Display.DrawDot(labelPoint, span.Start.Z.ToString("F2", CultureInfo.InvariantCulture), ElevationDotColor, LabelTextColor);
        }

        // The two ends always carry an elevation, whatever the stride skipped.
        if (analysis.Spans.Count > 0)
        {
            CurveReviewSpan first = analysis.Spans[0];
            CurveReviewSpan last = analysis.Spans[^1];
            DrawOffsetElevation(e, analysis, first.Start, first.End - first.Start, -1.5, occupied, collisionDistance);
            DrawOffsetElevation(e, analysis, last.End, last.End - last.Start, 1.5, occupied, collisionDistance);
        }
    }

    /// <summary>Slides the viewport marker to wherever the pointer is on the panel's profile.</summary>
    private void DrawScrub(DrawEventArgs e, CurveReviewAnalysis analysis)
    {
        if (!HoveredStation.HasValue)
            return;
        int index = 0;
        double nearest = double.MaxValue;
        for (int i = 0; i < analysis.Samples.Count; i++)
        {
            double distance = Math.Abs(analysis.Samples[i].Station - HoveredStation.Value);
            if (distance < nearest)
            {
                nearest = distance;
                index = i;
            }
        }
        CurveReviewSample sample = analysis.Samples[index];
        Vector3d tangent = index >= 0 && index < analysis.Samples.Count - 1
            ? analysis.Samples[index + 1].Point - sample.Point
            : sample.Point - analysis.Samples[Math.Max(0, index - 1)].Point;
        Point3d labelPoint = OffsetLabelPoint(analysis, sample.Point, tangent, 1.0);
        e.Display.DrawLine(sample.Point, labelPoint, LabelTextColor, Scale(1));
        e.Display.DrawPoint(sample.Point, PointStyle.RoundControlPoint, Scale(10), LabelTextColor);
        e.Display.DrawDot(
            labelPoint,
            Format($"Sta {sample.Station:F1}  Z {sample.Point.Z:F2}  grade {GradeAt(analysis, index):+0.00;-0.00;0.00}%"),
            GradeDotColor,
            LabelTextColor);
    }

    private static double GradeAt(CurveReviewAnalysis analysis, int sampleIndex)
    {
        int index = Math.Clamp(sampleIndex, 0, analysis.IntervalGrades.Count - 1);
        return index >= 0 && index < analysis.IntervalGrades.Count && double.IsFinite(analysis.IntervalGrades[index])
            ? analysis.IntervalGrades[index]
            : 0.0;
    }

    private static Point3d OffsetLabelPoint(CurveReviewAnalysis analysis, Point3d anchor, Vector3d tangent, double side)
    {
        tangent.Z = 0.0;
        if (!tangent.Unitize())
            tangent = Vector3d.XAxis;
        var normal = new Vector3d(-tangent.Y, tangent.X, 0.0);
        double offset = Math.Max(analysis.Bounds.Diagonal.Length * 0.018, 1e-3);
        return LiftAnnotation(analysis, anchor + (normal * offset * side));
    }

    /// <summary>Lift annotations slightly in world Z so labels and event markers remain readable over a
    /// triangulated terrain mesh. Leader lines still start at the real geometry point.</summary>
    private static Point3d LiftAnnotation(CurveReviewAnalysis analysis, Point3d point)
    {
        double lift = Math.Max(analysis.Bounds.Diagonal.Length * 0.004, 1e-3);
        return point + (Vector3d.ZAxis * lift);
    }

    private void DrawEvents(
        DrawEventArgs e,
        CurveReviewAnalysis analysis,
        List<Point3d> occupied,
        double collisionDistance)
    {
        foreach (CurveReviewEvent item in analysis.Events)
        {
            Color color = item.Kind switch
            {
                CurveReviewEventKind.VerticalBreak => BreakDotColor,
                CurveReviewEventKind.TerrainGap => TerrainLineColor,
                CurveReviewEventKind.SharpRadius => GradeOverColor,
                CurveReviewEventKind.PlanCorner => GradeOverColor,
                _ => EventDotColor
            };
            e.Display.DrawPoint(LiftAnnotation(analysis, item.Point), PointStyle.RoundControlPoint, Scale(EventPointSize), color);
            int index = CurveReviewAnalyzer.NearestSampleIndex(analysis.Samples, item.Station);
            Vector3d tangent = index < analysis.Samples.Count - 1
                ? analysis.Samples[index + 1].Point - item.Point
                : item.Point - analysis.Samples[Math.Max(0, index - 1)].Point;
            if (!TryFindLabelPoint(
                    analysis, item.Point, tangent, index % 2 == 0 ? 2.0 : -2.0,
                    occupied, collisionDistance, out Point3d labelPoint))
                continue;
            e.Display.DrawLine(item.Point, labelPoint, color, Scale(1));
            e.Display.DrawDot(labelPoint, item.Label, color, LabelTextColor);
        }
    }

    private void DrawOffsetElevation(
        DrawEventArgs e,
        CurveReviewAnalysis analysis,
        Point3d anchor,
        Vector3d tangent,
        double side,
        List<Point3d> occupied,
        double collisionDistance)
    {
        Point3d labelPoint = OffsetLabelPoint(analysis, anchor, tangent, side);
        if (!TryOccupy(occupied, labelPoint, collisionDistance))
            return;
        e.Display.DrawLine(anchor, labelPoint, ElevationDotColor, Scale(1));
        e.Display.DrawDot(labelPoint, anchor.Z.ToString("F2", CultureInfo.InvariantCulture), ElevationDotColor, LabelTextColor);
    }

    private static bool TryOccupy(ICollection<Point3d> occupied, Point3d point, double minimumDistance)
    {
        double squared = minimumDistance * minimumDistance;
        if (occupied.Any(existing => existing.DistanceToSquared(point) < squared))
            return false;
        occupied.Add(point);
        return true;
    }

    private static bool TryFindLabelPoint(
        CurveReviewAnalysis analysis,
        Point3d anchor,
        Vector3d tangent,
        double preferredSide,
        ICollection<Point3d> occupied,
        double minimumDistance,
        out Point3d labelPoint)
    {
        double sign = preferredSide < 0.0 ? -1.0 : 1.0;
        double magnitude = Math.Max(Math.Abs(preferredSide), 1.0);
        double[] candidates = { sign * magnitude, -sign * magnitude, sign * (magnitude + 1.0), -sign * (magnitude + 1.0) };
        foreach (double side in candidates)
        {
            labelPoint = OffsetLabelPoint(analysis, anchor, tangent, side);
            if (TryOccupy(occupied, labelPoint, minimumDistance))
                return true;
        }

        labelPoint = Point3d.Unset;
        return false;
    }

    private static Vector3d RunTangent(CurveReviewRun run) => run.Path.Length > 1
        ? run.Path[^1] - run.Path[0]
        : Vector3d.XAxis;

    private static string Format(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Active warning stretches always draw, so a failing curve is obvious even with overlays trimmed back.</summary>
    private void DrawViolations(
        DrawEventArgs e,
        CurveReviewAnalysis analysis,
        List<Point3d> occupied,
        double collisionDistance)
    {
        if (analysis.MaximumGradeLimit.HasValue)
        {
            foreach (CurveReviewRun run in analysis.GradeExceedances)
                e.Display.DrawPolyline(run.Path, GradeOverColor, Scale(GradeRunWidth));
        }
        if (analysis.MinimumRadiusLimit.HasValue)
        {
            foreach (CurveReviewRun run in analysis.RadiusViolations)
                e.Display.DrawPolyline(run.Path, GradeOverColor, Scale(RadiusRunWidth));
        }

        if (!Parts.HasFlag(CurveReviewOverlayParts.Labels))
            return;

        double gradeLimit = analysis.MaximumGradeLimit ?? 0.0;
        foreach (CurveReviewRun run in analysis.MaximumGradeLimit.HasValue ? analysis.GradeExceedances : Array.Empty<CurveReviewRun>())
        {
            Vector3d tangent = RunTangent(run);
            if (!TryFindLabelPoint(analysis, run.PeakPoint, tangent, 2.5, occupied, collisionDistance, out Point3d labelPoint))
                continue;
            e.Display.DrawLine(run.PeakPoint, labelPoint, GradeOverColor, Scale(1));
            e.Display.DrawDot(
                labelPoint,
                Format($"{run.PeakValue:F2}% > {gradeLimit:F2}%   {run.StartStation:F1}-{run.EndStation:F1} (L {run.PlanLength:F1})"),
                GradeOverColor,
                LabelTextColor);
        }

        double radiusLimit = analysis.MinimumRadiusLimit ?? 0.0;
        foreach (CurveReviewRun run in analysis.MinimumRadiusLimit.HasValue ? analysis.RadiusViolations : Array.Empty<CurveReviewRun>())
        {
            Vector3d tangent = RunTangent(run);
            if (!TryFindLabelPoint(analysis, run.PeakPoint, tangent, -2.5, occupied, collisionDistance, out Point3d labelPoint))
                continue;
            e.Display.DrawLine(run.PeakPoint, labelPoint, GradeOverColor, Scale(1));
            e.Display.DrawDot(
                labelPoint,
                Format($"R {run.PeakValue:F1} < {radiusLimit:F1}   {run.StartStation:F1}-{run.EndStation:F1}"),
                GradeOverColor,
                LabelTextColor);
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

        if (analysis.MaximumFill > 0.0 && analysis.MaximumFillPoint.IsValid)
        {
            e.Display.DrawDot(
                LiftAnnotation(analysis, analysis.MaximumFillPoint),
                Format($"fill {analysis.MaximumFill:F2} @ {analysis.MaximumFillStation:F1}"),
                FillColor,
                LabelTextColor);
        }

        if (analysis.MaximumCut > 0.0 && analysis.MaximumCutPoint.IsValid)
        {
            e.Display.DrawDot(
                LiftAnnotation(analysis, analysis.MaximumCutPoint),
                Format($"cut {analysis.MaximumCut:F2} @ {analysis.MaximumCutStation:F1}"),
                CutColor,
                LabelTextColor);
        }
    }

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
