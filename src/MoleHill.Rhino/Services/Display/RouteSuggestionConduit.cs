// Draws mhSuggestRoute's alternatives over everything while the user chooses which to keep.
using System.Drawing;
using MoleHill.Core.Analysis;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The review overlay for suggested routes: each alternative in its own categorical colour, labelled with
/// its number part-way along. Drawn as an overlay rather than in the scene because a route lies on the
/// ground by design, and depth-tested it would flicker in and out of the terrain mesh.
/// </summary>
internal sealed class RouteSuggestionConduit : DisplayConduit
{
    private const int RouteWidth = 4;
    private const int EndPointSize = 7;
    private static readonly Color LabelTextColor = Color.White;

    /// <summary>Larger than Rhino's default dot, whose single digit was hard to read on a busy terrain.</summary>
    private const int LabelFontHeight = 18;

    private IReadOnlyList<Polyline> _routes = Array.Empty<Polyline>();
    private IReadOnlyList<Point3d> _picked = Array.Empty<Point3d>();
    private BoundingBox _bounds = BoundingBox.Empty;

    /// <summary>Colour of route <paramref name="index"/> (zero-based), shared with nothing else on screen.</summary>
    public static Color RouteColor(int index)
    {
        (byte r, byte g, byte b) = CategoricalPalette.ColorAt(index);
        return Color.FromArgb(r, g, b);
    }

    public void SetRoutes(IReadOnlyList<Polyline> routes, IReadOnlyList<Point3d> picked)
    {
        _routes = routes;
        _picked = picked;
        _bounds = BoundingBox.Empty;
        foreach (Polyline route in routes)
            _bounds.Union(route.BoundingBox);
        foreach (Point3d point in picked)
            _bounds.Union(point);
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (_bounds.IsValid)
            e.IncludeBoundingBox(_bounds);
    }

    /// <summary>
    /// The lines, in <c>DrawForeground</c> with depth testing and writing off. The terrain preview paints its
    /// surface earlier, in <c>PostDrawObjects</c>, so a route lying on the ground is never hidden in it; and
    /// the lines are kept out of <c>DrawOverlay</c>, where the numbers go, because within one channel Rhino
    /// paints thick lines after dots whatever order they are drawn in — a number drawn after every line was
    /// still covered by them (2026-10-16, live).
    /// </summary>
    protected override void DrawForeground(DrawEventArgs e)
    {
        e.Display.PushDepthTesting(false);
        e.Display.PushDepthWriting(false);
        try
        {
            // Back to front, so route 1 — the best — is drawn last and stays on top where routes share ground.
            for (int index = _routes.Count - 1; index >= 0; index--)
            {
                if (_routes[index].Count >= 2)
                    e.Display.DrawPolyline(_routes[index], RouteColor(index), RouteWidth);
            }
        }
        finally
        {
            e.Display.PopDepthWriting();
            e.Display.PopDepthTesting();
        }
    }

    /// <summary>
    /// The numbers and the picked points, in <c>DrawOverlay</c>: the last channel, which neither the lines nor
    /// the terrain preview paint in, so nothing covers them (docs/architecture.md, the inspector overlay note).
    /// </summary>
    protected override void DrawOverlay(DrawEventArgs e)
    {
        e.Display.PushDepthTesting(false);
        e.Display.PushDepthWriting(false);
        try
        {
            for (int index = _routes.Count - 1; index >= 0; index--)
            {
                Polyline route = _routes[index];
                if (route.Count < 2)
                    continue;

                Color color = RouteColor(index);
                double along = (index + 1.0) / (_routes.Count + 1.0);
                using var dot = new TextDot((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), PointAlong(route, along))
                {
                    FontHeight = LabelFontHeight,
                };
                e.Display.DrawDot(dot, color, LabelTextColor, LabelTextColor);
            }

            foreach (Point3d point in _picked)
                e.Display.DrawPoint(point, PointStyle.RoundSimple, EndPointSize, Color.Black);
        }
        finally
        {
            e.Display.PopDepthWriting();
            e.Display.PopDepthTesting();
        }
    }

    /// <summary>
    /// The point a share of the way along the route. Each route's number sits at a different share
    /// (a quarter, a half, three quarters for three), so routes that run together do not stack their
    /// numbers on one spot.
    /// </summary>
    private static Point3d PointAlong(Polyline route, double share)
    {
        double target = route.Length * share;
        double walked = 0.0;
        for (int i = 1; i < route.Count; i++)
        {
            double segment = route[i - 1].DistanceTo(route[i]);
            if (walked + segment >= target && segment > 0.0)
                return route[i - 1] + ((route[i] - route[i - 1]) * ((target - walked) / segment));
            walked += segment;
        }

        return route[route.Count - 1];
    }
}
