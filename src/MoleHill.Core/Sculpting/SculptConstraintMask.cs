using MoleHill.Core.Geometry;
using MoleHill.Core.Grading;

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

        _regions.Add(Region.Create((double[])xyVertices.Clone(), vertexCount, true, true, 0.0));
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

        _regions.Add(Region.Create(
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
            // Bounds rejection. The distance from the point to the region's bounding box, less its
            // half width, is a lower bound on DistanceOutsideRegion. A region whose lower bound is
            // both positive and at or beyond the feather distance can neither pin the point nor pull
            // `nearest` below the feather threshold, so its edges never need visiting - and the
            // remaining regions decide the same answer. Every sculpt vertex and field sample pays this
            // loop, so a scene with many protected areas was scanning every edge of all of them.
            double lowerBound = region.DistanceOutsideBounds(x, y);
            if (lowerBound > 0.0 && lowerBound >= FeatherDistance)
                continue;

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
        // A point outside the bounding box is outside the polygon; skip the ray cast for it.
        if (region.IsPolygon &&
            region.DistanceOutsideBounds(x, y) <= 0.0 &&
            Geometry2D.PointInPolygon(x, y, region.XyVertices, region.VertexCount))
        {
            return 0.0;
        }

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
        double HalfWidth,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY)
    {
        public static Region Create(double[] xyVertices, int vertexCount, bool isPolygon, bool isClosed, double halfWidth)
        {
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            for (int i = 0; i < vertexCount; i++)
            {
                double x = xyVertices[i * 2];
                double y = xyVertices[(i * 2) + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            return new Region(xyVertices, vertexCount, isPolygon, isClosed, halfWidth, minX, maxX, minY, maxY);
        }

        /// <summary>
        /// Lower bound on <c>DistanceOutsideRegion</c>: the distance from the point to the region's
        /// bounding box, less the half width, clamped at zero. Never larger than the true value.
        /// </summary>
        public double DistanceOutsideBounds(double x, double y)
        {
            double dx = Math.Max(Math.Max(MinX - x, x - MaxX), 0.0);
            double dy = Math.Max(Math.Max(MinY - y, y - MaxY), 0.0);
            if (dx == 0.0 && dy == 0.0)
                return 0.0;

            return Math.Max(0.0, Math.Sqrt((dx * dx) + (dy * dy)) - HalfWidth);
        }
    }
}
