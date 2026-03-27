namespace MoleHill.Core.Engine;

internal static class TriangleBoundaryCuller
{
    internal const double DegenerateBoundaryAngleDegrees = 170.0;

    internal sealed class Result
    {
        public bool Changed { get; }

        public int[] Faces { get; }

        public int FaceCount { get; }

        public int[] NewToOld { get; }

        public int VertexCount { get; }

        public Result(bool changed, int[] faces, int faceCount, int[] newToOld, int vertexCount)
        {
            Changed = changed;
            Faces = faces;
            FaceCount = faceCount;
            NewToOld = newToOld;
            VertexCount = vertexCount;
        }
    }

    public static Result Cull(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] inputXy,
        int[] inputSegments,
        double maxBoundaryEdgeLength)
    {
        if (faceCount <= 0 || maxBoundaryEdgeLength < 0)
            return new Result(false, faces, faceCount, Array.Empty<int>(), vertexCount);

        double effectiveThreshold = maxBoundaryEdgeLength > 0
            ? maxBoundaryEdgeLength
            : ComputeAutoThreshold(vertices, faces, faceCount);

        if (!(effectiveThreshold > 0) || double.IsNaN(effectiveThreshold) || double.IsInfinity(effectiveThreshold))
            return new Result(false, faces, faceCount, Array.Empty<int>(), vertexCount);

        var spatialIndex = ConstraintSpatialIndex.Build(inputXy, inputSegments);
        var active = new bool[faceCount];
        Array.Fill(active, true);

        bool changed = false;
        int activeFaceCount = faceCount;

        while (activeFaceCount > 0)
        {
            var edgeCounts = BuildActiveEdgeCounts(faces, faceCount, active);
            var toRemove = new List<int>();

            for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
            {
                if (!active[faceIndex])
                    continue;

                int i0 = faces[faceIndex * 3];
                int i1 = faces[faceIndex * 3 + 1];
                int i2 = faces[faceIndex * 3 + 2];

                bool hasBoundaryEdge =
                    edgeCounts.GetValueOrDefault(IndexedMeshTools.GetEdgeKey(i0, i1), 0) == 1 ||
                    edgeCounts.GetValueOrDefault(IndexedMeshTools.GetEdgeKey(i1, i2), 0) == 1 ||
                    edgeCounts.GetValueOrDefault(IndexedMeshTools.GetEdgeKey(i2, i0), 0) == 1;

                if (!hasBoundaryEdge)
                    continue;

                if (TriangleCrossesConstraint(vertices, i0, i1, i2, spatialIndex) ||
                    IsDegenerateBoundaryTriangle(vertices, i0, i1, i2, effectiveThreshold))
                {
                    toRemove.Add(faceIndex);
                }
            }

            if (toRemove.Count == 0 || toRemove.Count == activeFaceCount)
                break;

            foreach (int faceIndex in toRemove)
            {
                active[faceIndex] = false;
            }

            activeFaceCount -= toRemove.Count;
            changed = true;
        }

        if (!changed)
            return new Result(false, faces, faceCount, Array.Empty<int>(), vertexCount);

        var filteredFaces = new int[activeFaceCount * 3];
        int nextFace = 0;
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if (!active[faceIndex])
                continue;

            Array.Copy(faces, faceIndex * 3, filteredFaces, nextFace * 3, 3);
            nextFace++;
        }

        var compact = IndexedMeshTools.Compact(vertexCount, filteredFaces, activeFaceCount);
        return new Result(true, compact.Faces, compact.FaceCount, compact.NewToOld, compact.VertexCount);
    }

    private static double ComputeAutoThreshold(double[] vertices, int[] faces, int faceCount)
    {
        var topology = IndexedMeshTools.BuildEdgeTopology(faces, faceCount);
        if (topology.EdgeCount == 0)
            return 0;

        var lengths = new double[topology.EdgeCount];
        for (int i = 0; i < topology.EdgeCount; i++)
        {
            int a = topology.Edges[i * 2];
            int b = topology.Edges[i * 2 + 1];
            lengths[i] = Distance2D(vertices, a, b);
        }

        Array.Sort(lengths);
        double median = lengths[lengths.Length / 2];
        return median * 4.0;
    }

    private static Dictionary<long, int> BuildActiveEdgeCounts(int[] faces, int faceCount, bool[] active)
    {
        var edgeCounts = new Dictionary<long, int>(Math.Max(faceCount * 2, 8));
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if (!active[faceIndex])
                continue;

            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];

            CountEdge(edgeCounts, a, b);
            CountEdge(edgeCounts, b, c);
            CountEdge(edgeCounts, c, a);
        }

        return edgeCounts;
    }

    private static void CountEdge(Dictionary<long, int> edgeCounts, int a, int b)
    {
        long edgeKey = IndexedMeshTools.GetEdgeKey(a, b);
        edgeCounts[edgeKey] = edgeCounts.GetValueOrDefault(edgeKey, 0) + 1;
    }

    private static bool TriangleCrossesConstraint(double[] vertices, int i0, int i1, int i2, ConstraintSpatialIndex spatialIndex)
    {
        if (spatialIndex.Count == 0)
            return false;

        return EdgeCrossesConstraint(vertices, i0, i1, spatialIndex) ||
               EdgeCrossesConstraint(vertices, i1, i2, spatialIndex) ||
               EdgeCrossesConstraint(vertices, i2, i0, spatialIndex);
    }

    private static bool EdgeCrossesConstraint(double[] vertices, int a, int b, ConstraintSpatialIndex spatialIndex)
    {
        double ax = vertices[a * 3];
        double ay = vertices[a * 3 + 1];
        double bx = vertices[b * 3];
        double by = vertices[b * 3 + 1];

        foreach (int segIndex in spatialIndex.Query(ax, ay, bx, by))
        {
            spatialIndex.GetSegment(segIndex, out double sx0, out double sy0, out double sx1, out double sy1);
            if (ProperSegmentsIntersect(ax, ay, bx, by, sx0, sy0, sx1, sy1))
                return true;
        }

        return false;
    }

    private static bool IsDegenerateBoundaryTriangle(double[] vertices, int i0, int i1, int i2, double maxEdgeThreshold)
    {
        double len01 = Distance2D(vertices, i0, i1);
        double len12 = Distance2D(vertices, i1, i2);
        double len20 = Distance2D(vertices, i2, i0);
        double longest = Math.Max(len01, Math.Max(len12, len20));

        if (longest <= maxEdgeThreshold)
            return false;

        return ComputeMaxAngleDegrees(len01, len12, len20) >= DegenerateBoundaryAngleDegrees;
    }

    private static double ComputeMaxAngleDegrees(double len01, double len12, double len20)
    {
        double angle0 = AngleDegrees(len20, len01, len12);
        double angle1 = AngleDegrees(len01, len12, len20);
        double angle2 = AngleDegrees(len12, len20, len01);
        return Math.Max(angle0, Math.Max(angle1, angle2));
    }

    private static double AngleDegrees(double adjA, double adjB, double opposite)
    {
        if (adjA <= 1e-12 || adjB <= 1e-12)
            return 180.0;

        double cos = (adjA * adjA + adjB * adjB - opposite * opposite) / (2.0 * adjA * adjB);
        cos = Math.Clamp(cos, -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    private static double Distance2D(double[] vertices, int a, int b)
    {
        double dx = vertices[a * 3] - vertices[b * 3];
        double dy = vertices[a * 3 + 1] - vertices[b * 3 + 1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool ProperSegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        const double eps = 1e-9;

        double abC = Orient(ax, ay, bx, by, cx, cy);
        double abD = Orient(ax, ay, bx, by, dx, dy);
        if (Math.Abs(abC) <= eps || Math.Abs(abD) <= eps)
            return false;

        if ((abC > 0) == (abD > 0))
            return false;

        double cdA = Orient(cx, cy, dx, dy, ax, ay);
        double cdB = Orient(cx, cy, dx, dy, bx, by);
        if (Math.Abs(cdA) <= eps || Math.Abs(cdB) <= eps)
            return false;

        return (cdA > 0) != (cdB > 0);
    }

    private static double Orient(double ax, double ay, double bx, double by, double cx, double cy)
    {
        return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    }

    private sealed class ConstraintSpatialIndex
    {
        private readonly Bounds2D[] _bounds;
        private readonly bool[] _valid;
        private readonly SpatialHashGrid2D _grid;
        private readonly SpatialHashGrid2D.QueryScratch _scratch;
        private readonly List<int> _queryCandidates = new(16);
        private readonly List<int> _candidates = new(16);
        private readonly double[] _xy;
        private readonly int[] _segments;

        public int Count { get; }

        private ConstraintSpatialIndex(
            double[] xy,
            int[] segments,
            Bounds2D[] bounds,
            bool[] valid,
            SpatialHashGrid2D grid,
            int count)
        {
            _xy = xy;
            _segments = segments;
            _bounds = bounds;
            _valid = valid;
            _grid = grid;
            _scratch = new SpatialHashGrid2D.QueryScratch(bounds.Length);
            Count = count;
        }

        public static ConstraintSpatialIndex Build(double[] xy, int[] segments)
        {
            int vertexCount = xy.Length / 2;
            int segmentCount = segments.Length / 2;
            var bounds = new Bounds2D[segmentCount];
            var valid = new bool[segmentCount];

            if (segmentCount == 0 || vertexCount == 0)
            {
                return new ConstraintSpatialIndex(
                    xy,
                    segments,
                    bounds,
                    valid,
                    SpatialHashGrid2D.Build(bounds, valid),
                    0);
            }

            int validCount = 0;

            for (int i = 0; i < segmentCount; i++)
            {
                int a = segments[i * 2];
                int b = segments[i * 2 + 1];
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || a == b)
                    continue;

                double ax = xy[a * 2];
                double ay = xy[a * 2 + 1];
                double bx = xy[b * 2];
                double by = xy[b * 2 + 1];
                if ((Math.Abs(ax - bx) + Math.Abs(ay - by)) <= 1e-12)
                    continue;

                bounds[i] = new Bounds2D(
                    Math.Min(ax, bx),
                    Math.Max(ax, bx),
                    Math.Min(ay, by),
                    Math.Max(ay, by));
                valid[i] = true;
                validCount++;
            }

            if (validCount == 0)
            {
                return new ConstraintSpatialIndex(
                    xy,
                    segments,
                    bounds,
                    valid,
                    SpatialHashGrid2D.Build(bounds, valid),
                    0);
            }

            return new ConstraintSpatialIndex(
                xy,
                segments,
                bounds,
                valid,
                SpatialHashGrid2D.Build(bounds, valid),
                validCount);
        }

        public IReadOnlyList<int> Query(double ax, double ay, double bx, double by)
        {
            _candidates.Clear();
            if (Count == 0)
                return _candidates;

            Bounds2D queryBounds = new(
                Math.Min(ax, bx),
                Math.Max(ax, bx),
                Math.Min(ay, by),
                Math.Max(ay, by));
            _grid.GatherCandidates(queryBounds, _queryCandidates, _scratch);
            foreach (int segIndex in _queryCandidates)
            {
                if (!_valid[segIndex] || !_bounds[segIndex].Intersects(queryBounds))
                    continue;

                _candidates.Add(segIndex);
            }

            return _candidates;
        }

        public void GetSegment(int segIndex, out double ax, out double ay, out double bx, out double by)
        {
            int a = _segments[segIndex * 2];
            int b = _segments[segIndex * 2 + 1];
            ax = _xy[a * 2];
            ay = _xy[a * 2 + 1];
            bx = _xy[b * 2];
            by = _xy[b * 2 + 1];
        }
    }
}
