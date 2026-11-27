// Draws mhEditProfile's live long section: grid, ground, the edited grades and their labels, and the plan link.
using System.Drawing;
using MoleHill.Core.Analysis;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The edit session's overlay. Nothing it draws is in the document: the section exists only while the
/// command runs, so there is no drawing to move, copy or lose the link to. Text is drawn in screen space
/// (a fixed pixel height at any zoom), and labels that would collide on a dense section are skipped by
/// their on-screen spacing rather than by a fixed count.
/// </summary>
internal sealed class ProfileEditConduit : DisplayConduit
{
    private const int ProfileWidth = 3;
    private const int GroundWidth = 2;
    private const int BendSize = 8;
    private const int TextHeight = 12;
    private const double MinGradeLabelPixels = 56.0;
    private const double MinElevationLabelPixels = 44.0;

    private static readonly Color Ink = Color.FromArgb(35, 31, 32);
    private static readonly Color Accent = Color.FromArgb(25, 157, 73);
    private static readonly Color Breach = Color.FromArgb(214, 39, 40);
    private static readonly Color Hot = Color.FromArgb(255, 150, 0);
    private static readonly Color GroundColor = Color.FromArgb(150, 120, 90);
    private static readonly Color GridColor = Color.FromArgb(70, 128, 128, 128);
    private static readonly Color GhostColor = Color.FromArgb(110, 128, 128, 128);
    private static readonly Color LabelColor = Color.FromArgb(70, 70, 70);

    private volatile ProfileEditView? _view;

