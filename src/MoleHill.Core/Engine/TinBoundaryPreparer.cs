namespace MoleHill.Core.Engine;

/// <summary>
/// Adds an explicit or inferred outer boundary loop to TIN inputs before triangulation.
/// </summary>
public static class TinBoundaryPreparer
{
    public readonly record struct BoundaryPolyline(double[] Points, int PointCount, bool IsClosed);

    public enum BoundaryMode
    {
        None,
        Explicit,
        InferredEndpointHull
    }

    public sealed class PreparedTinInput
    {
        public required double[] XyCoords { get; init; }

        public required double[] ZValues { get; init; }

        public required int[] Segments { get; init; }

        public required bool UseConvexHull { get; init; }

        public required BoundaryMode Mode { get; init; }

        public string? InfoMessage { get; init; }

        public string? WarningMessage { get; init; }
    }

    private readonly record struct IndexedPoint(int Index, double X, double Y);

    private sealed class VertexReuseLookup
    {
        private readonly List<double> _xyList;
        private readonly double _toleranceSquared;
        private readonly double _inverseCellSize;
        private readonly Dictionary<long, List<int>> _cells = new();

        public VertexReuseLookup(List<double> xyList, double tolerance)
        {
            _xyList = xyList;
            double resolvedTolerance = Math.Max(tolerance, 1e-9);
            _toleranceSquared = resolvedTolerance * resolvedTolerance;
            _inverseCellSize = 1.0 / resolvedTolerance;

            int vertexCount = xyList.Count / 2;
            for (int i = 0; i < vertexCount; i++)
                Register(i, xyList[i * 2], xyList[i * 2 + 1]);
        }

        public bool TryFind(double x, double y, out int index)
        {
            long cellX = ToCell(x);
            long cellY = ToCell(y);
            double bestDistanceSquared = double.MaxValue;
            int bestIndex = -1;

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!_cells.TryGetValue(PackCellKey(cellX + dx, cellY + dy), out var list))
                        continue;

