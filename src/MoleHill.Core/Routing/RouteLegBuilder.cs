using MoleHill.Core.Geometry;

namespace MoleHill.Core.Routing;

/// <summary>
/// Turns one leg's grid path into a 3D polyline: the plan straightened, the profile fitted to the ground
/// within the grade limit, and the profile thinned to the points that carry it.
/// </summary>
internal static class RouteLegBuilder
{
    /// <summary>Longest stretch of grid path one straightened chord may replace, which bounds the work per vertex.</summary>
    private const int MaxChordNodes = 400;

    /// <summary>
    /// Share of a chord sample's bilinear weight that must be ground. Edges between two ground nodes
    /// read 1; at 0.75 a chord may not cut more than a sliver off a cell with an off-terrain corner.
    /// </summary>
    private const double ChordGroundWeight = 0.75;

    /// <summary>How many rejected chords in a row end the search for a longer one.</summary>
    private const int MaxChordMisses = 16;

    /// <summary>Ground sample spacing along the leg, in cells.</summary>
    private const double StationSpacingCells = 0.5;

    public static bool TryBuild(
        RouteHeightGrid grid,
        RouteRequest request,
        List<int> nodes,
        (double X, double Y) start,
        (double X, double Y) end,
        double startZ,
        double endZ,
        bool straighten,
        double profileTolerance,
        out List<double> pointsXyz,
        out double requiredGrade)
    {
        // The grid path runs between nodes; the leg itself starts and ends exactly at the picked points.
        var plan = new List<(double X, double Y)>(nodes.Count + 1);
        foreach (int node in nodes)
            plan.Add((grid.NodeX(node % grid.Columns), grid.NodeY(node / grid.Columns)));
        if (plan.Count == 1)
            plan.Add(end);
        plan[0] = start;
        plan[^1] = end;

        if (straighten)
            plan = StraightenPlan(grid, new RouteCost(request), plan);

        double spacing = StationSpacingCells * grid.CellSize;
        var stations = new List<double>();
        var xs = new List<double>();
        var ys = new List<double>();
        var corners = new List<int>();
        double station = 0.0;
        stations.Add(0.0);
        xs.Add(plan[0].X);
        ys.Add(plan[0].Y);
        corners.Add(0);
        for (int i = 1; i < plan.Count; i++)
        {
            (double ax, double ay) = plan[i - 1];
            (double bx, double by) = plan[i];
            double length = Math.Sqrt(Geometry2D.DistanceSquared(ax, ay, bx, by));
            if (length <= 0.0)
                continue;

            int pieces = Math.Max(1, (int)Math.Ceiling(length / spacing));
            for (int k = 1; k <= pieces; k++)
            {
                double t = (double)k / pieces;
                stations.Add(station + (length * t));
                xs.Add(Geometry2D.Lerp(ax, bx, t));
                ys.Add(Geometry2D.Lerp(ay, by, t));
            }

            station += length;
            corners.Add(stations.Count - 1);
        }

        if (stations.Count == 1)
        {
            // Both ends on the same spot: a degenerate leg is just its start.
            pointsXyz = new List<double> { plan[0].X, plan[0].Y, startZ };
            requiredGrade = 0.0;
            return true;
        }

        var ground = new double[stations.Count];
        double last = startZ;
        for (int i = 0; i < stations.Count; i++)
        {
            if (grid.TrySample(xs[i], ys[i], out double z))
                last = z;
            ground[i] = last;
        }

        if (!GradeLimitedProfile.TryFit(stations, ground, request.MaxGrade, startZ, endZ, out double[] profile, out requiredGrade))
        {
            pointsXyz = new List<double>();
            return false;
        }

        // Thin within each straight stretch only, so every plan corner survives with its own elevation.
        var keep = new bool[stations.Count];
        foreach (int corner in corners)
            keep[corner] = true;
        for (int c = 1; c < corners.Count; c++)
            MarkProfileBreaks(stations, profile, corners[c - 1], corners[c], profileTolerance, keep);

        pointsXyz = new List<double>();
        for (int i = 0; i < stations.Count; i++)
        {
            if (!keep[i])
                continue;
            pointsXyz.Add(xs[i]);
            pointsXyz.Add(ys[i]);
            pointsXyz.Add(profile[i]);
        }

        return true;
    }

