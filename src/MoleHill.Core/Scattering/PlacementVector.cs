namespace MoleHill.Core.Scattering;

/// <summary>Double precision point/vector used by the host-independent placement solver.</summary>
public readonly record struct PlacementVector(double X, double Y, double Z)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public double Length => Math.Sqrt(Dot(this, this));
    public PlacementVector Unit => Length > 0 ? this / Length : default;
    public static PlacementVector operator +(PlacementVector a, PlacementVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static PlacementVector operator -(PlacementVector a, PlacementVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static PlacementVector operator *(PlacementVector a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public static PlacementVector operator /(PlacementVector a, double b) => new(a.X / b, a.Y / b, a.Z / b);
    public static double Dot(PlacementVector a, PlacementVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static PlacementVector Cross(PlacementVector a, PlacementVector b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
