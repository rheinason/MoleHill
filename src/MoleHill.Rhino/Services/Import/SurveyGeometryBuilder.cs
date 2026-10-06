using MoleHill.Core.Interop;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Turns a Core <see cref="SurveyFigure"/> into a Rhino curve.
///
/// Core records <i>which</i> points a crew flagged as lying on an arc and stops there; fitting the arc
/// needs Rhino's geometry, and a second fitter in Core would eventually give a second answer. So the
/// split is: Core decides what the figure is, this decides what it looks like.
/// </summary>
internal static class SurveyGeometryBuilder
{
    /// <summary>
    /// Builds the curve for a figure, or null when its points cannot make one.
    ///
    /// A flagged point becomes an arc through its neighbours — the three-point arc a crew means when it
    /// codes the middle of a curve. Flags that cannot resolve (first or last point, or three collinear
    /// points) fall back to a straight segment rather than failing: a curve drawn slightly straighter
    /// than intended is a drafting matter, a failed import is a lost survey.
    /// </summary>
    public static Curve? BuildFigure(SurveyFigure figure, IReadOnlyList<Point3d> points)
    {
        ArgumentNullException.ThrowIfNull(figure);
        ArgumentNullException.ThrowIfNull(points);

        List<Point3d> ordered = Vertices(figure, points, out List<bool> arcFlags);
        if (ordered.Count < 2)
            return null;

        if (figure.IsClosed && ordered.Count >= 3 && ordered[0].DistanceTo(ordered[^1]) > RhinoMath.ZeroTolerance)
        {
            ordered.Add(ordered[0]);
            arcFlags.Add(false);
        }

        if (!arcFlags.Contains(true))
            return new PolylineCurve(ordered);

        return BuildWithArcs(ordered, arcFlags);
    }

    private static List<Point3d> Vertices(
        SurveyFigure figure,
        IReadOnlyList<Point3d> points,
        out List<bool> arcFlags)
    {
        var ordered = new List<Point3d>(figure.PointCount);
        arcFlags = new List<bool>(figure.PointCount);

        for (int i = 0; i < figure.PointIndices.Count; i++)
        {
            int index = figure.PointIndices[i];
            if (index < 0 || index >= points.Count)
                continue;

            Point3d point = points[index];

            // Two shots at the same place would make a zero-length segment, which Rhino will not join.
            // Dropping the repeat keeps the figure rather than rejecting it.
            if (ordered.Count > 0 && ordered[^1].DistanceTo(point) <= RhinoMath.ZeroTolerance)
                continue;

            ordered.Add(point);
            arcFlags.Add(i < figure.ArcFlags.Count && figure.ArcFlags[i]);
        }

        return ordered;
    }

    private static Curve BuildWithArcs(IReadOnlyList<Point3d> points, IReadOnlyList<bool> arcFlags)
    {
        var poly = new PolyCurve();
        int i = 0;

        while (i < points.Count - 1)
        {
            // A flag on the *next* point means the span from here to the one after it bends through it.
            bool bends = i + 2 < points.Count && arcFlags[i + 1];
            if (bends)
            {
                var arc = new Arc(points[i], points[i + 1], points[i + 2]);
                if (arc.IsValid && arc.Radius > RhinoMath.ZeroTolerance)
                {
                    poly.Append(new ArcCurve(arc));
                    i += 2;
                    continue;
                }
            }

            poly.Append(new Line(points[i], points[i + 1]));
            i++;
        }

        return poly.SegmentCount > 0 ? poly : new PolylineCurve(points);
    }
}