    /// <summary>
    /// Cost-aware string pulling: from each kept vertex, jump to the farthest later path vertex whose
    /// straight chord costs no more than the stretch of path it replaces, priced by the same
    /// <see cref="RouteCost"/> the search used and sampled every half cell. On even ground that is the
    /// straight line (a grid path is only ever an approximation of one); round a hill it is the tightest
    /// line the costs allow; and a chord over a hole is never taken, because off-terrain samples make it
    /// infinitely expensive.
    /// </summary>
    internal static List<(double X, double Y)> StraightenPlan(RouteHeightGrid grid, RouteCost price, List<(double X, double Y)> plan)
    {
        int n = plan.Count;
        if (n <= 2)
            return plan;

        double spacing = StationSpacingCells * grid.CellSize;
        var prefix = new double[n];
        for (int i = 1; i < n; i++)
        {
            double cost = ChordCost(grid, price, plan[i - 1], plan[i], spacing, 1e-9);
            if (double.IsPositiveInfinity(cost))
                cost = Math.Sqrt(Geometry2D.DistanceSquared(plan[i - 1].X, plan[i - 1].Y, plan[i].X, plan[i].Y)) * price.Factor(0.0);
            prefix[i] = prefix[i - 1] + cost;
        }

        var straightened = new List<(double X, double Y)> { plan[0] };
        int anchor = 0;
        while (anchor < n - 1)
        {
            int best = anchor + 1;
            int misses = 0;
            for (int j = anchor + 2; j < n && j - anchor <= MaxChordNodes; j++)
            {
                double chord = ChordCost(grid, price, plan[anchor], plan[j], spacing, ChordGroundWeight);
                double path = prefix[j] - prefix[anchor];
                if (chord <= (path * (1.0 + 1e-9)) + 1e-12)
                {
                    best = j;
                    misses = 0;
                }
                else if (++misses > MaxChordMisses)
                {
                    break;
                }
            }

            straightened.Add(plan[best]);
            anchor = best;
        }

        return straightened;
    }

    /// <summary>A polyline's cost by the search's pricing, from its plan coordinates and the ground under them.</summary>
    internal static double PolylineCost(RouteHeightGrid grid, RouteCost price, double[] pointsXyz)
    {
        double spacing = StationSpacingCells * grid.CellSize;
        double total = 0.0;
        for (int i = 1; i < pointsXyz.Length / 3; i++)
        {
            (double X, double Y) a = (pointsXyz[(i - 1) * 3], pointsXyz[((i - 1) * 3) + 1]);
            (double X, double Y) b = (pointsXyz[i * 3], pointsXyz[(i * 3) + 1]);
            double cost = ChordCost(grid, price, a, b, spacing, 1e-9);
            total += double.IsPositiveInfinity(cost)
                ? Math.Sqrt(Geometry2D.DistanceSquared(a.X, a.Y, b.X, b.Y)) * price.Factor(0.0)
                : cost;
        }

        return total;
    }

    /// <summary>Cost of a straight chord over the sampled ground; infinite where it leaves the terrain.</summary>
    private static double ChordCost(
        RouteHeightGrid grid,
        RouteCost price,
        (double X, double Y) a,
        (double X, double Y) b,
        double spacing,
        double minimumWeight)
    {
        double length = Math.Sqrt(Geometry2D.DistanceSquared(a.X, a.Y, b.X, b.Y));
        if (length <= 0.0)
            return 0.0;

        int pieces = Math.Max(1, (int)Math.Ceiling(length / spacing));
        double piece = length / pieces;
        if (!grid.TrySample(a.X, a.Y, minimumWeight, out double previous))
            return double.PositiveInfinity;

        double total = 0.0;
        for (int k = 1; k <= pieces; k++)
        {
            double t = (double)k / pieces;
            if (!grid.TrySample(Geometry2D.Lerp(a.X, b.X, t), Geometry2D.Lerp(a.Y, b.Y, t), minimumWeight, out double z))
                return double.PositiveInfinity;

            total += piece * price.Factor(Math.Abs(z - previous) / piece);
            previous = z;
        }

        return total;
    }

    /// <summary>Douglas-Peucker on (station, elevation) between two kept stations, vertical deviation only.</summary>
    private static void MarkProfileBreaks(
        List<double> stations,
        double[] profile,
        int first,
        int last,
        double tolerance,
        bool[] keep)
    {
        var spans = new Stack<(int First, int Last)>();
        spans.Push((first, last));
        while (spans.Count > 0)
        {
            (int a, int b) = spans.Pop();
            if (b - a < 2)
                continue;

            double run = stations[b] - stations[a];
            int farthest = -1;
            double deviation = -1.0;
            for (int i = a + 1; i < b; i++)
            {
                double t = run > 0.0 ? (stations[i] - stations[a]) / run : 0.0;
                double d = Math.Abs(profile[i] - Geometry2D.Lerp(profile[a], profile[b], t));
                if (d > deviation)
                {
                    deviation = d;
                    farthest = i;
                }
            }

            if (deviation <= tolerance)
                continue;

            keep[farthest] = true;
            spans.Push((a, farthest));
            spans.Push((farthest, b));
        }
    }
}
