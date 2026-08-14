namespace MoleHill.Core.Sculpting;

/// <summary>
/// World-XY protection mask for sculpting. An influence of 0 pins the terrain to the incoming
/// surface; 1 applies the full sculpt displacement. Influence ramps smoothly through
/// <see cref="FeatherDistance"/> outside protected polygons and buffered polylines.
/// </summary>
public sealed class SculptConstraintMask
{
    private readonly List<Region> _regions = new();

    public SculptConstraintMask(double featherDistance)
    {
        FeatherDistance = Math.Max(0.0, featherDistance);
    }

    public double FeatherDistance { get; }

    public bool IsEmpty => _regions.Count == 0;

    public void AddPolygon(double[] xyVertices, int vertexCount)
    {
        if (xyVertices == null)
            throw new ArgumentNullException(nameof(xyVertices));
        if (vertexCount < 3 || xyVertices.Length < vertexCount * 2)
            return;

        _regions.Add(new Region((double[])xyVertices.Clone(), vertexCount, true, true, 0.0));
    }

    public void AddPolyline(
        double[] xyVertices,
        int vertexCount,
        double halfWidth = 0.0,
        bool isClosed = false)
    {
        if (xyVertices == null)
            throw new ArgumentNullException(nameof(xyVertices));
        if (vertexCount < 2 || xyVertices.Length < vertexCount * 2)
            return;

        _regions.Add(new Region(
            (double[])xyVertices.Clone(),
            vertexCount,
            false,
            isClosed,
            Math.Max(0.0, halfWidth)));
    }

    /// <summary>Returns the multiplier applied to sculpt displacement at the world-XY point.</summary>
    public double EvaluateInfluence(double x, double y)
    {
        if (_regions.Count == 0)
            return 1.0;

        double nearest = double.PositiveInfinity;
        foreach (Region region in _regions)
        {
            double distance = DistanceOutsideRegion(region, x, y);
            if (distance <= 0.0)
                return 0.0;
            nearest = Math.Min(nearest, distance);
        }

        if (FeatherDistance <= 0.0 || nearest >= FeatherDistance)
            return 1.0;

        double t = Math.Clamp(nearest / FeatherDistance, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    private static double DistanceOutsideRegion(Region region, double x, double y)
    {
        if (region.IsPolygon && IsPointInPolygon(region.XyVertices, region.VertexCount, x, y))
            return 0.0;

        double distanceSquared = double.PositiveInfinity;
        int segmentCount = region.IsClosed ? region.VertexCount : region.VertexCount - 1;
        for (int segment = 0; segment < segmentCount; segment++)
        {
            int next = (segment + 1) % region.VertexCount;
            double d2 = DistanceToSegmentSquared(
                x,
                y,
                region.XyVertices[segment * 2],
                region.XyVertices[(segment * 2) + 1],
                region.XyVertices[next * 2],
                region.XyVertices[(next * 2) + 1]);
            distanceSquared = Math.Min(distanceSquared, d2);
        }

        double distance = Math.Sqrt(distanceSquared) - region.HalfWidth;
        return Math.Max(0.0, distance);
    }

    private static bool IsPointInPolygon(double[] xy, int count, double x, double y)
    {
        bool inside = false;
        int previous = count - 1;
        for (int current = 0; current < count; current++)
        {
            double xi = xy[current * 2];
            double yi = xy[(current * 2) + 1];
            double xj = xy[previous * 2];
            double yj = xy[(previous * 2) + 1];

            if (((yi > y) != (yj > y)) &&
                x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }

            previous = current;
        }

        return inside;
    }

    private static double DistanceToSegmentSquared(
        double px,
        double py,
        double ax,
        double ay,
        double bx,
        double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= double.Epsilon)
        {
            double pointDx = px - ax;
            double pointDy = py - ay;
            return (pointDx * pointDx) + (pointDy * pointDy);
        }

        double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0.0, 1.0);
        double nearestX = ax + (t * dx);
        double nearestY = ay + (t * dy);
        double offsetX = px - nearestX;
        double offsetY = py - nearestY;
        return (offsetX * offsetX) + (offsetY * offsetY);
    }

    private sealed record Region(
        double[] XyVertices,
        int VertexCount,
        bool IsPolygon,
        bool IsClosed,
        double HalfWidth);
}