    public void Show(ProfileEditView view) => _view = view;

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (_view is not { } view)
            return;
        e.IncludeBoundingBox(view.DrawingBounds());
        if (view.Preview is { Count: >= 2 } preview)
            e.IncludeBoundingBox(preview.BoundingBox);
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
        if (_view is not { } view)
            return;
        DisplayPipeline display = e.Display;
        display.PushDepthTesting(false);
        display.PushDepthWriting(false);
        try
        {
            DrawFrame(display, view);
            foreach (double[] run in view.Ground)
                display.DrawPolyline(Polyline(view, run), GroundColor, GroundWidth);
            display.DrawPolyline(Polyline(view, view.Initial), GhostColor, 1);

            for (int i = 0; i < view.Bends.Length - 1; i++)
            {
                Color color = view.SegmentTooSteep[i] ? Breach : i == view.HotSegment ? Hot : Accent;
                display.DrawLine(
                    view.ToWorld(view.Bends[i].Station, view.Bends[i].Elevation),
                    view.ToWorld(view.Bends[i + 1].Station, view.Bends[i + 1].Elevation),
                    color,
                    ProfileWidth);
            }

            if (view.CursorStation is { } station)
                display.DrawDottedLine(view.ToWorld(station, view.GridLow), view.ToWorld(station, view.GridHigh), Hot);

            if (view.Preview is { Count: >= 2 } preview)
                display.DrawPolyline(preview, Accent, 2);
        }
        finally
        {
            display.PopDepthWriting();
            display.PopDepthTesting();
        }
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
        if (_view is not { } view)
            return;
        DisplayPipeline display = e.Display;
        RhinoViewport viewport = e.Viewport;
        display.PushDepthTesting(false);
        display.PushDepthWriting(false);
        try
        {
            DrawFrameLabels(display, viewport, view);
            DrawSegmentLabels(display, viewport, view);

            for (int i = 0; i < view.Bends.Length; i++)
            {
                Point3d point = view.ToWorld(view.Bends[i].Station, view.Bends[i].Elevation);
                bool hot = i == view.HotBend || i == view.SelectedBend;
                Color color = view.BendBreak[i] ? Breach : hot ? Hot : Ink;
                PointStyle style = i == view.SelectedBend ? PointStyle.RoundSimple : PointStyle.RoundControlPoint;
                display.DrawPoint(point, style, hot ? BendSize + 3 : BendSize, color);
            }

            if (view.PlanMarker is { } marker)
                display.DrawPoint(marker, PointStyle.RoundSimple, BendSize + 2, Hot);

            if (view.Readout is { Length: > 0 } lines)
            {
                Point2d at = viewport.WorldToClient(view.ReadoutAt);
                for (int i = 0; i < lines.Length; i++)
                    display.Draw2dText(lines[i], Ink, new Point2d(at.X + 18, at.Y + 16 + i * (TextHeight + 3)), false, TextHeight);
            }
        }
        finally
        {
            display.PopDepthWriting();
            display.PopDepthTesting();
        }
    }

    private static void DrawFrame(DisplayPipeline display, ProfileEditView view)
    {
        for (int k = 0; view.GridLow + k * view.GridStep <= view.GridHigh + view.GridStep * 1e-6; k++)
        {
            double z = view.GridLow + k * view.GridStep;
            display.DrawLine(view.ToWorld(0.0, z), view.ToWorld(view.Length, z), k == 0 ? Ink : GridColor, 1);
        }

        display.DrawLine(view.ToWorld(0.0, view.GridLow), view.ToWorld(0.0, view.GridHigh), Ink, 1);
        display.DrawLine(view.ToWorld(view.Length, view.GridLow), view.ToWorld(view.Length, view.GridHigh), Ink, 1);
        for (double s = view.StationStep; s < view.Length - view.StationStep * 1e-6; s += view.StationStep)
            display.DrawLine(view.ToWorld(s, view.GridLow), view.ToWorld(s, view.GridHigh), GridColor, 1);
    }

    private static void DrawFrameLabels(DisplayPipeline display, RhinoViewport viewport, ProfileEditView view)
    {
        for (int k = 0; k < view.GridLabels.Length; k++)
        {
            Point2d left = viewport.WorldToClient(view.ToWorld(0.0, view.GridLow + k * view.GridStep));
            string text = view.GridLabels[k];
            display.Draw2dText(text, LabelColor, new Point2d(left.X - 6 - text.Length * TextHeight * 0.55, left.Y - TextHeight / 2.0), false, TextHeight);
        }

        double lastX = double.NegativeInfinity;
        for (double s = 0.0; s <= view.Length + view.StationStep * 1e-6; s += view.StationStep)
        {
            Point2d foot = viewport.WorldToClient(view.ToWorld(s, view.GridLow));
            if (foot.X - lastX < 60.0)
                continue;
            display.Draw2dText(view.FormatStation(s), LabelColor, new Point2d(foot.X, foot.Y + 12), true, TextHeight);
            lastX = foot.X;
        }
    }

    /// <summary>A grade above each segment long enough on screen to hold one, and each bend's elevation below it.</summary>
    private static void DrawSegmentLabels(DisplayPipeline display, RhinoViewport viewport, ProfileEditView view)
    {
        for (int i = 0; i < view.Bends.Length - 1; i++)
        {
            Point2d a = viewport.WorldToClient(view.ToWorld(view.Bends[i].Station, view.Bends[i].Elevation));
            Point2d b = viewport.WorldToClient(view.ToWorld(view.Bends[i + 1].Station, view.Bends[i + 1].Elevation));
            if (a.DistanceTo(b) < MinGradeLabelPixels)
                continue;
            var mid = new Point2d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0 - 14.0);
            Color color = view.SegmentTooSteep[i] ? Breach : Ink;
            display.Draw2dText(view.SegmentGrades[i], color, mid, true, TextHeight);
        }

        double lastX = double.NegativeInfinity;
        for (int i = 0; i < view.Bends.Length; i++)
        {
            Point2d at = viewport.WorldToClient(view.ToWorld(view.Bends[i].Station, view.Bends[i].Elevation));
            bool hot = i == view.HotBend || i == view.SelectedBend;
            if (!hot && at.X - lastX < MinElevationLabelPixels)
                continue;
            display.Draw2dText(view.BendElevations[i], hot ? Hot : LabelColor, new Point2d(at.X, at.Y + 16.0), true, TextHeight);
            lastX = at.X;
        }
    }

    private static Polyline Polyline(ProfileEditView view, double[] run)
    {
        var line = new Polyline(run.Length / 2);
        for (int i = 0; i < run.Length / 2; i++)
            line.Add(view.ToWorld(run[i * 2], run[i * 2 + 1]));
        return line;
    }

    private static Polyline Polyline(ProfileEditView view, ProfileBend[] bends)
    {
        var line = new Polyline(bends.Length);
        foreach (ProfileBend bend in bends)
            line.Add(view.ToWorld(bend.Station, bend.Elevation));
        return line;
    }
}
