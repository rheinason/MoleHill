namespace MoleHill.Core.Analysis;

/// <summary>
/// Single-pass marching-triangles contour generator. Instead of intersecting the whole mesh with a
/// plane once per level (O(levels × faces) — N full mesh scans), it makes ONE pass over the faces:
/// each triangle emits a crossing segment only for the levels inside its own Z-span (usually a handful),
/// then segments are stitched into polylines per level. Total work ≈ faces × avg-levels-per-triangle +
/// stitching, which is dramatically faster than the per-level plane intersection for dense terrain with
/// many levels. Pure geometry (no Rhino), so it is unit-tested.
/// </summary>
public static class ContourGenerator
{
    public static List<ContourLevel> Generate(
        double[] verticesXyz,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<double> levels,
        double tolerance)
    {
        var result = new List<ContourLevel>();
        if (levels == null || levels.Count == 0 || faceCount == 0 || vertexCount < 3)
            return result;

        // Sorted ascending levels enable a per-triangle range scan.
        var sortedLevels = levels.Distinct().OrderBy(value => value).ToArray();
        int levelCount = sortedLevels.Length;

        // Per-level segment buckets: flat [ax,ay,az, bx,by,bz, …].
        var segments = new List<double>[levelCount];
        for (int i = 0; i < levelCount; i++)
            segments[i] = new List<double>();

        for (int f = 0; f < faceCount; f++)
        {
            int ia = faces[f * 3], ib = faces[f * 3 + 1], ic = faces[f * 3 + 2];
            double ax = verticesXyz[ia * 3], ay = verticesXyz[ia * 3 + 1], az = verticesXyz[ia * 3 + 2];
            double bx = verticesXyz[ib * 3], by = verticesXyz[ib * 3 + 1], bz = verticesXyz[ib * 3 + 2];
            double cx = verticesXyz[ic * 3], cy = verticesXyz[ic * 3 + 1], cz = verticesXyz[ic * 3 + 2];

            double zmin = Math.Min(az, Math.Min(bz, cz));
            double zmax = Math.Max(az, Math.Max(bz, cz));
            if (zmax - zmin <= 0.0)
                continue;

            // First level strictly above zmin.
            int start = UpperBound(sortedLevels, zmin);
            for (int li = start; li < levelCount && sortedLevels[li] < zmax; li++)
            {
                double level = sortedLevels[li];
                // A plane strictly between zmin and zmax crosses exactly two of the three edges.
                Span<double> pts = stackalloc double[6];
                int found = 0;
                AddCrossing(ax, ay, az, bx, by, bz, level, pts, ref found);
                AddCrossing(bx, by, bz, cx, cy, cz, level, pts, ref found);
                if (found < 2)
                    AddCrossing(cx, cy, cz, ax, ay, az, level, pts, ref found);

                if (found != 2)
                    continue;

                var bucket = segments[li];
                bucket.Add(pts[0]); bucket.Add(pts[1]); bucket.Add(pts[2]);
                bucket.Add(pts[3]); bucket.Add(pts[4]); bucket.Add(pts[5]);
            }
        }

        double weld = Math.Max(tolerance, 1e-9);
        for (int li = 0; li < levelCount; li++)
        {
            if (segments[li].Count == 0)
                continue;

            var polylines = StitchSegments(segments[li], weld);
            if (polylines.Count > 0)
                result.Add(new ContourLevel { Z = sortedLevels[li], Polylines = polylines });
        }

        return result;
    }

    private static void AddCrossing(
        double px, double py, double pz,
        double qx, double qy, double qz,
        double level, Span<double> pts, ref int found)
    {
        if (found >= 2)
            return;

        bool pBelow = pz < level;
        bool qBelow = qz < level;
        if (pBelow == qBelow)
            return;

        double t = (level - pz) / (qz - pz);
        pts[found * 3] = px + (qx - px) * t;
        pts[found * 3 + 1] = py + (qy - py) * t;
        pts[found * 3 + 2] = pz + (qz - pz) * t;
        found++;
    }

