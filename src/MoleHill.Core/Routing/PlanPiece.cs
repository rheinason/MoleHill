namespace MoleHill.Core.Routing;

/// <summary>
/// One piece of a plan alignment: a straight, or a circular arc about (<see cref="CenterX"/>,
/// <see cref="CenterY"/>) from <see cref="StartAngle"/> through the signed <see cref="Sweep"/>
/// (counter-clockwise positive). Arcs meet their neighbours tangentially.
/// </summary>
internal readonly record struct PlanPiece(
    bool IsArc,
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    double CenterX,
    double CenterY,
    double Radius,
    double StartAngle,
    double Sweep)
{
    public static PlanPiece Line(double ax, double ay, double bx, double by) =>
        new(false, ax, ay, bx, by, 0.0, 0.0, 0.0, 0.0, 0.0);

    public double Length => IsArc
        ? Radius * Math.Abs(Sweep)
        : Math.Sqrt(((EndX - StartX) * (EndX - StartX)) + ((EndY - StartY) * (EndY - StartY)));

    /// <summary>The plan point at a fraction of the piece's length.</summary>
    public (double X, double Y) PointAt(double fraction)
    {
        if (!IsArc)
            return (StartX + ((EndX - StartX) * fraction), StartY + ((EndY - StartY) * fraction));

        double angle = StartAngle + (Sweep * fraction);
        return (CenterX + (Radius * Math.Cos(angle)), CenterY + (Radius * Math.Sin(angle)));
    }

    /// <summary>Unit tangent at a fraction of the piece, in the direction of travel.</summary>
    public (double X, double Y) TangentAt(double fraction)
    {
        if (!IsArc)
        {
            double length = Length;
            return length > 0.0 ? ((EndX - StartX) / length, (EndY - StartY) / length) : (0.0, 0.0);
        }

        double angle = StartAngle + (Sweep * fraction);
        double sign = Math.Sign(Sweep);
        return (-Math.Sin(angle) * sign, Math.Cos(angle) * sign);
    }
}
