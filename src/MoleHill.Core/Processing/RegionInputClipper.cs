using MoleHill.Core.Grading;

namespace MoleHill.Core.Processing;

/// <summary>Exact World-XY clipping of raw terrain inputs to a union of closed polygons.</summary>
public static class RegionInputClipper
{
    public readonly record struct InputPolyline(double[] Points, int PointCount, bool IsClosed);

    public static bool[] KeepPointsInside(
        double[] pointsXyz,
        int pointCount,
        IReadOnlyList<MeshAreaSplitter.AreaBoundary> polygons,
        double tolerance)
    {
        var keep = new bool[pointCount];
        if (polygons.Count == 0)
        {
            Array.Fill(keep, true);
            return keep;
        }

        for (int i = 0; i < pointCount; i++)
        {
            double x = pointsXyz[i * 3];
            double y = pointsXyz[i * 3 + 1];
            keep[i] = IsInsideAny(x, y, polygons, tolerance);
        }

        return keep;
    }

    public static List<InputPolyline> ClipPolylines(
        IReadOnlyList<InputPolyline> polylines,
        IReadOnlyList<MeshAreaSplitter.AreaBoundary> polygons,
        double tolerance,
        Func<bool>? shouldCancel = null)
    {
        if (polygons.Count == 0)
            return polylines.ToList();

        // Bounding-box reject: a segment that stays farther than the tolerance from a polygon's box keeps
        // nothing from that polygon (the clipper keeps only pieces whose midpoint is inside or within
        // tolerance of it), so skip that polygon — or the whole polyline — without the per-edge test.
        double margin = Math.Abs(tolerance) + 1e-9;
        var polygonBounds = new Bounds[polygons.Count];
        for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
            polygonBounds[polygonIndex] = Bounds.Of(polygons[polygonIndex], margin);
        var candidates = new List<MeshAreaSplitter.AreaBoundary>(polygons.Count);

        var result = new List<InputPolyline>();
        foreach (InputPolyline polyline in polylines)
        {
            if (shouldCancel?.Invoke() == true)
                throw new OperationCanceledException();
            if (polyline.PointCount < 2 || polyline.Points.Length < polyline.PointCount * 3)
                continue;

            Bounds polylineBounds = Bounds.Of(polyline.Points, polyline.PointCount);
            if (!Array.Exists(polygonBounds, bounds => bounds.Overlaps(polylineBounds)))
                continue;

            var current = new List<double>();
            int segmentCount = polyline.IsClosed ? polyline.PointCount : polyline.PointCount - 1;
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                int next = (segmentIndex + 1) % polyline.PointCount;
                int a = segmentIndex * 3;
                int b = next * 3;
                var segmentBounds = new Bounds(
                    Math.Min(polyline.Points[a], polyline.Points[b]),
                    Math.Min(polyline.Points[a + 1], polyline.Points[b + 1]),
                    Math.Max(polyline.Points[a], polyline.Points[b]),
                    Math.Max(polyline.Points[a + 1], polyline.Points[b + 1]));
                candidates.Clear();
                for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
                {
                    if (polygonBounds[polygonIndex].Overlaps(segmentBounds))
                        candidates.Add(polygons[polygonIndex]);
                }

                // No candidate means no interval, which leaves the current run untouched — as before.
                if (candidates.Count == 0)
                    continue;

                List<(double Start, double End)> intervals = CollectUnionIntervals(
                    polyline.Points[a], polyline.Points[a + 1], polyline.Points[a + 2],
                    polyline.Points[b], polyline.Points[b + 1], polyline.Points[b + 2],
                    candidates, tolerance);

                foreach ((double start, double end) in intervals)
                {
                    double sx = Lerp(polyline.Points[a], polyline.Points[b], start);
                    double sy = Lerp(polyline.Points[a + 1], polyline.Points[b + 1], start);
                    double sz = Lerp(polyline.Points[a + 2], polyline.Points[b + 2], start);
                    double ex = Lerp(polyline.Points[a], polyline.Points[b], end);
                    double ey = Lerp(polyline.Points[a + 1], polyline.Points[b + 1], end);
                    double ez = Lerp(polyline.Points[a + 2], polyline.Points[b + 2], end);

                    if (current.Count == 0 || !SamePoint(current, sx, sy, sz, tolerance))
                    {
                        Flush(current, result);
                        current.AddRange(new[] { sx, sy, sz });
                    }

                    current.AddRange(new[] { ex, ey, ez });
                    if (end < 1.0 - 1e-9)
                        Flush(current, result);
                }
            }

            Flush(current, result);
        }

