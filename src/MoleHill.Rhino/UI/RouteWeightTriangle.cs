using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The route's compromise as one gesture: a triangle whose corners are Short (distance), Gentle (steepness)
/// and Little earthwork, with a dot the user drags between them. The dot's barycentric position is the
/// three weights, scaled to sum to three so the middle is the commands' default of 1, 1, 1. Three sliders
/// said the same thing, but did not show that the weights are relative: raising one is lowering the others.
/// </summary>
internal sealed class RouteWeightTriangle : Drawable
{
    private const float Margin = 18f;
    private const float DotRadius = 6f;

    private double _distance = 1.0, _steepness = 1.0, _earthworks = 1.0;
    private bool _dragging;

    public RouteWeightTriangle()
    {
        Size = new Size(240, 150);
        ToolTip = "Drag the dot towards what matters most: a short route, a gentle one, or one that moves little earth.";
        Paint += (_, e) => Draw(e.Graphics);
        MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;
            _dragging = true;
            MoveTo(e.Location, commit: false);
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (!_dragging)
                return;
            MoveTo(e.Location, commit: false);
            e.Handled = true;
        };
        MouseUp += (_, e) =>
        {
            if (!_dragging)
                return;
            _dragging = false;
            MoveTo(e.Location, commit: true);
            e.Handled = true;
        };
    }

    /// <summary>Raised as the dot moves, with the weights summing to three; <c>Commit</c> is true when it is let go.</summary>
    public event Action<double, double, double, bool>? WeightsChanged;

    public void SetWeights(double distance, double steepness, double earthworks)
    {
        if (_dragging)
            return;
        _distance = Math.Max(0.0, distance);
        _steepness = Math.Max(0.0, steepness);
        _earthworks = Math.Max(0.0, earthworks);
        Invalidate();
    }

    // Corners: Short bottom left, Little earthwork bottom right, Gentle at the top.
    private PointF ShortCorner => new(Margin, Height - Margin);

    private PointF EarthCorner => new(Width - Margin, Height - Margin);

    private PointF GentleCorner => new(Width / 2f, Margin);

    private void Draw(Graphics g)
    {
        g.AntiAlias = true;
        var shape = new[] { ShortCorner, EarthCorner, GentleCorner };
        g.FillPolygon(UiTheme.PillBackground, shape);
        g.DrawPolygon(new Pen(UiTheme.MutedText, 1f), shape);

        // The default, all three weighted alike.
        PointF middle = Blend(1.0, 1.0, 1.0);
        g.DrawLine(new Pen(UiTheme.MutedText, 1f), middle.X - 4, middle.Y, middle.X + 4, middle.Y);
        g.DrawLine(new Pen(UiTheme.MutedText, 1f), middle.X, middle.Y - 4, middle.X, middle.Y + 4);

        var font = SystemFonts.Default(8f);
        Label(g, font, "Short", ShortCorner, -0.1f, 4f);
        Label(g, font, "Little earthwork", EarthCorner, -0.9f, 4f);
        Label(g, font, "Gentle", GentleCorner, -0.5f, -14f);

        PointF dot = Blend(_distance, _steepness, _earthworks);
        g.FillEllipse(Color.FromArgb(25, 157, 73), dot.X - DotRadius, dot.Y - DotRadius, 2 * DotRadius, 2 * DotRadius);
        g.DrawEllipse(new Pen(Colors.White, 1.5f), dot.X - DotRadius, dot.Y - DotRadius, 2 * DotRadius, 2 * DotRadius);
    }

    private static void Label(Graphics g, Font font, string text, PointF corner, float alignX, float offsetY)
    {
        SizeF size = g.MeasureString(font, text);
        g.DrawText(font, UiTheme.PrimaryText, corner.X + (alignX * size.Width), corner.Y + offsetY, text);
    }

    private PointF Blend(double distance, double steepness, double earthworks)
    {
        double sum = distance + steepness + earthworks;
        if (!(sum > 0.0))
            (distance, steepness, earthworks, sum) = (1.0, 1.0, 1.0, 3.0);
        PointF a = ShortCorner, b = GentleCorner, c = EarthCorner;
        return new PointF(
            (float)(((a.X * distance) + (b.X * steepness) + (c.X * earthworks)) / sum),
            (float)(((a.Y * distance) + (b.Y * steepness) + (c.Y * earthworks)) / sum));
    }

    /// <summary>Barycentric weights of a point, clamped into the triangle.</summary>
    private void MoveTo(PointF point, bool commit)
    {
        PointF a = ShortCorner, b = GentleCorner, c = EarthCorner;
        double det = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        double wa = (((b.Y - c.Y) * (point.X - c.X)) + ((c.X - b.X) * (point.Y - c.Y))) / det;
        double wb = (((c.Y - a.Y) * (point.X - c.X)) + ((a.X - c.X) * (point.Y - c.Y))) / det;
        double wc = 1.0 - wa - wb;
        wa = Math.Max(0.0, wa);
        wb = Math.Max(0.0, wb);
        wc = Math.Max(0.0, wc);
        double sum = wa + wb + wc;
        _distance = 3.0 * wa / sum;
        _steepness = 3.0 * wb / sum;
        _earthworks = 3.0 * wc / sum;
        Invalidate();
        WeightsChanged?.Invoke(_distance, _steepness, _earthworks, commit);
    }
}
