using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

internal enum PanelButtonIcon
{
    Add,
    Duplicate,
    Delete,
    Clear,
    HideOff,
    HideOn,
    Lock,
    Unlock,
    More,
    ChevronDown,
    ChevronRight,
    TerrainMesh,
    ZoneMeshes,
    AnalysisVisible,
    AnalysisHidden,
    Rebuild,
    ResetBuild,
    Bake
}

/// <summary>
/// Small theme-aware icon set for panel action buttons.
/// </summary>
/// <remarks>
/// Bold filled silhouettes rather than thin outlines — a Blender-style choice, since a hairline reads
/// as a smudge at 16px on a high-DPI panel and a filled shape holds its identity where a thin one
/// dissolves into noise (this bit hardest for <see cref="PanelButtonIcon.Duplicate"/>, <see cref="PanelButtonIcon.ZoneMeshes"/> and the
/// analysis-visibility pair, which used to be two same-weight outlined squares / a bare circle).
/// </remarks>
internal static class PanelButtonIcons
{
    private static readonly Dictionary<IconCacheKey, Bitmap> Cache = new();

    internal static Image Get(PanelButtonIcon icon, bool muted = false, int size = UiMetrics.IconSize)
    {
        var key = new IconCacheKey(icon, muted, UiTheme.IsDark, size);
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var bitmap))
            {
                bitmap = Render(icon, muted, size);
                Cache[key] = bitmap;
            }

            return bitmap;
        }
    }

    internal static void Apply(Button button, PanelButtonIcon icon, bool muted = false)
    {
        button.Text = string.Empty;
        button.Image = Get(icon, muted);
    }

    private static Bitmap Render(PanelButtonIcon icon, bool muted, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppRgba);
        using var g = new Graphics(bitmap);
        g.Clear(Colors.Transparent);
        g.AntiAlias = true;

        // "Muted" fades the same primary-text color rather than swapping to UiTheme.MutedText's separate
        // grey tone — a different hue per state is what made unavailable/off icons (Lock's default
        // unlocked state, a disabled Bake) read as a noticeably different, colder grey than everything
        // else in the same row instead of just a dimmer version of it.
        var color = muted ? new Color(UiTheme.PrimaryText, 0.5f) : UiTheme.PrimaryText;
        float scale = size / 16f;

        var pen = new Pen(color, 2.2f * scale)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round
        };
        var thickPen = new Pen(color, 2.6f * scale)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round
        };

        PointF P(float x, float y) => new(x * scale, y * scale);
        void Stroke(params PointF[] points) => g.DrawLines(pen, points);
        void FillRect(float x, float y, float width, float height) =>
            g.FillRectangle(color, x * scale, y * scale, width * scale, height * scale);
        void FillPoly(params PointF[] points) => g.FillPolygon(color, points);
        Color Faded(float alpha) => new(color, alpha);

        var thinPen = new Pen(color, 1.6f * scale)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round
        };

        // An arc in icon-grid units, with a filled arrowhead at its end pointing along the sweep.
        // Angles are degrees clockwise from +x, as Eto's DrawArc takes them.
        void ArcArrow(float cx, float cy, float r, float startDeg, float sweepDeg)
        {
            g.DrawArc(pen, (cx - r) * scale, (cy - r) * scale, 2 * r * scale, 2 * r * scale, startDeg, sweepDeg);
            double end = (startDeg + sweepDeg) * Math.PI / 180.0;
            float ex = cx + r * (float)Math.Cos(end), ey = cy + r * (float)Math.Sin(end);
            float sign = Math.Sign(sweepDeg);
            float dx = -(float)Math.Sin(end) * sign, dy = (float)Math.Cos(end) * sign;
            FillPoly(P(ex + dx * 2.2f, ey + dy * 2.2f), P(ex - dy * 2.3f - dx * 0.6f, ey + dx * 2.3f - dy * 0.6f), P(ex + dy * 2.3f - dx * 0.6f, ey - dx * 2.3f - dy * 0.6f));
        }

        // An almond eye outline, traced as a polyline so it needs no path data.
        PointF[] EyeOutline()
        {
            var points = new List<PointF>();
            for (int i = 0; i <= 16; i++)
            {
                float t = i / 16f;
                points.Add(P(1.6f + 12.8f * t, 8f - 4.3f * (float)Math.Sin(Math.PI * t)));
            }
            for (int i = 15; i >= 1; i--)
            {
                float t = i / 16f;
                points.Add(P(1.6f + 12.8f * t, 8f + 4.3f * (float)Math.Sin(Math.PI * t)));
            }
            points.Add(points[0]);
            return points.ToArray();
        }

        switch (icon)
        {
            case PanelButtonIcon.Add:
                Stroke(P(3f, 8f), P(13f, 8f));
                Stroke(P(8f, 3f), P(8f, 13f));
                break;

            case PanelButtonIcon.Duplicate:
                // A faded outline behind a solid front copy: stacked, but not the zone-parcel mark.
                g.DrawRectangle(new Pen(Faded(0.55f), 1.6f * scale), 2f * scale, 6f * scale, 8f * scale, 8f * scale);
                FillRect(6f, 2f, 8f, 8f);
                break;

            case PanelButtonIcon.Delete:
                // A bin: lid with a handle, a tapered body, two faded ribs.
                Stroke(P(2.5f, 4f), P(13.5f, 4f));
                g.DrawLines(thinPen, P(6f, 4f), P(6.6f, 2.2f), P(9.4f, 2.2f), P(10f, 4f));
                g.DrawLines(pen, P(4.2f, 5.8f), P(4.9f, 13.8f), P(11.1f, 13.8f), P(11.8f, 5.8f));
                var rib = new Pen(Faded(0.6f), 1.3f * scale) { LineCap = PenLineCap.Round };
                g.DrawLine(rib, P(6.8f, 7.4f), P(7f, 11.8f));
                g.DrawLine(rib, P(9.2f, 7.4f), P(9f, 11.8f));
                break;

            case PanelButtonIcon.Clear:
                Stroke(P(4f, 4f), P(12f, 12f));
                Stroke(P(12f, 4f), P(4f, 12f));
                break;

            case PanelButtonIcon.HideOff:
                // Visible: an open eye with a solid pupil.
                g.DrawLines(thinPen, EyeOutline());
                g.FillEllipse(color, 5.6f * scale, 5.6f * scale, 4.8f * scale, 4.8f * scale);
                break;

            case PanelButtonIcon.HideOn:
                // Hidden: the same eye faded, without its pupil and struck through.
                g.DrawLines(new Pen(Faded(0.6f), 1.6f * scale) { LineJoin = PenLineJoin.Round }, EyeOutline());
                g.DrawLine(thinPen, P(3f, 13f), P(13f, 3f));
                break;

            case PanelButtonIcon.Lock:
                Stroke(P(5, 7), P(5, 5.25f), P(6.1f, 3.65f), P(8, 3.1f), P(9.9f, 3.65f), P(11, 5.25f), P(11, 7));
                FillRect(4.75f, 7, 6.5f, 5.75f);
                g.FillEllipse(Faded(0.9f), 7.1f * scale, 8.85f * scale, 1.8f * scale, 1.8f * scale);
                break;

            case PanelButtonIcon.Unlock:
                // The shackle swings up and to the right, clear of the body, so locked and unlocked differ
                // by silhouette rather than by a couple pixels of one leg.
                Stroke(P(7.5f, 6.4f), P(7.5f, 5.25f), P(8.6f, 3.65f), P(10.5f, 3.1f), P(12.4f, 3.65f), P(13.5f, 5.25f), P(13.5f, 6.6f));
                FillRect(4.75f, 7, 6.5f, 5.75f);
                g.FillEllipse(Faded(0.9f), 7.1f * scale, 8.85f * scale, 1.8f * scale, 1.8f * scale);
                break;

            case PanelButtonIcon.More:
                g.FillEllipse(color, 3f * scale, 7f * scale, 2f * scale, 2f * scale);
                g.FillEllipse(color, 7f * scale, 7f * scale, 2f * scale, 2f * scale);
                g.FillEllipse(color, 11f * scale, 7f * scale, 2f * scale, 2f * scale);
                break;

            case PanelButtonIcon.ChevronDown:
                Stroke(P(4.25f, 6.25f), P(8, 10), P(11.75f, 6.25f));
                break;

            case PanelButtonIcon.ChevronRight:
                Stroke(P(6.25f, 4.25f), P(10, 8), P(6.25f, 11.75f));
                break;

            case PanelButtonIcon.TerrainMesh:
                // A ridge silhouette reads as "terrain" faster than a literal grid rectangle at this size.
                g.FillPolygon(Faded(0.55f), new[] { P(3.2f, 11.6f), P(7.4f, 4.5f), P(11.4f, 11.6f) });
                FillPoly(P(6.6f, 11.6f), P(10.2f, 6f), P(13.2f, 11.6f));
                break;

            case PanelButtonIcon.ZoneMeshes:
                // Distinct from Duplicate's near-equal stack: one large filled zone with a smaller
                // zone offset to the opposite corner, reading as adjacent parcels rather than a copy.
                g.FillRectangle(Faded(0.55f), P(2.6f, 2.6f).X, P(2.6f, 2.6f).Y, 8.2f * scale, 8.2f * scale);
                FillRect(8.4f, 8.4f, 4.8f, 4.8f);
                break;

            case PanelButtonIcon.AnalysisVisible:
                DrawAnalysisBars(g, color, scale);
                break;

            case PanelButtonIcon.AnalysisHidden:
                DrawAnalysisBars(g, color, scale);
                g.DrawLine(thickPen, P(2.5f, 13f), P(13.5f, 2.5f));
                break;

            case PanelButtonIcon.Rebuild:
                // A reload ring. Left unadorned; Reset Build is the one with a return path.
                ArcArrow(8f, 8f, 5f, 110f, 290f);
                break;

            case PanelButtonIcon.ResetBuild:
                // A return arrow: back along the top, round and down, "go back and start again".
                Stroke(P(3.5f, 5f), P(9.5f, 5f));
                g.DrawArc(pen, 5f * scale, 5f * scale, 9f * scale, 9f * scale, -90f, 180f);
                Stroke(P(9.5f, 14f), P(6.5f, 14f));
                Stroke(P(6f, 2.5f), P(3.5f, 5f), P(6f, 7.5f));
                break;

            case PanelButtonIcon.Bake:
                // A framed terrain sent out to the document.
                g.DrawRectangle(thinPen, 1.8f * scale, 1.8f * scale, 9f * scale, 9f * scale);
                FillPoly(P(3.4f, 9.2f), P(6.3f, 4.4f), P(9.2f, 9.2f));
                Stroke(P(10.4f, 10.4f), P(14f, 14f));
                Stroke(P(14f, 10.6f), P(14f, 14f), P(10.6f, 14f));
                break;

        }

        return bitmap;
    }

    private static void DrawAnalysisBars(Graphics g, Color color, float scale)
    {
        void Bar(float x, float top, float width, float height) =>
            g.FillRectangle(color, x * scale, top * scale, width * scale, height * scale);

        Bar(3.4f, 9f, 2.4f, 4f);
        Bar(6.8f, 6f, 2.4f, 7f);
        Bar(10.2f, 3.4f, 2.4f, 9.6f);
    }


    private readonly record struct IconCacheKey(PanelButtonIcon Icon, bool Muted, bool IsDark, int Size);
}