                    foreach (int candidate in list)
                    {
                        double vx = _xyList[candidate * 2];
                        double vy = _xyList[candidate * 2 + 1];
                        double deltaX = vx - x;
                        double deltaY = vy - y;
                        double distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
                        if (distanceSquared >= _toleranceSquared || distanceSquared >= bestDistanceSquared)
                            continue;

                        bestDistanceSquared = distanceSquared;
                        bestIndex = candidate;
                    }
                }
            }

            index = bestIndex;
            return bestIndex >= 0;
        }

        public void Register(int index, double x, double y)
        {
            long key = PackCellKey(ToCell(x), ToCell(y));
            if (!_cells.TryGetValue(key, out var list))
            {
                list = new List<int>(4);
                _cells[key] = list;
            }

            list.Add(index);
        }

        private long ToCell(double value) => (long)Math.Floor(value * _inverseCellSize);
    }

    public static PreparedTinInput Prepare(
        double[] xyCoords,
        double[] zValues,
        int[] segments,
        IReadOnlyList<BoundaryPolyline> boundaryPolylines,
        double tolerance)
    {
        if (TryBuildExplicitBoundaryInput(
            xyCoords,
            zValues,
            segments,
            boundaryPolylines,
            tolerance,
            out var explicitInput,
            out var boundaryWarning))
            return explicitInput;

        if (TryBuildInferredBoundaryInput(xyCoords, zValues, segments, tolerance, out var inferredInput))
            return WithWarning(inferredInput, boundaryWarning);

        return new PreparedTinInput
        {
            XyCoords = xyCoords,
            ZValues = zValues,
            Segments = segments,
            UseConvexHull = true,
            Mode = BoundaryMode.None,
            WarningMessage = boundaryWarning
        };
    }

    private static bool TryBuildExplicitBoundaryInput(
        double[] xyCoords,
        double[] zValues,
        int[] segments,
        IReadOnlyList<BoundaryPolyline> boundaryPolylines,
        double tolerance,
        out PreparedTinInput prepared,
        out string? warning)
    {
        prepared = null!;
        warning = null;

        if (!TrySelectBoundaryPolyline(boundaryPolylines, tolerance, out var boundary, out warning))
            return false;

        var xyList = xyCoords.ToList();
        var zList = zValues.ToList();
        var segmentList = new List<(int a, int b)>(segments.Length / 2 + boundary.PointCount);
        var segmentKeys = new HashSet<long>();

        CopySegments(segments, xyCoords.Length / 2, segmentList, segmentKeys);

        int normalizedCount = NormalizeBoundaryCount(boundary, tolerance);
        double reuseTolerance = Math.Max(Math.Min(tolerance, 1e-3), 1e-9);
        var loop = new List<int>(normalizedCount);
        var vertexLookup = new VertexReuseLookup(xyList, reuseTolerance);

        for (int i = 0; i < normalizedCount; i++)
        {
            double x = boundary.Points[i * 3];
            double y = boundary.Points[i * 3 + 1];
            if (vertexLookup.TryFind(x, y, out int existing))
            {
                loop.Add(existing);
                continue;
            }

            int newIndex = zList.Count;
            loop.Add(newIndex);
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(double.NaN);
            vertexLookup.Register(newIndex, x, y);
        }

        AddClosedLoop(segmentList, segmentKeys, loop);

        prepared = new PreparedTinInput
        {
            XyCoords = xyList.ToArray(),
            ZValues = zList.ToArray(),
            Segments = FlattenSegments(segmentList),
            UseConvexHull = false,
            Mode = BoundaryMode.Explicit,
            InfoMessage = "Using explicit terrain boundary for TIN generation.",
            WarningMessage = warning
        };
        return true;
    }

    private static bool TryBuildInferredBoundaryInput(
        double[] xyCoords,
        double[] zValues,
        int[] segments,
        double tolerance,
        out PreparedTinInput prepared)
    {
        prepared = null!;
        int vertexCount = xyCoords.Length / 2;
        if (segments.Length < 6)
            return false;

        var degree = new int[vertexCount];
        for (int i = 0; i < segments.Length / 2; i++)
        {
            int a = segments[i * 2];
            int b = segments[i * 2 + 1];
            if (a < 0 || b < 0 || a >= vertexCount || b >= vertexCount || a == b)
                continue;

            degree[a]++;
            degree[b]++;
        }

        var endpointIndices = new List<int>();
        for (int i = 0; i < degree.Length; i++)
        {
            if (degree[i] == 1)
                endpointIndices.Add(i);
        }

        if (endpointIndices.Count < 3)
            return false;

        var hull = ComputeConvexHull(endpointIndices, xyCoords);
        double hullDiagonal = ComputeHullDiagonal(hull, xyCoords);
        RemoveNearCollinearHullVertices(hull, xyCoords, Math.Max(Math.Max(tolerance * 4.0, hullDiagonal * 0.001), 1e-6));
        if (hull.Count < 3)
            return false;

        var directHull = new double[hull.Count * 2];
        for (int i = 0; i < hull.Count; i++)
        {
            directHull[i * 2] = xyCoords[hull[i] * 2];
            directHull[i * 2 + 1] = xyCoords[hull[i] * 2 + 1];
        }

        bool useDirectHull = true;
        for (int i = 0; i < vertexCount; i++)
        {
            if (!IsInsideOrOnBoundary(xyCoords[i * 2], xyCoords[i * 2 + 1], directHull, tolerance))
            {
                useDirectHull = false;
                break;
            }
        }

        var xyList = xyCoords.ToList();
        var zList = zValues.ToList();
        var segmentList = new List<(int a, int b)>(segments.Length / 2 + hull.Count);
        var segmentKeys = new HashSet<long>();
        CopySegments(segments, vertexCount, segmentList, segmentKeys);

        if (useDirectHull)
        {
            AddClosedLoop(segmentList, segmentKeys, hull);
        }
        else
        {
            var offsetHull = BuildOffsetHull(hull, xyCoords, tolerance, hullDiagonal);
            for (int i = 0; i < vertexCount; i++)
            {
                if (!IsInsideOrOnBoundary(xyCoords[i * 2], xyCoords[i * 2 + 1], offsetHull, tolerance))
                    return false;
            }

            var loop = new List<int>(offsetHull.Length / 2);
            for (int i = 0; i < offsetHull.Length / 2; i++)
            {
                loop.Add(zList.Count);
                xyList.Add(offsetHull[i * 2]);
                xyList.Add(offsetHull[i * 2 + 1]);
                zList.Add(double.NaN);
            }

            AddClosedLoop(segmentList, segmentKeys, loop);
        }

        prepared = new PreparedTinInput
        {
            XyCoords = xyList.ToArray(),
            ZValues = zList.ToArray(),
            Segments = FlattenSegments(segmentList),
            UseConvexHull = false,
            Mode = BoundaryMode.InferredEndpointHull,
            InfoMessage = $"Using inferred terrain boundary from {endpointIndices.Count} open-breakline endpoints."
        };
        return true;
    }

    private static bool TrySelectBoundaryPolyline(
        IReadOnlyList<BoundaryPolyline> boundaryPolylines,
        double tolerance,
        out BoundaryPolyline boundary,
        out string? warning)
    {
        boundary = default;
        warning = null;

        if (boundaryPolylines.Count == 0)
            return false;

        double bestArea = double.MinValue;
        int validCount = 0;

        foreach (var candidate in boundaryPolylines)
        {
            int count = NormalizeBoundaryCount(candidate, tolerance);
            if (!candidate.IsClosed || count < 3)
                continue;

            double area = Math.Abs(ComputeSignedArea(candidate.Points, count));
            if (area <= tolerance * tolerance)
                continue;

            validCount++;
            if (area > bestArea)
            {
                bestArea = area;
                boundary = candidate;
            }
        }

        if (validCount == 0)
        {
            warning = "Boundary override ignored because no valid closed boundary curve was supplied.";
            return false;
        }

        if (validCount > 1)
            warning = "Multiple closed boundary curves were supplied. Using the largest closed boundary.";

        return true;
    }

    private static int NormalizeBoundaryCount(BoundaryPolyline boundary, double tolerance)
    {
        if (!boundary.IsClosed || boundary.PointCount < 3)
            return boundary.PointCount;

        int last = boundary.PointCount - 1;
        double dx = boundary.Points[last * 3] - boundary.Points[0];
        double dy = boundary.Points[last * 3 + 1] - boundary.Points[1];
        return dx * dx + dy * dy <= tolerance * tolerance
            ? last
            : boundary.PointCount;
    }

    private static double ComputeSignedArea(double[] points, int pointCount)
    {
        double area = 0.0;
        for (int i = 0; i < pointCount; i++)
        {
            int next = (i + 1) % pointCount;
            area += points[i * 3] * points[next * 3 + 1] - points[next * 3] * points[i * 3 + 1];
        }

        return area * 0.5;
    }

    private static void CopySegments(
        int[] segments,
        int vertexCount,
        List<(int a, int b)> segmentList,
        HashSet<long> segmentKeys)
    {
        for (int i = 0; i < segments.Length / 2; i++)
        {
            int a = segments[i * 2];
            int b = segments[i * 2 + 1];
            if (a < 0 || b < 0 || a >= vertexCount || b >= vertexCount || a == b)
                continue;

            TryAddSegment(segmentList, segmentKeys, a, b);
        }
    }

    private static void AddClosedLoop(
        List<(int a, int b)> segmentList,
        HashSet<long> segmentKeys,
        IReadOnlyList<int> loop)
    {
        if (loop.Count < 3)
            return;

        for (int i = 0; i < loop.Count; i++)
        {
            int next = (i + 1) % loop.Count;
            TryAddSegment(segmentList, segmentKeys, loop[i], loop[next]);
        }
    }

    private static bool TryAddSegment(
        List<(int a, int b)> segments,
        HashSet<long> segmentKeys,
        int a,
        int b)
    {
        if (a == b)
            return false;

        long key = GetEdgeKey(a, b);
        if (!segmentKeys.Add(key))
            return false;

        segments.Add((a, b));
        return true;
    }

    private static int[] FlattenSegments(List<(int a, int b)> segments)
    {
        var result = new int[segments.Count * 2];
        for (int i = 0; i < segments.Count; i++)
        {
            result[i * 2] = segments[i].a;
            result[i * 2 + 1] = segments[i].b;
        }

        return result;
    }

    private static List<int> ComputeConvexHull(IReadOnlyList<int> indices, double[] xyCoords)
    {
        var points = indices
            .Select(index => new IndexedPoint(index, xyCoords[index * 2], xyCoords[index * 2 + 1]))
            .DistinctBy(point => (point.X, point.Y))
            .OrderBy(point => point.X)
            .ThenBy(point => point.Y)
            .ToList();

        if (points.Count < 3)
            return new List<int>();

        var lower = new List<IndexedPoint>();
        foreach (var point in points)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 0.0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(point);
        }

        var upper = new List<IndexedPoint>();
        for (int i = points.Count - 1; i >= 0; i--)
        {
            var point = points[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 0.0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        return lower.Concat(upper).Select(point => point.Index).ToList();
    }

    private static double[] BuildOffsetHull(IReadOnlyList<int> hull, double[] xyCoords, double tolerance, double hullDiagonal)
    {
        var points = new (double X, double Y)[hull.Count];
        for (int i = 0; i < hull.Count; i++)
            points[i] = (xyCoords[hull[i] * 2], xyCoords[hull[i] * 2 + 1]);

        double offsetDistance = Math.Max(Math.Max(tolerance * 8.0, hullDiagonal * 0.0025), 1e-6);
        bool isCounterClockwise = ComputeSignedArea(points) > 0.0;
        var result = new double[hull.Count * 2];

        for (int i = 0; i < points.Length; i++)
        {
            var prev = points[(i - 1 + points.Length) % points.Length];
            var current = points[i];
            var next = points[(i + 1) % points.Length];

            var prevEdge = Normalize(current.X - prev.X, current.Y - prev.Y);
            var nextEdge = Normalize(next.X - current.X, next.Y - current.Y);
            if (prevEdge.Length <= 1e-12 || nextEdge.Length <= 1e-12)
            {
                result[i * 2] = current.X;
                result[i * 2 + 1] = current.Y;
                continue;
            }

            var prevNormal = ComputeOutwardNormal(prevEdge.X, prevEdge.Y, isCounterClockwise);
            var nextNormal = ComputeOutwardNormal(nextEdge.X, nextEdge.Y, isCounterClockwise);

            double line1X = current.X + prevNormal.X * offsetDistance;
            double line1Y = current.Y + prevNormal.Y * offsetDistance;
            double line2X = current.X + nextNormal.X * offsetDistance;
            double line2Y = current.Y + nextNormal.Y * offsetDistance;

            if (TryIntersectLines(
                line1X, line1Y, prevEdge.X, prevEdge.Y,
                line2X, line2Y, nextEdge.X, nextEdge.Y,
                out double offsetX,
                out double offsetY))
            {
                double miterDistance = Math.Sqrt(DistanceSquared(offsetX, offsetY, current.X, current.Y));
                if (miterDistance <= offsetDistance * 4.0)
                {
                    result[i * 2] = offsetX;
                    result[i * 2 + 1] = offsetY;
                    continue;
                }
            }

            var averageNormal = Normalize(prevNormal.X + nextNormal.X, prevNormal.Y + nextNormal.Y);
            if (averageNormal.Length <= 1e-12)
                averageNormal = prevNormal;

            result[i * 2] = current.X + averageNormal.X * offsetDistance;
            result[i * 2 + 1] = current.Y + averageNormal.Y * offsetDistance;
        }

        return result;
    }

    private static double ComputeHullDiagonal(IReadOnlyList<int> hull, double[] xyCoords)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        for (int i = 0; i < hull.Count; i++)
        {
            double x = xyCoords[hull[i] * 2];
            double y = xyCoords[hull[i] * 2 + 1];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
    }

    private static double ComputeSignedArea(IReadOnlyList<(double X, double Y)> points)
    {
        double area = 0.0;
        for (int i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }

        return area * 0.5;
    }

    private static (double X, double Y, double Length) Normalize(double x, double y)
    {
        double length = Math.Sqrt(x * x + y * y);
        if (length <= 1e-12)
            return (0.0, 0.0, 0.0);

        return (x / length, y / length, length);
    }

    private static (double X, double Y, double Length) ComputeOutwardNormal(double edgeX, double edgeY, bool isCounterClockwise)
    {
        return isCounterClockwise
            ? Normalize(edgeY, -edgeX)
            : Normalize(-edgeY, edgeX);
    }

    private static bool TryIntersectLines(
        double ax,
        double ay,
        double adx,
        double ady,
        double bx,
        double by,
        double bdx,
        double bdy,
        out double x,
        out double y)
    {
        double cross = (adx * bdy) - (ady * bdx);
        if (Math.Abs(cross) <= 1e-12)
        {
            x = 0.0;
            y = 0.0;
            return false;
        }

        double t = (((bx - ax) * bdy) - ((by - ay) * bdx)) / cross;
        x = ax + adx * t;
        y = ay + ady * t;
        return true;
    }

    private static double Cross(IndexedPoint a, IndexedPoint b, IndexedPoint c)
    {
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }

    private static void RemoveNearCollinearHullVertices(List<int> hull, double[] xyCoords, double tolerance)
    {
        if (hull.Count <= 3)
            return;

        bool removed;
        do
        {
            removed = false;
            for (int i = 0; i < hull.Count && hull.Count > 3; i++)
            {
                int prev = hull[(i - 1 + hull.Count) % hull.Count];
                int current = hull[i];
                int next = hull[(i + 1) % hull.Count];

                double distance = DistanceToSegment(
                    xyCoords[current * 2],
                    xyCoords[current * 2 + 1],
                    xyCoords[prev * 2],
                    xyCoords[prev * 2 + 1],
                    xyCoords[next * 2],
                    xyCoords[next * 2 + 1]);

                if (distance <= tolerance)
                {
                    hull.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }
        while (removed);
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq <= 1e-20)
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

        double t = Math.Max(0.0, Math.Min(1.0, ((px - ax) * dx + (py - ay) * dy) / lenSq));
        double cx = ax + t * dx;
        double cy = ay + t * dy;
        double ddx = px - cx;
        double ddy = py - cy;
        return Math.Sqrt(ddx * ddx + ddy * ddy);
    }

    private static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return dx * dx + dy * dy;
    }

    private static bool IsInsideOrOnBoundary(
        double px,
        double py,
        IReadOnlyList<int> hull,
        double[] xyCoords,
        double tolerance)
    {
        bool inside = false;
        for (int i = 0, j = hull.Count - 1; i < hull.Count; j = i++)
        {
            double xi = xyCoords[hull[i] * 2];
            double yi = xyCoords[hull[i] * 2 + 1];
            double xj = xyCoords[hull[j] * 2];
            double yj = xyCoords[hull[j] * 2 + 1];

            if (DistanceToSegment(px, py, xi, yi, xj, yj) <= tolerance)
                return true;

            bool intersects = ((yi > py) != (yj > py)) &&
                              (px < ((xj - xi) * (py - yi) / ((yj - yi) + 1e-20)) + xi);
            if (intersects)
                inside = !inside;
        }

        return inside;
    }

    private static bool IsInsideOrOnBoundary(
        double px,
        double py,
        double[] hullPoints,
        double tolerance)
    {
        int pointCount = hullPoints.Length / 2;
        bool inside = false;
        for (int i = 0, j = pointCount - 1; i < pointCount; j = i++)
        {
            double xi = hullPoints[i * 2];
            double yi = hullPoints[i * 2 + 1];
            double xj = hullPoints[j * 2];
            double yj = hullPoints[j * 2 + 1];

            if (DistanceToSegment(px, py, xi, yi, xj, yj) <= tolerance)
                return true;

            bool intersects = ((yi > py) != (yj > py)) &&
                              (px < ((xj - xi) * (py - yi) / ((yj - yi) + 1e-20)) + xi);
            if (intersects)
                inside = !inside;
        }

        return inside;
    }

    private static PreparedTinInput WithWarning(PreparedTinInput input, string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            return input;

        return new PreparedTinInput
        {
            XyCoords = input.XyCoords,
            ZValues = input.ZValues,
            Segments = input.Segments,
            UseConvexHull = input.UseConvexHull,
            Mode = input.Mode,
            InfoMessage = input.InfoMessage,
            WarningMessage = string.IsNullOrWhiteSpace(input.WarningMessage)
                ? warning
                : $"{warning} {input.WarningMessage}"
        };
    }

    private static long GetEdgeKey(int a, int b)
    {
        return a < b
            ? ((long)a << 32) | (uint)b
            : ((long)b << 32) | (uint)a;
    }

    private static long PackCellKey(long cellX, long cellY)
    {
        return (cellX * 0x100000001L) ^ (cellY * 0x27d4eb2dL);
    }
}
