// Draws mhSuggestRoute's alternatives over everything while the user chooses which to keep.
using System.Drawing;
using MoleHill.Core.Analysis;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The review overlay for suggested routes: each alternative in its own categorical colour, labelled with
/// its number at mid-length. Drawn as an overlay rather than in the scene because a route lies on the
/// ground by design, and depth-tested it would flicker in and out of the terrain mesh.
/// </summary>
internal sealed class RouteSuggestionConduit : DisplayConduit
{
    private const int RouteWidth = 4;
    private const int EndPointSize = 7;
    private static readonly Color LabelTextColor = Color.White;

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
    /// <c>DrawOverlay</c> with depth testing and writing off, as <see cref="CurveReviewConduit"/> does: a
    /// MoleHill terrain preview paints in its own conduit's channels, and depth flags alone cannot keep
    /// a later painter in the same channel from covering the routes (docs/architecture.md, the
    /// inspector overlay note).
    /// </summary>
    protected override void DrawOverlay(DrawEventArgs e)
    {
        e.Display.PushDepthTesting(false);
        e.Display.PushDepthWriting(false);
        try
        {
            // Back to front, so route 1 — the best — is drawn last and stays on top where routes share ground.
            for (int index = _routes.Count - 1; index >= 0; index--)
            {
                Polyline route = _routes[index];
                if (route.Count < 2)
                    continue;

                Color color = RouteColor(index);
                e.Display.DrawPolyline(route, color, RouteWidth);
                Point3d label = MidLengthPoint(route);
                e.Display.DrawDot(label, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), color, LabelTextColor);
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

    private static Point3d MidLengthPoint(Polyline route)
    {
        double half = route.Length * 0.5;
        double walked = 0.0;
        for (int i = 1; i < route.Count; i++)
        {
            double segment = route[i - 1].DistanceTo(route[i]);
            if (walked + segment >= half && segment > 0.0)
                return route[i - 1] + ((route[i] - route[i - 1]) * ((half - walked) / segment));
            walked += segment;
        }

        return route[route.Count - 1];
    }
}