    // First index whose value is strictly greater than <paramref name="value"/>.
    private static int UpperBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] > value)
                hi = mid;
            else
                lo = mid + 1;
        }

        return lo;
    }

    private static List<ContourPolyline> StitchSegments(List<double> segmentCoords, double weld)
    {
        // Merge endpoints onto welded nodes, then walk the segment soup into chains.
        double inverse = 1.0 / weld;
        var nodeIndex = new Dictionary<(long, long, long), int>();
        var nodes = new List<(double X, double Y, double Z)>();

        int AddNode(double x, double y, double z)
        {
            var key = ((long)Math.Round(x * inverse), (long)Math.Round(y * inverse), (long)Math.Round(z * inverse));
            if (nodeIndex.TryGetValue(key, out int existing))
                return existing;

            int index = nodes.Count;
            nodes.Add((x, y, z));
            nodeIndex[key] = index;
            return index;
        }

        int segmentCount = segmentCoords.Count / 6;
        var segA = new int[segmentCount];
        var segB = new int[segmentCount];
        var nodeSegments = new Dictionary<int, List<int>>();

        void Link(int node, int segment)
        {
            if (!nodeSegments.TryGetValue(node, out var list))
            {
                list = new List<int>(2);
                nodeSegments[node] = list;
            }

            list.Add(segment);
        }

        int realCount = 0;
        for (int s = 0; s < segmentCount; s++)
        {
            int a = AddNode(segmentCoords[s * 6], segmentCoords[s * 6 + 1], segmentCoords[s * 6 + 2]);
            int b = AddNode(segmentCoords[s * 6 + 3], segmentCoords[s * 6 + 4], segmentCoords[s * 6 + 5]);
            if (a == b)
                continue;

            segA[realCount] = a;
            segB[realCount] = b;
            Link(a, realCount);
            Link(b, realCount);
            realCount++;
        }

        var used = new bool[realCount];
        int Other(int segment, int node) => segA[segment] == node ? segB[segment] : segA[segment];

        int NextUnused(int node)
        {
            if (!nodeSegments.TryGetValue(node, out var list))
                return -1;

            foreach (int segment in list)
                if (!used[segment])
                    return segment;

            return -1;
        }

        var polylines = new List<ContourPolyline>();

        void Extend(LinkedList<int> chain, int fromNode, bool prepend)
        {
            int current = fromNode;
            while (true)
            {
                int segment = NextUnused(current);
                if (segment < 0)
                    break;

                used[segment] = true;
                int other = Other(segment, current);
                if (prepend)
                    chain.AddFirst(other);
                else
                    chain.AddLast(other);
                current = other;
            }
        }

        // Prefer starting at open endpoints (degree 1) so open chains aren't split mid-way.
        var seeds = Enumerable.Range(0, realCount)
            .OrderBy(s => Math.Min(Degree(nodeSegments, segA[s]), Degree(nodeSegments, segB[s])));

        foreach (int seed in seeds)
        {
            if (used[seed])
                continue;

            used[seed] = true;
            var chain = new LinkedList<int>();
            chain.AddLast(segA[seed]);
            chain.AddLast(segB[seed]);
            Extend(chain, segB[seed], prepend: false);
            Extend(chain, segA[seed], prepend: true);

            var order = chain.ToArray();
            bool closed = order.Length > 2 && order[0] == order[^1];
            int emitCount = closed ? order.Length - 1 : order.Length;
            var pointsXyz = new double[emitCount * 3];
            for (int i = 0; i < emitCount; i++)
            {
                var node = nodes[order[i]];
                pointsXyz[i * 3] = node.X;
                pointsXyz[i * 3 + 1] = node.Y;
                pointsXyz[i * 3 + 2] = node.Z;
            }

            polylines.Add(new ContourPolyline { PointsXyz = pointsXyz, IsClosed = closed });
        }

        return polylines;
    }

    private static int Degree(Dictionary<int, List<int>> nodeSegments, int node) =>
        nodeSegments.TryGetValue(node, out var list) ? list.Count : 0;
}
