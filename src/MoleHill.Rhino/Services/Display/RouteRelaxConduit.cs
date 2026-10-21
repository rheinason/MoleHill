// Draws mhRelaxRoute's live band, its pins, the sketch it came from and the stretches it does not fit.
using System.Drawing;
using MoleHill.Core.Routing;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The relax session's overlay. The sketch stays as a faint ghost so the user sees what they drew; the band
/// is drawn thick on the ground and moves as it relaxes; pins are large dots (the one under the cursor
/// highlighted, the one being dragged filled); stretches the route does not fit are marked where they are
/// deepest. Like the suggested-route overlay it draws without depth testing: the band lies on the ground
/// by design and would flicker in and out of the terrain mesh.
/// </summary>
internal sealed class RouteRelaxConduit : DisplayConduit
{
    private const int BandWidth = 4;
    private const int SketchWidth = 2;
    private const int CurveWidth = 1;
    private const int PinSize = 9;
    private const int IssueFontHeight = 13;

    private static readonly Color SketchColor = Color.FromArgb(150, 128, 128, 128);
    private static readonly Color BandColor = Color.FromArgb(25, 157, 73);
    private static readonly Color SettledColor = Color.FromArgb(24, 118, 210);
    private static readonly Color PinColor = Color.FromArgb(35, 31, 32);
    private static readonly Color HotPinColor = Color.FromArgb(255, 170, 0);
    private static readonly Color ClimbColor = Color.FromArgb(214, 39, 40);
    private static readonly Color CutColor = Color.FromArgb(214, 96, 40);
    private static readonly Color FillColor = Color.FromArgb(40, 110, 214);

    private readonly Polyline _sketch;
    private readonly BoundingBox _bounds;
    private volatile RouteRelaxSnapshot? _snapshot;
    private volatile RouteRelaxScore? _score;

    public RouteRelaxConduit(Polyline sketch, BoundingBox domain)
    {
        _sketch = sketch;
        _bounds = domain;
    }

    /// <summary>The pin under the cursor (-1 for none), highlighted.</summary>
    public int HotPin { get; set; } = -1;

    /// <summary>The pin being dragged (-1 for none), filled.</summary>
    public int DraggedPin { get; set; } = -1;

    public void Show(RouteRelaxSnapshot snapshot) => _snapshot = snapshot;

    public void Show(RouteRelaxScore score) => _score = score;

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (_bounds.IsValid)
            e.IncludeBoundingBox(_bounds);
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
        RouteRelaxSnapshot? snapshot = _snapshot;
        RouteRelaxScore? score = _score;
        e.Display.PushDepthTesting(false);
        e.Display.PushDepthWriting(false);
        try
        {
            if (_sketch.Count >= 2)
                e.Display.DrawPolyline(_sketch, SketchColor, SketchWidth);

            if (snapshot is { Band.Count: >= 2 })
                e.Display.DrawPolyline(snapshot.Band, snapshot.Settled ? SettledColor : BandColor, BandWidth);

            // The curve Keep will add, thin over the band, once it is fitted to the settled band.
            if (snapshot is { Settled: true } && score?.Curve is { Count: >= 2 } curve)
                e.Display.DrawPolyline(curve, Color.White, CurveWidth);

            if (IsCurrent(snapshot, score))
                DrawIssueStretches(e.Display, score!);
        }
        finally
        {
            e.Display.PopDepthWriting();
            e.Display.PopDepthTesting();
        }
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
        RouteRelaxSnapshot? snapshot = _snapshot;
        RouteRelaxScore? score = _score;
        e.Display.PushDepthTesting(false);
        e.Display.PushDepthWriting(false);
        try
        {
            if (snapshot != null)
            {
                foreach ((int id, Point3d point) in snapshot.Pins)
                {
                    Color color = id == DraggedPin || id == HotPin ? HotPinColor : PinColor;
                    PointStyle style = id == DraggedPin ? PointStyle.RoundSimple : PointStyle.RoundControlPoint;
                    e.Display.DrawPoint(point, style, id == HotPin || id == DraggedPin ? PinSize + 3 : PinSize, color);
                }
            }

            if (IsCurrent(snapshot, score))
            {
                foreach ((RouteRelaxIssue issue, Point3d where, string tag) in score!.Issues)
                {
                    using var dot = new TextDot(tag, where) { FontHeight = IssueFontHeight };
                    e.Display.DrawDot(dot, IssueColor(issue.Kind), Color.White, Color.White);
                }
            }
        }
        finally
        {
            e.Display.PopDepthWriting();
            e.Display.PopDepthTesting();
        }
    }

    /// <summary>
    /// True when the score describes the band as drawn. While the band moves the score lags it by up to a
    /// quarter second, and a marked stretch drawn from the old band strayed off the new one (live,
    /// 2026-10-17), so marks wait for the score to catch up.
    /// </summary>
    private static bool IsCurrent(RouteRelaxSnapshot? snapshot, RouteRelaxScore? score) =>
        snapshot != null && score != null && score.Version == snapshot.Version;

    /// <summary>The evaluated line coloured where the route leaves the ground deeper than the reporting depth.</summary>
    private static void DrawIssueStretches(DisplayPipeline display, RouteRelaxScore score)
    {
        RouteRelaxEvaluation evaluation = score.Evaluation;
        double[] points = evaluation.PointsXyz;
        foreach ((RouteRelaxIssue issue, _, _) in score.Issues)
        {
            if (issue.Kind == RouteRelaxIssueKind.TooShort)
                continue;

            var stretch = new Polyline();
            double along = 0.0;
            for (int i = 0; i < points.Length / 3; i++)
            {
                if (i > 0)
                {
                    double dx = points[i * 3] - points[(i - 1) * 3], dy = points[(i * 3) + 1] - points[((i - 1) * 3) + 1];
                    along += Math.Sqrt((dx * dx) + (dy * dy));
                }

                if (along >= issue.StartStation - 1e-9 && along <= issue.EndStation + 1e-9)
                    stretch.Add(points[i * 3], points[(i * 3) + 1], evaluation.GroundZ[i]);
            }

            if (stretch.Count >= 2)
                display.DrawPolyline(stretch, IssueColor(issue.Kind), BandWidth + 3);
        }
    }

    private static Color IssueColor(RouteRelaxIssueKind kind) => kind switch
    {
        RouteRelaxIssueKind.Climb or RouteRelaxIssueKind.TooShort or RouteRelaxIssueKind.Crossing => ClimbColor,
        RouteRelaxIssueKind.Cut => CutColor,
        _ => FillColor,
    };
}
