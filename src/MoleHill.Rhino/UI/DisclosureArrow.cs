using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The expand/collapse arrow on every card and section header: a filled triangle, right when collapsed,
/// down when expanded. Drawn as a shape rather than the ▶/▼ characters, whose position depended on the
/// font's baseline and sat visibly above the row's centre.
/// </summary>
internal sealed class DisclosureArrow : Drawable
{
    private bool _expanded;

    public DisclosureArrow(bool expanded)
    {
        _expanded = expanded;
        Size = new Size(UiMetrics.DisclosureSize, UiMetrics.ControlHeight);
        Paint += (_, e) => Draw(e.Graphics);
    }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            Invalidate();
        }
    }

    private void Draw(Graphics g)
    {
        g.AntiAlias = true;
        float cx = Width / 2f, cy = Height / 2f, r = UiMetrics.DisclosureGlyph / 2f;
        PointF[] triangle = _expanded
            ? new[] { new PointF(cx - r, cy - r / 2f), new PointF(cx + r, cy - r / 2f), new PointF(cx, cy + r / 2f) }
            : new[] { new PointF(cx - r / 2f, cy - r), new PointF(cx + r / 2f, cy), new PointF(cx - r / 2f, cy + r) };
        g.FillPolygon(UiTheme.PrimaryText, triangle);
    }
}
