using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// A small chart of how much better than the sketch the route has become, sample by sample, so the user
/// watches it climb as the route relaxes and sees a change of weights or a dragged pin bend the line.
/// </summary>
internal sealed class ScoreSparkline : Drawable
{
    private const int MaximumSamples = 240;
    private readonly List<double> _values = new();

    public ScoreSparkline()
    {
        Size = new Size(240, 36);
        Paint += (_, e) => Draw(e.Graphics);
    }

    /// <summary>Adds a sample (a share: 0.4 is 40% better), dropping the oldest past the chart's width.</summary>
    public void Add(double improvement)
    {
        _values.Add(improvement);
        if (_values.Count > MaximumSamples)
            _values.RemoveAt(0);
        Invalidate();
    }

    public void Clear()
    {
        _values.Clear();
        Invalidate();
    }

    private void Draw(Graphics g)
    {
        g.AntiAlias = true;
        g.FillRectangle(UiTheme.InputBackground, 0, 0, Width, Height);
        if (_values.Count < 2)
            return;

        // Zero (as good as the sketch) is the baseline; the top is the best so far, at least 10%.
        double low = Math.Min(0.0, _values.Min());
        double high = Math.Max(0.1, _values.Max());
        float Y(double value) => (float)(Height - 2 - ((value - low) / (high - low) * (Height - 4)));
        float X(int index) => (float)index / (MaximumSamples - 1) * (Width - 1);

        float zero = Y(0.0);
        g.DrawLine(new Pen(UiTheme.ZoneStripColor, 1f), 0, zero, Width, zero);

        var points = new PointF[_values.Count];
        for (int i = 0; i < _values.Count; i++)
            points[i] = new PointF(X(i), Y(_values[i]));

        var area = new List<PointF> { new(points[0].X, zero) };
        area.AddRange(points);
        area.Add(new PointF(points[^1].X, zero));
        g.FillPolygon(Color.FromArgb(25, 157, 73, 70), area.ToArray());
        g.DrawLines(new Pen(Color.FromArgb(25, 157, 73), 1.5f), points);
    }
}
