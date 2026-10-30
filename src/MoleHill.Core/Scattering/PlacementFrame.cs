namespace MoleHill.Core.Scattering;

/// <summary>A rigid placement frame. X follows the requested heading in plan.</summary>
public readonly record struct PlacementFrame(PlacementVector Origin, PlacementVector XAxis, PlacementVector YAxis)
{
    public PlacementVector Normal => PlacementVector.Cross(XAxis, YAxis).Unit;

    public static bool TryFit(IReadOnlyList<PlacementVector> hits, PlacementVector heading, out PlacementFrame frame)
    {
        frame = default;
        if (!PlacementLinearAlgebra.TryPrincipalAxes(hits, out var center, out var axes, out var moments))
            return false;
        if (moments[1] <= Math.Max(1e-24, moments[2] * 1e-12))
            return false;
        var normal = axes[0];
        if (normal.Z < 0) normal *= -1;
        if (normal.Z < 1e-6) return false;
        heading = new PlacementVector(heading.X, heading.Y, 0);
        if (heading.Length < 1e-12) return false;
        // Orthogonal projection onto the plane changes the heading in plan. Lift the
        // horizontal direction instead, so a driver's heading survives a cross slope.
        var x = new PlacementVector(heading.X, heading.Y,
            -(normal.X * heading.X + normal.Y * heading.Y) / normal.Z).Unit;
        frame = new PlacementFrame(center, x, PlacementVector.Cross(normal, x).Unit);
        return true;
    }
}
