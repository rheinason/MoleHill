// Profile mode's read-only overlay: grid, scales, ground line and true grades behind the section curve.
using System.Drawing;
using System.Globalization;
using MoleHill.Core.Analysis;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Draws what a long section needs to be read, never anything to edit: the grid and its elevation and station
/// scale, the ground under the curve, and each section segment's true grade (heights are exaggerated in the
/// drawing, so its slopes cannot be read by eye). Editing is the section curve itself, with Rhino's tools.
/// Colours are the user's own Rhino colours: grid lines in the grid colours, text in whichever of black and
/// white reads on the viewport, and only a grade over the Inspect Curve limit in red.
/// </summary>
internal sealed class ProfileModeConduit : DisplayConduit
{
    public static ProfileModeConduit Instance { get; } = new();

    private const int TextHeight = 11;
    private const double MinGradeLabelPixels = 48.0;
    private static readonly Color Breach = Color.FromArgb(200, 30, 30);
    private static readonly Color GroundColor = Color.FromArgb(120, 92, 64);

    private Picture? _picture;
    private string _key = string.Empty;

    private ProfileModeConduit()
    {
        ProfileModeService.SectionsChanged += _ => _key = string.Empty;
    }

    private sealed record Picture(
        ProfileSection Link, double Length, double GridLow, double GridHigh, double GridStep, double StationStep,
        ProfileGroundLine Ground, List<(Point3d A, Point3d B, double Grade)> Segments, double? MaxGrade);

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (Current(e.Display) is not { } picture)
            return;
        e.IncludeBoundingBox(new BoundingBox(
            picture.Link.ToWorld(0.0, picture.GridLow), picture.Link.ToWorld(picture.Length, picture.GridHigh)));
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
        if (Current(e.Display) is not { } picture)
            return;
        DisplayPipeline display = e.Display;
        ProfileSection link = picture.Link;
        Color thin = AppearanceSettings.GridThinLineColor, thick = AppearanceSettings.GridThickLineColor;

        for (double z = picture.GridLow; z <= picture.GridHigh + picture.GridStep * 1e-6; z += picture.GridStep)
            display.DrawLine(link.ToWorld(0.0, z), link.ToWorld(picture.Length, z), Math.Abs(z - picture.GridLow) < 1e-9 ? thick : thin, 1);
        for (double s = 0.0; s <= picture.Length + picture.StationStep * 1e-6; s += picture.StationStep)
            display.DrawLine(link.ToWorld(s, picture.GridLow), link.ToWorld(s, picture.GridHigh), thin, 1);
        display.DrawLine(link.ToWorld(picture.Length, picture.GridLow), link.ToWorld(picture.Length, picture.GridHigh), thick, 1);
        display.DrawLine(link.ToWorld(0.0, picture.GridLow), link.ToWorld(0.0, picture.GridHigh), thick, 1);

        foreach (double[] run in picture.Ground.Runs)
        {
            var line = new Polyline(run.Length / 2);
            for (int i = 0; i < run.Length / 2; i++)
                line.Add(link.ToWorld(run[i * 2], run[i * 2 + 1]));
            display.DrawPolyline(line, GroundColor, 1);
        }
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
        if (Current(e.Display) is not { } picture)
            return;
        DisplayPipeline display = e.Display;
        RhinoViewport viewport = e.Viewport;
        ProfileSection link = picture.Link;
        Color ink = Ink();

