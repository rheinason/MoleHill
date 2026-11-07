using MoleHill.Core.Geometry;

namespace MoleHill.Core.Scattering;

/// <summary>Plan-space region unions with holes, exclusions, clearance and boundary feathering.</summary>
public sealed class ScatterRegionMask
{
    private readonly IReadOnlyList<IReadOnlyList<double[]>> _regions;
    private readonly IReadOnlyList<double[]> _exclusions;

    public ScatterRegionMask(IReadOnlyList<IReadOnlyList<double[]>> regions, IReadOnlyList<double[]> exclusions)
    {
        _regions = regions;
        _exclusions = exclusions;
    }

    /// <summary>Area of disjoint regions, with nested loops interpreted as holes.</summary>
    public double PlanArea => _regions.Sum(region => region.Sum(loop =>
        Math.Abs(Geometry2D.SignedArea(loop, loop.Length / 2)) *
        (region.Count(other => !ReferenceEquals(other, loop) && Geometry2D.PointInPolygon(loop[0], loop[1], other, other.Length / 2)) % 2 == 0 ? 1 : -1)));

    public bool Contains(double x, double y) => _regions.Any(region =>
        region.Count(loop => Geometry2D.PointInPolygon(x, y, loop, loop.Length / 2)) % 2 != 0);

    public bool IsExcluded(double x, double y, double clearance) => _exclusions.Any(loop =>
        Geometry2D.PointInPolygon(x, y, loop, loop.Length / 2) ||
        (clearance > 0 && Distance(loop, x, y) < clearance));

    public double BoundaryDistance(double x, double y)
    {
        double distance = 0;
        foreach (var region in _regions)
        {
            if (region.Count(loop => Geometry2D.PointInPolygon(x, y, loop, loop.Length / 2)) % 2 == 0)
                continue;
            distance = Math.Max(distance, region.Min(loop => Distance(loop, x, y)));
        }
        return distance;
    }

    public static double FeatherProbability(double distance, double setback, double feather) =>
        distance < setback ? 0 : feather > 0 ? Math.Clamp((distance - setback) / feather, 0, 1) : 1;

    /// <summary>Conservative whole-box check, also rejecting a hole/exclusion enclosed by the box.</summary>
    public bool ContainsFootprint(double[] footprint, double setback, double exclusionClearance)
    {
        for (int i = 0; i < footprint.Length; i += 2)
        {
            double x = footprint[i], y = footprint[i + 1];
            if (!Contains(x, y) || IsExcluded(x, y, exclusionClearance) || BoundaryDistance(x, y) < setback)
                return false;
        }
        foreach (double[] loop in _regions.SelectMany(region => region).Concat(_exclusions))
        {
            // A boundary passing through or enclosed by the box may be a concave notch or a hole.
            if (Geometry2D.PointInPolygon(loop[0], loop[1], footprint, footprint.Length / 2))
                return false;
            for (int i = 0; i < footprint.Length; i += 2)
            {
                int next = (i + 2) % footprint.Length;
                for (int j = 0; j < loop.Length; j += 2)
                {
                    int k = (j + 2) % loop.Length;
                    if (Geometry2D.SegmentsTouch(footprint[i], footprint[i + 1], footprint[next], footprint[next + 1],
                            loop[j], loop[j + 1], loop[k], loop[k + 1]))
                        return false;
                    double clearance = _exclusions.Contains(loop) ? exclusionClearance : setback;
                    if (clearance > 0 && (SegmentDistance(footprint[i], footprint[i + 1], footprint[next], footprint[next + 1], loop[j], loop[j + 1]) < clearance ||
                        SegmentDistance(footprint[i], footprint[i + 1], footprint[next], footprint[next + 1], loop[k], loop[k + 1]) < clearance))
                        return false;
                }
            }
        }
        return true;
    }

    private static double Distance(double[] loop, double x, double y)
    {
        double distance = double.PositiveInfinity;
        for (int i = 0; i < loop.Length; i += 2)
        {
            int j = (i + 2) % loop.Length;
            distance = Math.Min(distance, SegmentDistance(loop[i], loop[i + 1], loop[j], loop[j + 1], x, y));
        }
        return distance;
    }

    private static double SegmentDistance(double ax, double ay, double bx, double by, double x, double y)
    {
        double t = Geometry2D.ParameterOnSegmentClamped(ax, ay, bx, by, x, y);
        return Math.Sqrt(Geometry2D.DistanceSquared(Geometry2D.Lerp(ax, bx, t), Geometry2D.Lerp(ay, by, t), x, y));
    }
}
