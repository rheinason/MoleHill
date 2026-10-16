namespace MoleHill.Core.Displacement;

/// <summary>
/// Where a displacement pattern lies in world XY: an origin and a rotation about it. A pattern is a pure
/// function of world plan position through this, never of vertex indices or UVs, so a displacement stacks
/// and survives a remesh or retopo below it unchanged.
/// </summary>
public readonly struct DisplacementPlacement
{
    // Stored as cos − 1 so the default value is a valid, unrotated placement at the world origin.
    private readonly double _cosMinusOne;
    private readonly double _sin;

    public DisplacementPlacement(double originX, double originY, double rotationDegrees)
    {
        OriginX = originX;
        OriginY = originY;
        RotationDegrees = rotationDegrees;
        double radians = rotationDegrees * Math.PI / 180.0;
        _cosMinusOne = rotationDegrees == 0.0 ? 0.0 : Math.Cos(radians) - 1.0;
        _sin = rotationDegrees == 0.0 ? 0.0 : Math.Sin(radians);
    }

    public double OriginX { get; }

    public double OriginY { get; }

    /// <summary>Counter-clockwise rotation of the pattern about the origin, a true angle in degrees.</summary>
    public double RotationDegrees { get; }

    private double Cos => 1.0 + _cosMinusOne;

    /// <summary>World (x, y) in the pattern's own axes: offset from the origin, then rotated back.</summary>
    public (double U, double V) ToLocal(double x, double y)
    {
        double dx = x - OriginX;
        double dy = y - OriginY;
        double cos = Cos;
        return ((dx * cos) + (dy * _sin), (dy * cos) - (dx * _sin));
    }

    /// <summary>A point in the pattern's own axes back to world (x, y).</summary>
    public (double X, double Y) ToWorld(double u, double v)
    {
        double cos = Cos;
        return (OriginX + (u * cos) - (v * _sin), OriginY + (u * _sin) + (v * cos));
    }
}