        int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(picture.GridStep)), 0, 4);
        for (double z = picture.GridLow; z <= picture.GridHigh + picture.GridStep * 1e-6; z += picture.GridStep)
        {
            Point2d at = viewport.WorldToClient(link.ToWorld(0.0, z));
            string text = z.ToString("F" + decimals, CultureInfo.InvariantCulture);
            display.Draw2dText(text, ink, new Point2d(at.X - 6 - text.Length * TextHeight * 0.55, at.Y - TextHeight / 2.0), false, TextHeight);
        }

        double lastX = double.NegativeInfinity;
        int stationDecimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(picture.StationStep)), 0, 3);
        for (double s = 0.0; s <= picture.Length + picture.StationStep * 1e-6; s += picture.StationStep)
        {
            Point2d foot = viewport.WorldToClient(link.ToWorld(s, picture.GridLow));
            if (foot.X - lastX < 56.0)
                continue;
            display.Draw2dText(s.ToString("F" + stationDecimals, CultureInfo.InvariantCulture), ink, new Point2d(foot.X, foot.Y + 10), true, TextHeight);
            lastX = foot.X;
        }

        foreach ((Point3d a3, Point3d b3, double grade) in picture.Segments)
        {
            Point2d a = viewport.WorldToClient(a3), b = viewport.WorldToClient(b3);
            if (a.DistanceTo(b) < MinGradeLabelPixels || !double.IsFinite(grade))
                continue;
            bool over = picture.MaxGrade is { } limit && Math.Abs(grade) > limit + 1e-9;
            string text = SlopeInput.FormatWithUnit(grade, SlopeUnitPreference.Current, "0.0#");
            display.Draw2dText(text, over ? Breach : ink, new Point2d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0 - 10.0), true, TextHeight);
        }
    }

    /// <summary>Black or white, whichever reads on the viewport background.</summary>
    private static Color Ink()
    {
        Color background = AppearanceSettings.ViewportBackgroundColor;
        double luminance = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B;
        return luminance > 128 ? Color.Black : Color.White;
    }

    private Picture? Current(DisplayPipeline display)
    {
        RhinoDoc? doc = display.Viewport?.ParentView?.Document ?? RhinoDoc.ActiveDoc;
        if (doc == null)
            return null;
        IReadOnlyList<RhinoObject> objects = ProfileModeService.SectionObjects(doc);
        if (objects.Count == 0)
            return null;
        string key = doc.RuntimeSerialNumber + "|" + string.Join(",", objects.Select(o => o.RuntimeSerialNumber)) + "|" + SlopeUnitPreference.Current;
        if (key == _key)
            return _picture;
        _key = key;
        _picture = Build(doc);
        return _picture;
    }

    private static Picture? Build(RhinoDoc doc)
    {
        if (ProfileModeService.Session(doc) is not { } session)
            return null;
        ProfileSection link = session.Link;
        double tolerance = doc.ModelAbsoluteTolerance;
        if (doc.Objects.FindId(link.PlanId)?.Geometry is not Curve plan ||
            !ProfileModeService.TryGetPath(plan, tolerance, out double[] path))
            return null;
        double length = LongSectionProfile.PlanStations(path)[^1];

        var segments = new List<(Point3d, Point3d, double)>();
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        foreach (RhinoObject piece in session.Pieces)
        {
            if (piece.IsHidden || piece.Geometry is not Curve curve || link.ToStations(curve, tolerance) is not { } flat)
                continue;
            for (int i = 0; i < flat.Length / 2; i++)
            {
                low = Math.Min(low, flat[i * 2 + 1]);
                high = Math.Max(high, flat[i * 2 + 1]);
                if (i == 0)
                    continue;
                double run = flat[i * 2] - flat[i * 2 - 2];
                double grade = Math.Abs(run) > tolerance ? (flat[i * 2 + 1] - flat[i * 2 - 1]) / run : double.NaN;
                segments.Add((link.ToWorld(flat[i * 2 - 2], flat[i * 2 - 1]), link.ToWorld(flat[i * 2], flat[i * 2 + 1]), grade));
            }
        }

        ProfileGroundLine ground = ProfileModeService.Ground(doc, link);
        if (ground.Extent() is { } extent)
        {
            low = Math.Min(low, extent.Low);
            high = Math.Max(high, extent.High);
        }

        if (!double.IsFinite(low))
            low = high = link.Frame.Datum;
        // The grid always reaches the frame's datum, so it never jumps away from where the section was placed.
        low = Math.Min(low, link.Frame.Datum);
        double step = AnalysisRange.NiceStep(Math.Max(high - low, tolerance * 100.0) / 6.0);
        double gridLow = Math.Floor(low / step + 1e-9) * step;
        double gridHigh = Math.Ceiling(high / step - 1e-9) * step;
        if (gridHigh - gridLow < step)
            gridHigh = gridLow + step;
        double stationStep = AnalysisRange.NiceStep(length / 8.0);

        CurveReviewRuleSettings rules = CurveReviewRuleStore.Load(doc);
        double? maxGrade = rules.MaximumGradeMode != CurveReviewRuleMode.Off ? rules.MaximumGradePercent / 100.0 : null;
        return new Picture(link, length, gridLow, gridHigh, step, stationStep, ground, segments, maxGrade);
    }
}
