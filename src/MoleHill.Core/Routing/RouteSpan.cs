namespace MoleHill.Core.Routing;

/// <summary>
/// One span of a route's exact geometry. A straight is the 3D line from start to end. An arc is a
/// rational quadratic Bézier span — start, control, end, with the control weighted
/// <see cref="Weight"/> — whose plan projection is an exact circular arc and whose elevation runs from
/// the start's to the end's. A host builds a degree-2 NURBS span from it directly.
/// </summary>
public readonly record struct RouteSpan(
    bool IsArc,
    double StartX,
    double StartY,
    double StartZ,
    double ControlX,
    double ControlY,
    double ControlZ,
    double EndX,
    double EndY,
    double EndZ,
    double Weight)
{
    public static RouteSpan Line(double ax, double ay, double az, double bx, double by, double bz) =>
        new(false, ax, ay, az, 0.5 * (ax + bx), 0.5 * (ay + by), 0.5 * (az + bz), bx, by, bz, 1.0);

    /// <summary>The span's point at Bézier parameter <paramref name="t"/> in [0, 1].</summary>
    public (double X, double Y, double Z) Evaluate(double t)
    {
        double b0 = (1.0 - t) * (1.0 - t);
        double b1 = 2.0 * t * (1.0 - t) * Weight;
        double b2 = t * t;
        double d = b0 + b1 + b2;
        return (
            ((b0 * StartX) + (b1 * ControlX) + (b2 * EndX)) / d,
            ((b0 * StartY) + (b1 * ControlY) + (b2 * EndY)) / d,
            ((b0 * StartZ) + (b1 * ControlZ) + (b2 * EndZ)) / d);
    }
}
