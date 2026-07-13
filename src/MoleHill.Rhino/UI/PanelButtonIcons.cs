using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

internal enum PanelButtonIcon
{
    Add,
    Duplicate,
    Delete,
    Clear,
    Eye,
    EyeOff,
    Lock,
    Unlock,
    ChevronDown,
    ChevronRight,
    TerrainMesh,
    ZoneMeshes,
    AnalysisVisible,
    AnalysisHidden
}

/// <summary>
/// Small theme-aware line icon set for panel action buttons.
/// </summary>
internal static class PanelButtonIcons
{
    private static readonly Dictionary<IconCacheKey, Bitmap> Cache = new();

    internal static Image Get(PanelButtonIcon icon, bool muted = false, int size = 16)
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

        var color = muted ? UiTheme.MutedText : UiTheme.PrimaryText;
        float scale = size / 16f;
        var pen = new Pen(color, 1.55f * scale);

        PointF P(float x, float y) => new(x * scale, y * scale);
        void Stroke(params PointF[] points) => g.DrawLines(pen, points);
        void Segment(float x1, float y1, float x2, float y2) => Stroke(P(x1, y1), P(x2, y2));
        void Rect(float x, float y, float width, float height) =>
            Stroke(P(x, y), P(x + width, y), P(x + width, y + height), P(x, y + height), P(x, y));

        switch (icon)
        {
            case PanelButtonIcon.Add:
                Segment(8, 3.25f, 8, 12.75f);
                Segment(3.25f, 8, 12.75f, 8);
                break;

            case PanelButtonIcon.Duplicate:
                Rect(6, 5, 6, 6);
                Rect(4, 3, 6, 6);
                break;

            case PanelButtonIcon.Delete:
                // Familiar outline bin: a broad lid and handle remain legible beside the duplicate icon,
                // while the tapered body avoids reading as another small square.
                Stroke(P(3, 4.5f), P(13, 4.5f));
                Stroke(P(6, 4.5f), P(6, 2.75f), P(10, 2.75f), P(10, 4.5f));
                Stroke(P(4.25f, 5.75f), P(5.15f, 13), P(10.85f, 13), P(11.75f, 5.75f));
                Segment(7, 6.75f, 7.25f, 11.75f);
                Segment(9, 6.75f, 8.75f, 11.75f);
                break;

            case PanelButtonIcon.Clear:
                Segment(5, 5, 11, 11);
                Segment(11, 5, 5, 11);
                break;

            case PanelButtonIcon.Eye:
                DrawEye(g, pen, color, scale, slashed: false);
                break;

            case PanelButtonIcon.EyeOff:
                DrawEye(g, pen, color, scale, slashed: true);
                break;

            case PanelButtonIcon.Lock:
                Stroke(P(5, 7), P(5, 5.25f), P(6.1f, 3.65f), P(8, 3.1f), P(9.9f, 3.65f), P(11, 5.25f), P(11, 7));
                Rect(4.75f, 7, 6.5f, 5.75f);
                g.FillEllipse(color, 7.25f * scale, 9f * scale, 1.5f * scale, 1.5f * scale);
                break;

            case PanelButtonIcon.Unlock:
                Stroke(P(5, 7), P(5, 5.25f), P(6.1f, 3.65f), P(8, 3.1f), P(9.9f, 3.65f), P(11, 5.25f));
                Rect(4.75f, 7, 6.5f, 5.75f);
                g.FillEllipse(color, 7.25f * scale, 9f * scale, 1.5f * scale, 1.5f * scale);
                break;

            case PanelButtonIcon.ChevronDown:
                Stroke(P(4.25f, 6.25f), P(8, 10), P(11.75f, 6.25f));
                break;

            case PanelButtonIcon.ChevronRight:
                Stroke(P(6.25f, 4.25f), P(10, 8), P(6.25f, 11.75f));
                break;

            case PanelButtonIcon.TerrainMesh:
                Rect(3.5f, 4.25f, 9, 7.5f);
                Segment(3.5f, 8, 12.5f, 8);
                Segment(8, 4.25f, 8, 11.75f);
                break;

            case PanelButtonIcon.ZoneMeshes:
                Rect(3, 3, 4.25f, 4.25f);
                Rect(8.75f, 3, 4.25f, 4.25f);
                Rect(3, 8.75f, 4.25f, 4.25f);
                Rect(8.75f, 8.75f, 4.25f, 4.25f);
                break;

            case PanelButtonIcon.AnalysisVisible:
                g.FillEllipse(color, 4.25f * scale, 4.25f * scale, 7.5f * scale, 7.5f * scale);
                break;

            case PanelButtonIcon.AnalysisHidden:
                g.DrawEllipse(pen, 4.25f * scale, 4.25f * scale, 7.5f * scale, 7.5f * scale);
                break;
        }

        return bitmap;
    }

    private static void DrawEye(Graphics g, Pen pen, Color color, float scale, bool slashed)
    {
        PointF P(float x, float y) => new(x * scale, y * scale);
        g.DrawLines(pen, new[]
        {
            P(2.25f, 8),
            P(4.8f, 5.4f),
            P(8, 4.4f),
            P(11.2f, 5.4f),
            P(13.75f, 8),
            P(11.2f, 10.6f),
            P(8, 11.6f),
            P(4.8f, 10.6f),
            P(2.25f, 8)
        });
        g.FillEllipse(color, 6.6f * scale, 6.6f * scale, 2.8f * scale, 2.8f * scale);
        if (slashed)
            g.DrawLines(pen, new[] { P(12.25f, 3.75f), P(3.75f, 12.25f) });
    }

    private readonly record struct IconCacheKey(PanelButtonIcon Icon, bool Muted, bool IsDark, int Size);
}
