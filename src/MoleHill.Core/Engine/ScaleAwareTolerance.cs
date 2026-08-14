namespace MoleHill.Core.Engine;

/// <summary>Numerical floors derived from the geometry's own scale rather than an assumed model unit.</summary>
internal static class ScaleAwareTolerance
{
    public static double LengthFloor(double characteristicLength)
    {
        double scale = double.IsFinite(characteristicLength) ? Math.Abs(characteristicLength) : 0.0;
        return Math.Max(scale * 1e-12, double.Epsilon);
    }

    public static double ResolveLength(double requestedTolerance, double characteristicLength)
    {
        double requested = double.IsFinite(requestedTolerance) ? Math.Abs(requestedTolerance) : 0.0;
        return Math.Max(requested, LengthFloor(characteristicLength));
    }
}
