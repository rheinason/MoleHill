using System;
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
/// dissolves into noise (this bit hardest for <see cref="Duplicate"/>, <see cref="ZoneMeshes"/> and the
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

        // Fills one closed subpath given in 0..16 icon-grid units — used to port hand-drawn SVG
        // reference icons (see icons/README.md) exactly rather than approximating them with primitives.
        void FillSubpath(float startX, float startY, params PathOp[] ops) =>
            FillCompound(color, FillMode.Winding, (startX, startY, ops));

        // As FillSubpath, but combines several subpaths into one GraphicsPath before filling. Needed
        // whenever a ported source path relies on a second subpath nested inside the first to punch a
        // hole (e.g. a trash-can handle drawn as an outer loop plus an inner cutout rectangle) — filling
        // each subpath separately would paint over the hole instead of leaving it open.
        void FillCompound(Color fillColor, FillMode fillMode, params (float StartX, float StartY, PathOp[] Ops)[] subpaths)
        {
            using var path = new GraphicsPath { FillMode = fillMode };
            foreach (var (startX, startY, ops) in subpaths)
            {
                var cur = P(startX, startY);
                path.MoveTo(cur.X, cur.Y);
                foreach (var op in ops)
                {
                    if (op.IsCurve)
                    {
                        var end = P(op.Ex, op.Ey);
                        path.AddBezier(cur, P(op.C1x, op.C1y), P(op.C2x, op.C2y), end);
                        cur = end;
                    }
                    else
                    {
                        cur = P(op.Ex, op.Ey);
                        path.LineTo(cur.X, cur.Y);
                    }
                }
                path.CloseFigure();
            }
            g.FillPath(fillColor, path);
        }

        switch (icon)
        {
            case PanelButtonIcon.Add:
                // Ported from icons/+.svg — a softly rounded filled plus, the same "one continuous
                // rounded outline" language as Clear's X below instead of a plain squared-off stroke.
                FillSubpath(8.009f, 2.034f,
                    PathOp.Curve(7.832f, 2.032f, 7.661f, 2.102f, 7.536f, 2.227f),
                    PathOp.Curve(7.41f, 2.352f, 7.341f, 2.523f, 7.343f, 2.7f),
                    PathOp.Line(7.343f, 7.311f),
                    PathOp.Line(2.732f, 7.311f),
                    PathOp.Curve(2.488f, 7.295f, 2.254f, 7.417f, 2.127f, 7.627f),
                    PathOp.Curve(2f, 7.836f, 2f, 8.099f, 2.127f, 8.309f),
                    PathOp.Curve(2.254f, 8.519f, 2.488f, 8.641f, 2.732f, 8.625f),
                    PathOp.Line(7.343f, 8.625f),
                    PathOp.Line(7.343f, 13.236f),
                    PathOp.Curve(7.327f, 13.48f, 7.449f, 13.714f, 7.659f, 13.841f),
                    PathOp.Curve(7.868f, 13.968f, 8.132f, 13.968f, 8.341f, 13.841f),
                    PathOp.Curve(8.551f, 13.714f, 8.673f, 13.48f, 8.657f, 13.236f),
                    PathOp.Line(8.657f, 8.625f),
                    PathOp.Line(13.268f, 8.625f),
                    PathOp.Curve(13.512f, 8.641f, 13.746f, 8.519f, 13.873f, 8.309f),
                    PathOp.Curve(14f, 8.099f, 14f, 7.836f, 13.873f, 7.627f),
                    PathOp.Curve(13.746f, 7.417f, 13.512f, 7.295f, 13.268f, 7.311f),
                    PathOp.Line(8.657f, 7.311f),
                    PathOp.Line(8.657f, 2.7f),
                    PathOp.Curve(8.661f, 2.338f, 8.372f, 2.04f, 8.009f, 2.034f));
                break;

            case PanelButtonIcon.Duplicate:
                // Ported from icons/Duplicate.svg — two rounded-corner square outlines (each already a
                // closed hollow band, not a filled block), a faded back copy behind a full-opacity front
                // one, matching how a hairline-outline "stacked squares" glyph reads at 14px.
                FillCompound(Faded(0.55f), FillMode.Winding, (1.5f, 15f, new[]
                {
                    PathOp.Curve(1.224f, 15f, 1f, 14.776f, 1f, 14.5f),
                    PathOp.Line(1f, 4.5f),
                    PathOp.Curve(1f, 4.224f, 1.224f, 4f, 1.5f, 4f),
                    PathOp.Line(4f, 4f),
                    PathOp.Line(4f, 5f),
                    PathOp.Line(2f, 5f),
                    PathOp.Line(2f, 14f),
                    PathOp.Line(11f, 14f),
                    PathOp.Line(11f, 12f),
                    PathOp.Line(12f, 12f),
                    PathOp.Line(12f, 14.5f),
                    PathOp.Curve(12f, 14.776f, 11.776f, 15f, 11.5f, 15f)
                }));
                FillSubpath(5.5f, 1f,
                    PathOp.Curve(5.224f, 1f, 5f, 1.224f, 5f, 1.5f),
                    PathOp.Line(5f, 10.5f),
                    PathOp.Curve(5f, 10.776f, 5.224f, 11f, 5.5f, 11f),
                    PathOp.Line(14.5f, 11f),
                    PathOp.Curve(14.776f, 11f, 15f, 10.776f, 15f, 10.5f),
                    PathOp.Line(15f, 6f),
                    PathOp.Line(14f, 6f),
                    PathOp.Line(14f, 10f),
                    PathOp.Line(6f, 10f),
                    PathOp.Line(6f, 2f),
                    PathOp.Line(11f, 2f),
                    PathOp.Line(11f, 4.5f),
                    PathOp.Curve(11f, 4.776f, 11.224f, 5f, 11.5f, 5f),
                    PathOp.Line(14f, 5f),
                    PathOp.Line(15f, 5f),
                    PathOp.Line(15f, 4.5f),
                    PathOp.Curve(15f, 4.367f, 14.947f, 4.24f, 14.854f, 4.146f),
                    PathOp.Line(11.854f, 1.146f),
                    PathOp.Curve(11.76f, 1.053f, 11.633f, 1f, 11.5f, 1f));
                break;

            case PanelButtonIcon.Delete:
                // Ported from icons/Trash.svg — outline body, plus a lid whose handle is a genuine hole
                // (an inner rectangle combined via FillMode.Alternate), plus two faded ribs inside.
                FillCompound(color, FillMode.Alternate,
                    (2.769f, 5.385f, new[]
                    {
                        PathOp.Line(2.769f, 12.969f),
                        PathOp.Curve(2.769f, 13.478f, 2.948f, 13.953f, 3.282f, 14.287f),
                        PathOp.Curve(3.617f, 14.622f, 4.091f, 14.8f, 4.6f, 14.8f),
                        PathOp.Line(11.4f, 14.8f),
                        PathOp.Curve(11.909f, 14.8f, 12.383f, 14.622f, 12.718f, 14.287f),
                        PathOp.Curve(13.052f, 13.953f, 13.231f, 13.478f, 13.231f, 12.969f),
                        PathOp.Line(13.231f, 5.385f),
                        PathOp.Line(12.185f, 5.385f),
                        PathOp.Line(12.185f, 12.969f),
                        PathOp.Curve(12.185f, 13.245f, 12.101f, 13.424f, 11.978f, 13.547f),
                        PathOp.Curve(11.855f, 13.671f, 11.676f, 13.754f, 11.4f, 13.754f),
                        PathOp.Line(4.6f, 13.754f),
                        PathOp.Curve(4.324f, 13.754f, 4.145f, 13.67f, 4.022f, 13.547f),
                        PathOp.Curve(3.899f, 13.424f, 3.815f, 13.245f, 3.815f, 12.969f),
                        PathOp.Line(3.815f, 5.385f)
                    }),
                    (6.431f, 1.2f, new[]
                    {
                        PathOp.Curve(6.142f, 1.2f, 5.908f, 1.434f, 5.908f, 1.723f),
                        PathOp.Line(5.908f, 3.292f),
                        PathOp.Line(2.246f, 3.292f),
                        PathOp.Curve(1.539f, 3.282f, 1.539f, 4.349f, 2.246f, 4.338f),
                        PathOp.Line(13.754f, 4.338f),
                        PathOp.Curve(14.461f, 4.349f, 14.461f, 3.282f, 13.754f, 3.292f),
                        PathOp.Line(10.092f, 3.292f),
                        PathOp.Line(10.092f, 1.723f),
                        PathOp.Curve(10.092f, 1.434f, 9.858f, 1.2f, 9.569f, 1.2f)
                    }),
                    (6.954f, 2.246f, new[]
                    {
                        PathOp.Line(9.046f, 2.246f),
                        PathOp.Line(9.046f, 3.292f),
                        PathOp.Line(6.954f, 3.292f)
                    }));
                FillCompound(Faded(0.6f), FillMode.Winding,
                    (6.423f, 6.423f, new[]
                    {
                        PathOp.Curve(6.134f, 6.423f, 5.903f, 6.665f, 5.908f, 6.954f),
                        PathOp.Line(5.908f, 11.138f),
                        PathOp.Curve(5.897f, 11.846f, 6.964f, 11.846f, 6.954f, 11.138f),
                        PathOp.Line(6.954f, 6.954f),
                        PathOp.Curve(6.958f, 6.659f, 6.718f, 6.418f, 6.423f, 6.423f)
                    }),
                    (9.561f, 6.423f, new[]
                    {
                        PathOp.Curve(9.272f, 6.423f, 9.042f, 6.665f, 9.046f, 6.954f),
                        PathOp.Line(9.046f, 11.138f),
                        PathOp.Curve(9.036f, 11.846f, 10.102f, 11.846f, 10.092f, 11.138f),
                        PathOp.Line(10.092f, 6.954f),
                        PathOp.Curve(10.096f, 6.659f, 9.856f, 6.418f, 9.561f, 6.423f)
                    }));
                break;

            case PanelButtonIcon.Clear:
                // Ported from icons/x.svg — the same rounded-outline language as Add's plus.
                FillSubpath(13.716f, 2.293f,
                    PathOp.Curve(14f, 2.586f, 13.993f, 3.054f, 13.7f, 3.339f),
                    PathOp.Line(9.039f, 8f),
                    PathOp.Line(13.7f, 12.661f),
                    PathOp.Curve(13.902f, 12.845f, 13.986f, 13.124f, 13.92f, 13.389f),
                    PathOp.Curve(13.853f, 13.653f, 13.647f, 13.86f, 13.382f, 13.927f),
                    PathOp.Curve(13.118f, 13.993f, 12.838f, 13.909f, 12.654f, 13.707f),
                    PathOp.Line(7.993f, 9.046f),
                    PathOp.Line(3.332f, 13.707f),
                    PathOp.Curve(3.148f, 13.909f, 2.869f, 13.993f, 2.604f, 13.927f),
                    PathOp.Curve(2.34f, 13.86f, 2.133f, 13.653f, 2.067f, 13.389f),
                    PathOp.Curve(2f, 13.124f, 2.084f, 12.845f, 2.286f, 12.661f),
                    PathOp.Line(6.947f, 8f),
                    PathOp.Line(2.286f, 3.339f),
                    PathOp.Curve(2.084f, 3.155f, 2f, 2.876f, 2.067f, 2.611f),
                    PathOp.Curve(2.133f, 2.347f, 2.34f, 2.14f, 2.604f, 2.073f),
                    PathOp.Curve(2.869f, 2.007f, 3.148f, 2.091f, 3.332f, 2.293f),
                    PathOp.Line(7.993f, 6.954f),
                    PathOp.Line(12.654f, 2.293f),
                    PathOp.Curve(12.793f, 2.149f, 12.985f, 2.069f, 13.185f, 2.069f),
                    PathOp.Curve(13.385f, 2.069f, 13.576f, 2.149f, 13.716f, 2.293f));
                break;

            case PanelButtonIcon.HideOff:
                // Ported from icons/Hide_Off.svg — an open eye with its iris and pupil cut into the
                // filled silhouette. The source is 18x13, so this keeps its aspect ratio while centering
                // it in the panel's 16x16 icon grid.
                FillCompound(color, FillMode.Alternate,
                    (8f, 3.271f, new[]
                    {
                        PathOp.Curve(4.503f, 3.270f, 2.628f, 5.404f, 1.213f, 7.642f),
                        PathOp.Curve(1.024f, 7.843f, 1.024f, 8.153f, 1.213f, 8.354f),
                        PathOp.Curve(2.629f, 9.868f, 4.504f, 12.723f, 8f, 12.725f),
                        PathOp.Curve(11.496f, 12.727f, 13.371f, 9.866f, 14.787f, 8.354f),
                        PathOp.Curve(14.976f, 7.843f, 14.976f, 8.153f, 14.787f, 7.642f),
                        PathOp.Curve(13.371f, 5.406f, 11.394f, 3.271f, 8f, 3.271f)
                    }),
                    (8f, 4.323f, new[]
                    {
                        PathOp.Curve(10.062f, 4.323f, 11.733f, 5.969f, 11.733f, 8f),
                        PathOp.Curve(11.733f, 10.031f, 10.062f, 11.677f, 8f, 11.677f),
                        PathOp.Curve(5.938f, 11.677f, 4.267f, 10.031f, 4.267f, 8f),
                        PathOp.Curve(4.267f, 5.969f, 5.938f, 4.323f, 8f, 4.323f)
                    }),
                    (8f, 6.424f, new[]
                    {
                        PathOp.Curve(8.884f, 6.424f, 9.6f, 7.293f, 9.6f, 8f),
                        PathOp.Curve(9.6f, 8.707f, 8.884f, 9.576f, 8f, 9.576f),
                        PathOp.Curve(7.116f, 9.576f, 6.4f, 8.707f, 6.4f, 8f),
                        PathOp.Curve(6.4f, 7.293f, 7.116f, 6.424f, 8f, 6.424f)
                    }));
                break;

            case PanelButtonIcon.HideOn:
                // Ported from icons/Hide_On.svg — the pupil-less eye silhouette used when the terrain
                // is hidden. The source intentionally uses 60% opacity; retain that state cue here while
                // still deriving the hue from the active Rhino theme.
                FillCompound(new Color(color, 0.6f), FillMode.Winding,
                    (1.587f, 7.491f, new[]
                    {
                        PathOp.Curve(1.406f, 7.495f, 1.233f, 7.984f, 1.233f, 8.378f),
                        PathOp.Curve(2.645f, 9.889f, 4.521f, 12.745f, 8.003f, 12.745f),
                        PathOp.Curve(11.483f, 12.745f, 13.361f, 9.888f, 14.773f, 8.378f),
                        PathOp.Curve(14.773f, 8.378f, 14.773f, 7.984f, 13.988f, 7.666f),
                        PathOp.Curve(12.457f, 6.028f, 10.888f, 3.637f, 8.003f, 3.637f),
                        PathOp.Curve(5.117f, 3.637f, 3.549f, 6.028f, 1.630f, 7.666f),
                        PathOp.Curve(1.630f, 7.666f, 1.587f, 7.491f, 1.587f, 7.491f)
                    }));
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
                // Ported from icons/Rebuild.svg — a single clean circular double-arrow "reload" glyph,
                // left otherwise unadorned so ResetBuild's added X is what tells the two apart.
                FillSubpath(13.428f, 2f,
                    PathOp.Curve(13.423f, 1.922f, 13.422f, 3.003f, 13.426f, 4.722f),
                    PathOp.Curve(12.269f, 2.356f, 8.989f, 1f, 6.238f, 1.755f),
                    PathOp.Curve(3.486f, 2.51f, 1.802f, 4.838f, 1.577f, 7.472f),
                    PathOp.Line(2.565f, 7.471f),
                    PathOp.Curve(2.834f, 4.9f, 4.182f, 3.345f, 6.498f, 2.709f),
                    PathOp.Curve(8.815f, 2.073f, 11.635f, 3.208f, 12.578f, 5.496f),
                    PathOp.Curve(11.787f, 5.505f, 10.59f, 5.486f, 9.916f, 5.497f),
                    PathOp.Curve(9.315f, 5.489f, 9.352f, 6.505f, 9.932f, 6.479f),
                    PathOp.Curve(10.421f, 6.477f, 13.49f, 6.479f, 14.412f, 6.485f),
                    PathOp.Curve(14.411f, 5.028f, 14.413f, 3.696f, 14.42f, 2.007f),
                    PathOp.Curve(14.409f, 1.389f, 13.433f, 1.394f, 13.428f, 1.998f));
                FillSubpath(2.572f, 13.935f,
                    PathOp.Curve(2.577f, 14.013f, 2.57f, 12.893f, 2.566f, 11.175f),
                    PathOp.Curve(3.723f, 13.54f, 7.041f, 15f, 9.762f, 14.141f),
                    PathOp.Curve(12.581f, 13.252f, 14.198f, 11.088f, 14.423f, 8.454f),
                    PathOp.Line(13.435f, 8.454f),
                    PathOp.Curve(13.087f, 10.924f, 11.886f, 12.417f, 9.502f, 13.187f),
                    PathOp.Curve(7.216f, 13.926f, 4.365f, 12.719f, 3.422f, 10.431f),
                    PathOp.Curve(4.213f, 10.422f, 5.41f, 10.442f, 6.084f, 10.43f),
                    PathOp.Curve(6.685f, 10.438f, 6.648f, 9.422f, 6.068f, 9.449f),
                    PathOp.Curve(5.579f, 9.451f, 2.51f, 9.448f, 1.588f, 9.442f),
                    PathOp.Curve(1.589f, 10.899f, 1.587f, 12.239f, 1.58f, 13.928f),
                    PathOp.Curve(1.591f, 14.546f, 2.567f, 14.541f, 2.572f, 13.937f));
                break;

            case PanelButtonIcon.ResetBuild:
                // Ported from icons/Reset Build.svg — a back-pointing arrow feeding into a loop, reading
                // as "cancel/discard the build" rather than Rebuild's plain reload symbol.
                FillSubpath(5.342f, 1.013f,
                    PathOp.Curve(5.202f, 1.017f, 5.07f, 1.075f, 4.972f, 1.175f),
                    PathOp.Line(1.743f, 4.404f),
                    PathOp.Curve(1.534f, 4.614f, 1.534f, 4.954f, 1.743f, 5.164f),
                    PathOp.Line(4.972f, 8.369f),
                    PathOp.Curve(5.479f, 8.896f, 6.259f, 8.115f, 5.732f, 7.609f),
                    PathOp.Line(3.42f, 5.321f),
                    PathOp.Line(9.095f, 5.321f),
                    PathOp.Curve(11.474f, 5.321f, 13.392f, 7.238f, 13.392f, 9.618f),
                    PathOp.Curve(13.392f, 11.998f, 11.474f, 13.915f, 9.095f, 13.915f),
                    PathOp.Line(7.483f, 13.915f),
                    PathOp.Curve(6.757f, 13.905f, 6.757f, 15f, 7.483f, 14.99f),
                    PathOp.Line(9.095f, 14.99f),
                    PathOp.Curve(12.055f, 14.99f, 14.466f, 12.578f, 14.466f, 9.618f),
                    PathOp.Curve(14.466f, 6.658f, 12.055f, 4.247f, 9.095f, 4.247f),
                    PathOp.Line(3.42f, 4.247f),
                    PathOp.Line(5.732f, 1.935f),
                    PathOp.Curve(6.081f, 1.593f, 5.83f, 1f, 5.342f, 1.013f));
                break;

            case PanelButtonIcon.Bake:
                // Ported from icons/BaleTerrain.svg — a flame over a canvas/frame with an export arrow,
                // reading as "render and send this to the document" rather than a generic down-arrow.
                FillSubpath(3.925f, 8.646f,
                    PathOp.Curve(4.761f, 8.647f, 5.596f, 8.644f, 6.431f, 8.646f),
                    PathOp.Curve(7.266f, 8.649f, 8.101f, 8.657f, 8.937f, 8.63f),
                    PathOp.Curve(9f, 8.628f, 9.064f, 8.626f, 9.119f, 8.602f),
                    PathOp.Curve(9.175f, 8.578f, 9.223f, 8.532f, 9.261f, 8.479f),
                    PathOp.Curve(9.299f, 8.426f, 9.326f, 8.365f, 9.331f, 8.304f),
                    PathOp.Curve(9.335f, 8.244f, 9.317f, 8.183f, 9.298f, 8.123f),
                    PathOp.Curve(9.085f, 7.429f, 8.834f, 6.75f, 8.597f, 6.065f),
                    PathOp.Curve(8.36f, 5.381f, 8.137f, 4.691f, 7.858f, 4.021f),
                    PathOp.Curve(7.826f, 3.944f, 7.794f, 3.868f, 7.734f, 3.818f),
                    PathOp.Curve(7.674f, 3.768f, 7.587f, 3.745f, 7.5f, 3.746f),
                    PathOp.Curve(7.414f, 3.747f, 7.329f, 3.773f, 7.269f, 3.824f),
                    PathOp.Curve(7.209f, 3.875f, 7.175f, 3.951f, 7.142f, 4.028f),
                    PathOp.Curve(7.014f, 4.332f, 6.921f, 4.647f, 6.827f, 4.963f),
                    PathOp.Curve(6.734f, 5.278f, 6.639f, 5.594f, 6.513f, 5.897f),
                    PathOp.Curve(6.482f, 5.973f, 6.448f, 6.048f, 6.387f, 6.101f),
                    PathOp.Curve(6.325f, 6.154f, 6.235f, 6.185f, 6.155f, 6.184f),
                    PathOp.Curve(5.995f, 6.182f, 5.875f, 6.053f, 5.769f, 5.919f),
                    PathOp.Curve(5.675f, 5.801f, 5.592f, 5.68f, 5.453f, 5.658f),
                    PathOp.Curve(5.384f, 5.646f, 5.301f, 5.66f, 5.237f, 5.695f),
                    PathOp.Curve(5.173f, 5.73f, 5.128f, 5.787f, 5.084f, 5.844f),
                    PathOp.Curve(4.817f, 6.192f, 4.581f, 6.565f, 4.334f, 6.93f),
                    PathOp.Curve(4.088f, 7.296f, 3.831f, 7.653f, 3.609f, 8.035f),
                    PathOp.Curve(3.573f, 8.097f, 3.537f, 8.16f, 3.528f, 8.228f),
                    PathOp.Curve(3.519f, 8.296f, 3.536f, 8.369f, 3.571f, 8.433f),
                    PathOp.Curve(3.642f, 8.561f, 3.784f, 8.65f, 3.925f, 8.646f));
                FillSubpath(1.5f, 1f,
                    PathOp.Curve(1.254f, 0.975f, 0.975f, 1.254f, 1f, 1.5f),
                    PathOp.Curve(1f, 4.833f, 1f, 8.167f, 1f, 11.5f),
                    PathOp.Curve(0.975f, 11.746f, 1.254f, 12.025f, 1.5f, 12f),
                    PathOp.Curve(3.499f, 12f, 5.499f, 12f, 7.498f, 12f),
                    PathOp.Curve(7.5f, 12f, 7.503f, 12f, 7.505f, 12f),
                    PathOp.Curve(7.751f, 12.024f, 8.03f, 11.746f, 8.005f, 11.5f),
                    PathOp.Curve(8.03f, 11.254f, 7.751f, 10.976f, 7.505f, 11f),
                    PathOp.Curve(7.503f, 11f, 7.5f, 11f, 7.498f, 11f),
                    PathOp.Curve(5.655f, 11f, 3.822f, 11f, 2f, 11f),
                    PathOp.Curve(2f, 8f, 2f, 5f, 2f, 2f),
                    PathOp.Curve(5f, 2f, 8f, 2f, 11f, 2f),
                    PathOp.Curve(11f, 3.833f, 11f, 5.667f, 11f, 7.5f),
                    PathOp.Curve(11f, 7.502f, 11f, 7.505f, 11f, 7.507f),
                    PathOp.Curve(10.982f, 7.765f, 11.241f, 8.007f, 11.5f, 8.007f),
                    PathOp.Curve(11.759f, 8.007f, 12.018f, 7.766f, 12f, 7.507f),
                    PathOp.Curve(12f, 7.505f, 12f, 7.502f, 12f, 7.5f),
                    PathOp.Curve(12f, 5.488f, 12f, 3.488f, 12f, 1.5f),
                    PathOp.Curve(12.025f, 1.254f, 11.746f, 0.975f, 11.5f, 1f),
                    PathOp.Curve(8.167f, 1f, 4.833f, 1f, 1.5f, 1f));
                FillSubpath(14.506f, 15.01f,
                    PathOp.Curve(14.953f, 15.008f, 15.173f, 14.465f, 14.854f, 14.152f),
                    PathOp.Curve(13.471f, 12.77f, 12.089f, 11.388f, 10.707f, 10.006f),
                    PathOp.Curve(11.641f, 10.012f, 12.576f, 10.019f, 13.51f, 10.025f),
                    PathOp.Curve(14.186f, 10.035f, 14.186f, 9.016f, 13.51f, 9.025f),
                    PathOp.Curve(12.173f, 9.019f, 10.837f, 9.012f, 9.5f, 9.006f),
                    PathOp.Curve(9.224f, 9.006f, 9f, 9.23f, 9f, 9.506f),
                    PathOp.Curve(9.003f, 10.846f, 9.007f, 12.186f, 9.01f, 13.525f),
                    PathOp.Curve(9f, 14.202f, 10.019f, 14.202f, 10.01f, 13.525f),
                    PathOp.Curve(10.007f, 12.588f, 10.003f, 11.65f, 10f, 10.713f),
                    PathOp.Curve(11.382f, 12.095f, 12.764f, 13.477f, 14.146f, 14.859f),
                    PathOp.Curve(14.241f, 14.956f, 14.37f, 15.01f, 14.506f, 15.01f));
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

    // One segment of a filled subpath in 0..16 icon-grid units: a cubic bezier to (Ex,Ey) via two
    // control points, or (when IsCurve is false) a straight line to (Ex,Ey).
    private readonly record struct PathOp(bool IsCurve, float C1x, float C1y, float C2x, float C2y, float Ex, float Ey)
    {
        public static PathOp Curve(float c1x, float c1y, float c2x, float c2y, float ex, float ey) =>
            new(true, c1x, c1y, c2x, c2y, ex, ey);

        public static PathOp Line(float ex, float ey) => new(false, 0, 0, 0, 0, ex, ey);
    }

    private readonly record struct IconCacheKey(PanelButtonIcon Icon, bool Muted, bool IsDark, int Size);
}
