namespace MoleHill.Core.Engine;

/// <summary>
/// Adds an explicit outer boundary loop to TIN inputs before triangulation.
/// </summary>
public static class TinBoundaryPreparer
{
    public readonly record struct BoundaryPolyline(double[] Points, int PointCount, bool IsClosed);

    public enum BoundaryMode
    {
        None,
        Explicit
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

    private sealed class VertexReuseLookup
    {
        private readonly List<double> _xyList;
        private readonly double _toleranceSquared;
        private readonly double _inverseCellSize;
        private readonly Dictionary<long, List<int>> _cells = new(IndexedMeshTools.CellKeyComparer.Instance);

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
        var segmentKeys = IndexedMeshTools.CreateEdgeKeySet();

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

        long key = IndexedMeshTools.GetEdgeKey(a, b);
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

    private static long PackCellKey(long cellX, long cellY)
    {
        return (cellX * 0x100000001L) ^ (cellY * 0x27d4eb2dL);
    }
}
