using MoleHill.Core.Geometry;

namespace MoleHill.Core.Scattering;

/// <summary>Conservative plan containment of a transformed block's convex bounds.</summary>
public static class ScatterFootprint
{
    public static bool IsInside(IReadOnlyList<double[]> boundaries, double[] footprint)
    {
        if (footprint.Length < 6 || footprint.Length % 2 != 0)
            return false;

        foreach (double[] loop in boundaries)
        {
            if (loop.Length < 6 || loop.Length % 2 != 0)
                continue;
            bool inside = true;
            for (int i = 0; i < footprint.Length && inside; i += 2)
            {
                double ax = footprint[i], ay = footprint[i + 1];
                int next = (i + 2) % footprint.Length;
                double bx = footprint[next], by = footprint[next + 1];
                if (!Contains(loop, ax, ay))
                {
                    inside = false;
                    break;
                }

                // Split edges at every intersection, including vertex touches and collinear runs.
                // Every interval must stay inside; corners alone miss concave notches.
                var cuts = new List<double> { 0, 1 };
                double ex = bx - ax, ey = by - ay;
                double lengthSquared = ex * ex + ey * ey;
                for (int j = 0; j < loop.Length; j += 2)
                {
                    int k = (j + 2) % loop.Length;
                    double cx = loop[j], cy = loop[j + 1];
                    double fx = loop[k] - cx, fy = loop[k + 1] - cy;
                    double denominator = ex * fy - ey * fx;
                    if (denominator != 0)
                    {
                        double t = ((cx - ax) * fy - (cy - ay) * fx) / denominator;
                        double u = ((cx - ax) * ey - (cy - ay) * ex) / denominator;
                        if (t > 0 && t < 1 && u >= 0 && u <= 1)
                            cuts.Add(t);
                    }
                    else if (lengthSquared > 0 && Geometry2D.Orient(ax, ay, bx, by, cx, cy) == 0)
                    {
                        cuts.Add(Math.Clamp(((cx - ax) * ex + (cy - ay) * ey) / lengthSquared, 0, 1));
                        cuts.Add(Math.Clamp(((loop[k] - ax) * ex + (loop[k + 1] - ay) * ey) / lengthSquared, 0, 1));
                    }
                }
                cuts.Sort();
                for (int j = 1; j < cuts.Count && inside; j++)
                {
                    double t = (cuts[j - 1] + cuts[j]) * 0.5;
                    inside = Contains(loop, ax + ex * t, ay + ey * t);
                }
            }
            if (inside)
                return true;
        }
        return false;
    }

    private static bool Contains(double[] loop, double x, double y)
    {
        for (int i = 0; i < loop.Length; i += 2)
        {
            int j = (i + 2) % loop.Length;
            if (Geometry2D.Orient(loop[i], loop[i + 1], loop[j], loop[j + 1], x, y) == 0 &&
                Geometry2D.InSegmentBox(loop[i], loop[i + 1], loop[j], loop[j + 1], x, y, epsilon: 0))
                return true;
        }
        return Geometry2D.PointInPolygon(x, y, loop, loop.Length / 2);
    }
}