        return result;
    }

    private static List<(double Start, double End)> CollectUnionIntervals(
        double ax, double ay, double az,
        double bx, double by, double bz,
        IReadOnlyList<MeshAreaSplitter.AreaBoundary> polygons,
        double tolerance)
    {
        var intervals = new List<(double Start, double End)>();
        foreach (MeshAreaSplitter.AreaBoundary polygon in polygons)
        {
            foreach (ClippedSegment piece in BoundaryClipper.ClipSegmentToBoundary(
                ax, ay, az, bx, by, bz, true, polygon.XyVertices, polygon.VertexCount, tolerance))
            {
                intervals.Add((piece.StartT, piece.EndT));
            }
        }

        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(double Start, double End)>();
        foreach ((double start, double end) in intervals)
        {
            if (merged.Count == 0 || start > merged[^1].End + 1e-9)
            {
                merged.Add((start, end));
                continue;
            }

            (double oldStart, double oldEnd) = merged[^1];
            merged[^1] = (oldStart, Math.Max(oldEnd, end));
        }

        return merged;
    }

    private static bool IsInsideAny(
        double x,
        double y,
        IReadOnlyList<MeshAreaSplitter.AreaBoundary> polygons,
        double tolerance)
    {
        foreach (MeshAreaSplitter.AreaBoundary polygon in polygons)
        {
            if (BoundaryClipper.IsInsideOrOnBoundary(
                x, y, true, polygon.XyVertices, polygon.VertexCount, tolerance))
                return true;
        }

        return false;
    }

    private static bool SamePoint(List<double> points, double x, double y, double z, double tolerance)
    {
        int offset = points.Count - 3;
        double dx = points[offset] - x;
        double dy = points[offset + 1] - y;
        double dz = points[offset + 2] - z;
        double resolved = Math.Max(tolerance, 1e-9);
        return (dx * dx) + (dy * dy) + (dz * dz) <= resolved * resolved;
    }

    private static void Flush(List<double> current, List<InputPolyline> result)
    {
        if (current.Count >= 6)
            result.Add(new InputPolyline(current.ToArray(), current.Count / 3, IsClosed: false));
        current.Clear();
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool Overlaps(Bounds other) =>
            MinX <= other.MaxX && other.MinX <= MaxX && MinY <= other.MaxY && other.MinY <= MaxY;

        /// <summary>
        /// A polygon's XY box grown by <paramref name="margin"/>. A polygon with fewer than three vertices
        /// is unbounded, because the clipper keeps a whole segment for one.
        /// </summary>
        public static Bounds Of(MeshAreaSplitter.AreaBoundary polygon, double margin)
        {
            if (polygon.VertexCount < 3)
                return new Bounds(double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity, double.PositiveInfinity);

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i < polygon.VertexCount; i++)
            {
                double x = polygon.XyVertices[i * 2];
                double y = polygon.XyVertices[(i * 2) + 1];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            return new Bounds(minX - margin, minY - margin, maxX + margin, maxY + margin);
        }

        public static Bounds Of(double[] pointsXyz, int pointCount)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i < pointCount; i++)
            {
                double x = pointsXyz[i * 3];
                double y = pointsXyz[(i * 3) + 1];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            return new Bounds(minX, minY, maxX, maxY);
        }
    }
}
