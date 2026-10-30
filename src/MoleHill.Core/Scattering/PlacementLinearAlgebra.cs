namespace MoleHill.Core.Scattering;

internal static class PlacementLinearAlgebra
{
    // Symmetric Jacobi eigensolver, shared by box initialization and terrain plane fitting.
    internal static bool TryPrincipalAxes(IReadOnlyList<PlacementVector> points, out PlacementVector center,
        out PlacementVector[] axes, out double[] moments)
    {
        center = default;
        axes = Array.Empty<PlacementVector>();
        moments = Array.Empty<double>();
        if (points.Count < 3 || points.Any(p => !p.IsFinite)) return false;
        var anchor = points[0];
        foreach (var p in points) center += (p - anchor) / points.Count;
        center += anchor;
        var a = new double[3, 3];
        foreach (var p in points)
        {
            var d = p - center;
            double[] v = { d.X, d.Y, d.Z };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) a[i, j] += v[i] * v[j] / points.Count;
        }
        var rotation = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int iteration = 0; iteration < 40; iteration++)
        {
            int p = 0, q = 1;
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    if (Math.Abs(a[i, j]) > Math.Abs(a[p, q])) { p = i; q = j; }
            if (Math.Abs(a[p, q]) <= Math.Max(1e-30, (Math.Abs(a[0, 0]) + Math.Abs(a[1, 1]) + Math.Abs(a[2, 2])) * 1e-15)) break;
            double angle = 0.5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            double app = a[p, p], aqq = a[q, q], apq = a[p, q];
            for (int k = 0; k < 3; k++)
            {
                if (k != p && k != q)
                {
                    double kp = a[k, p], kq = a[k, q];
                    a[k, p] = a[p, k] = c * kp - s * kq;
                    a[k, q] = a[q, k] = s * kp + c * kq;
                }
                double rp = rotation[k, p], rq = rotation[k, q];
                rotation[k, p] = c * rp - s * rq;
                rotation[k, q] = s * rp + c * rq;
            }
            a[p, p] = c * c * app - 2 * s * c * apq + s * s * aqq;
            a[q, q] = s * s * app + 2 * s * c * apq + c * c * aqq;
            a[p, q] = a[q, p] = 0;
        }
        int[] order = Enumerable.Range(0, 3).OrderBy(i => a[i, i]).ToArray();
        moments = order.Select(i => Math.Max(0, a[i, i])).ToArray();
        axes = order.Select(i => new PlacementVector(rotation[0, i], rotation[1, i], rotation[2, i]).Unit).ToArray();
        return axes.All(v => v.IsFinite && v.Length > 0.9);
    }
}
