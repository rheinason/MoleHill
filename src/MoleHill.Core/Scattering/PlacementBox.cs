namespace MoleHill.Core.Scattering;

/// <summary>Deterministic, bounded approximation to a minimum-volume oriented box.</summary>
public sealed record PlacementBox(PlacementVector[] Corners)
{
    public static bool TryFit(IReadOnlyList<PlacementVector> points, out PlacementBox? box)
    {
        box = null;
        if (!PlacementLinearAlgebra.TryPrincipalAxes(points, out var center, out var axes, out var moments) ||
            moments[1] <= Math.Max(1e-24, moments[2] * 1e-12)) return false;
        // Bound the search cost independently of asset density. Final extents still enclose every point.
        var sample = new List<PlacementVector>();
        int stride = Math.Max(1, (points.Count + 2047) / 2048);
        for (int i = 0; i < points.Count; i += stride) sample.Add(points[i] - center);
        PlacementVector[] best = { axes[2], axes[1], PlacementVector.Cross(axes[2], axes[1]).Unit };
        double bestVolume = Volume(sample, best);
        PlacementVector[][] seeds = { best, new[] { new PlacementVector(1, 0, 0), new PlacementVector(0, 1, 0), new PlacementVector(0, 0, 1) } };
        foreach (var seed in seeds)
        {
            var current = (PlacementVector[])seed.Clone();
            double volume = Volume(sample, current);
            for (double step = Math.PI / 8; step >= Math.PI / 1800; step *= 0.5)
            {
                for (int sweep = 0; sweep < 8; sweep++)
                {
                    bool changed = false;
                    for (int axis = 0; axis < 3; axis++)
                        foreach (int sign in new[] { -1, 1 })
                        {
                            var candidate = (PlacementVector[])current.Clone();
                            int j = (axis + 1) % 3, k = (axis + 2) % 3;
                            double c = Math.Cos(step), s = sign * Math.Sin(step);
                            candidate[j] = current[j] * c + current[k] * s;
                            candidate[k] = current[k] * c - current[j] * s;
                            double next = Volume(sample, candidate);
                            if (next < volume * (1 - 1e-10)) { current = candidate; volume = next; changed = true; }
                        }
                    if (!changed) break;
                }
            }
            if (volume < bestVolume * (1 - 1e-10)) { best = current; bestVolume = volume; }
        }
        var relative = points.Select(p => p - center).ToArray();
        Bounds(relative, best, out var low, out var high);
        // Longest dimension is forward, shortest is up. Fix signs deterministically.
        var order = Enumerable.Range(0, 3).OrderByDescending(i => high[i] - low[i]).ToArray();
        var x = best[order[0]];
        var z = best[order[2]];
        if (z.Z < 0) z *= -1;
        if (x.X < -1e-12 || (Math.Abs(x.X) <= 1e-12 && x.Y < 0)) x *= -1;
        best = new[] { x, PlacementVector.Cross(z, x).Unit, z };
        Bounds(relative, best, out low, out high);
        int[,] selectors = { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 1, 0 }, { 0, 1, 0 }, { 0, 0, 1 }, { 1, 0, 1 }, { 1, 1, 1 }, { 0, 1, 1 } };
        var corners = new PlacementVector[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = center;
            for (int j = 0; j < 3; j++) corners[i] += best[j] * (selectors[i, j] == 0 ? low[j] : high[j]);
        }
        box = new PlacementBox(corners);
        return true;
    }

    private static double Volume(IReadOnlyList<PlacementVector> points, PlacementVector[] axes)
    {
        Bounds(points, axes, out var low, out var high);
        return (high[0] - low[0]) * (high[1] - low[1]) * (high[2] - low[2]);
    }

    private static void Bounds(IReadOnlyList<PlacementVector> points, PlacementVector[] axes, out double[] low, out double[] high)
    {
        low = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        high = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var point in points)
            for (int j = 0; j < 3; j++)
            {
                double d = PlacementVector.Dot(point, axes[j]);
                low[j] = Math.Min(low[j], d);
                high[j] = Math.Max(high[j], d);
            }
    }
}
