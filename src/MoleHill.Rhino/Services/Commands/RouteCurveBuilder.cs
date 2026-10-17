// Turns routes from Core into ordinary Rhino curves, for mhSuggestRoute and mhRelaxRoute.
using MoleHill.Core.Routing;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Builds the curves the route commands add. The default is the fitted curve: one cubic NURBS with only the
/// control points the route needs (<see cref="FittedRouteCurve"/>), kinked where its pieces meet at waypoints
/// and sharp corners, so it edits like a hand-drawn curve. The exact form, straights and arcs at the minimum
/// radius with a vertex at every grade break, is kept for a designer who needs the radii to the millimetre.
/// </summary>
internal static class RouteCurveBuilder
{
    /// <summary>The fitted curve as one NURBS: its pieces joined end to end.</summary>
    public static Curve FromFitted(FittedRouteCurve fitted)
    {
        if (fitted.Pieces.Count == 1)
            return ToNurbs(fitted.Pieces[0]);

        var joined = new PolyCurve();
        foreach (RouteSplineCurve piece in fitted.Pieces)
            joined.Append(ToNurbs(piece));

        NurbsCurve? nurbs = joined.ToNurbsCurve();
        if (nurbs == null)
            return joined;

        joined.Dispose();
        return nurbs;
    }

    /// <summary>
    /// The exact route: a polyline when its corners are sharp, otherwise one degree-2 NURBS of its straights
    /// and exact plan arcs joined end to end, tangent at every rounded corner.
    /// </summary>
    public static Curve FromSpans(RouteAlternative route)
    {
        if (!route.HasArcs)
            return new PolylineCurve(ToPolyline(route.PointsXyz));

        var joined = new PolyCurve();
        foreach (RouteSpan span in route.Spans)
        {
            var start = new Point3d(span.StartX, span.StartY, span.StartZ);
            var end = new Point3d(span.EndX, span.EndY, span.EndZ);
            if (!span.IsArc)
            {
                joined.Append(new LineCurve(start, end));
                continue;
            }

            var arc = new NurbsCurve(3, true, 3, 3);
            arc.Points.SetPoint(0, start, 1.0);
            arc.Points.SetPoint(1, new Point3d(span.ControlX, span.ControlY, span.ControlZ), span.Weight);
            arc.Points.SetPoint(2, end, 1.0);
            arc.Knots[0] = 0.0;
            arc.Knots[1] = 0.0;
            arc.Knots[2] = 1.0;
            arc.Knots[3] = 1.0;
            joined.Append(arc);
        }

        NurbsCurve? nurbs = joined.ToNurbsCurve();
        if (nurbs == null)
            return joined;

        joined.Dispose();
        return nurbs;
    }

    public static Polyline ToPolyline(double[] pointsXyz)
    {
        var polyline = new Polyline(pointsXyz.Length / 3);
        for (int i = 0; i < pointsXyz.Length / 3; i++)
            polyline.Add(pointsXyz[i * 3], pointsXyz[(i * 3) + 1], pointsXyz[(i * 3) + 2]);
        return polyline;
    }

    /// <summary>
    /// A Core spline as a Rhino NURBS. Core keeps the full clamped knot vector; Rhino's drops the outermost
    /// knot at each end.
    /// </summary>
    private static NurbsCurve ToNurbs(RouteSplineCurve spline)
    {
        var nurbs = new NurbsCurve(3, false, spline.Degree + 1, spline.ControlPointCount);
        for (int i = 0; i < spline.ControlPointCount; i++)
        {
            nurbs.Points.SetPoint(i, new Point3d(
                spline.ControlPoints[i * 3], spline.ControlPoints[(i * 3) + 1], spline.ControlPoints[(i * 3) + 2]));
        }

        for (int k = 0; k < nurbs.Knots.Count; k++)
            nurbs.Knots[k] = spline.Knots[k + 1];
        return nurbs;
    }
}
