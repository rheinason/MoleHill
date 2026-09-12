namespace MoleHill.Core.Analysis;

/// <summary>
/// Single-pass marching-triangles contour generator. Instead of intersecting the whole mesh with a
/// plane once per level (O(levels × faces) — N full mesh scans), it makes ONE pass over the faces:
/// each triangle emits a crossing segment only for the levels inside its own value span (usually a
/// handful),
/// then segments are stitched into polylines per level. Total work ≈ faces × avg-levels-per-triangle +
/// stitching, which is dramatically faster than the per-level plane intersection for dense terrain with
/// many levels. Pure geometry (no Rhino), so it is unit-tested.
/// </summary>
public static class ContourGenerator
{
    /// <summary>Contours the mesh by elevation — the ordinary case, where the field being contoured is Z.</summary>
    public static List<ContourLevel> Generate(
        double[] verticesXyz,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<double> levels,
        double tolerance) =>
        Generate(verticesXyz, vertexCount, faces, faceCount, field: null, levels, tolerance);

    /// <summary>
    /// Contours an arbitrary per-vertex scalar <paramref name="field"/> over the mesh, emitting the
    /// crossings as points ON the mesh. Null contours elevation, so the two callers share one marching
    /// pass rather than one each.
    ///
    /// This is what draws a cut/fill delta: the field is <c>newZ − refZ</c>, so a "level" is a depth and
    /// level 0 is the balance line. A vertex whose field value is not finite marks ground the field does
    /// not describe — outside the comparison boundary, or over a hole in the reference — and every face
    /// touching one is skipped, so unmapped ground draws nothing rather than drawing a line derived from
    /// a value that does not exist.
    /// </summary>
    public static List<ContourLevel> Generate(
        double[] verticesXyz,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[]? field,
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

        var pts = new double[6];
        for (int f = 0; f < faceCount; f++)
        {
            int ia = faces[f * 3], ib = faces[f * 3 + 1], ic = faces[f * 3 + 2];
            double ax = verticesXyz[ia * 3], ay = verticesXyz[ia * 3 + 1], az = verticesXyz[ia * 3 + 2];
            double bx = verticesXyz[ib * 3], by = verticesXyz[ib * 3 + 1], bz = verticesXyz[ib * 3 + 2];
            double cx = verticesXyz[ic * 3], cy = verticesXyz[ic * 3 + 1], cz = verticesXyz[ic * 3 + 2];

            // The contoured value and the emitted point are separate: the crossing parameter comes from
            // the field, the coordinates from the mesh. For elevation contours they are the same number.
            double av = field == null ? az : field[ia];
            double bv = field == null ? bz : field[ib];
            double cv = field == null ? cz : field[ic];
            if (field != null && !(double.IsFinite(av) && double.IsFinite(bv) && double.IsFinite(cv)))
                continue;

            double vmin = Math.Min(av, Math.Min(bv, cv));
            double vmax = Math.Max(av, Math.Max(bv, cv));
            if (vmax - vmin <= 0.0)
                continue;

            // First level strictly above vmin. (A level == vmin can never emit: no vertex is below the
            // minimum, so AddCrossing finds no below->above transition.) The upper bound is INCLUSIVE of
            // vmax: a face with an edge exactly at the level — e.g. a batter triangle whose two rim
            // vertices sit at a round pad elevation and whose toe is below — has vmax == level and must
            // still emit that rim edge. Excluding it (the old strict `< vmax`) silently dropped the
            // pad-outline contour whenever users contoured at the pad's exact design elevation.
            int start = UpperBound(sortedLevels, vmin);
            for (int li = start; li < levelCount && sortedLevels[li] <= vmax; li++)
            {
                double level = sortedLevels[li];
                // A level between vmin and vmax (inclusive of vmax) crosses exactly two of the three
                // edges. At-level vertices count as "above" (AddCrossing uses pz < level), so a face with
                // two vertices exactly at the level and the third below emits the segment along that edge
                // via crossings landing on the two at-level vertices (t = 0 / t = 1).
                int found = 0;
                AddCrossing(ax, ay, az, av, bx, by, bz, bv, level, pts, ref found);
                AddCrossing(bx, by, bz, bv, cx, cy, cz, cv, level, pts, ref found);
                if (found < 2)
                    AddCrossing(cx, cy, cz, cv, ax, ay, az, av, level, pts, ref found);

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
        double px, double py, double pz, double pv,
        double qx, double qy, double qz, double qv,
        double level, Span<double> pts, ref int found)
    {
        if (found >= 2)
            return;

        bool pBelow = pv < level;
        bool qBelow = qv < level;
        if (pBelow == qBelow)
            return;

        double t = (level - pv) / (qv - pv);
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

        int realCount = 0;
        for (int s = 0; s < segmentCount; s++)
        {
            int a = AddNode(segmentCoords[s * 6], segmentCoords[s * 6 + 1], segmentCoords[s * 6 + 2]);
            int b = AddNode(segmentCoords[s * 6 + 3], segmentCoords[s * 6 + 4], segmentCoords[s * 6 + 5]);
            if (a == b)
                continue;

            segA[realCount] = a;
            segB[realCount] = b;
            realCount++;
        }

        // Node -> incident segments as flat CSR. A Dictionary<int, List<int>> allocated a list object
        // plus its backing array for every welded node, and a segment-heavy contour job has roughly as
        // many nodes as segments. Filling in segment order keeps each node's run in the insertion order
        // the lists had, which is the order NextUnused picks a continuation in.
        int nodeCount = nodes.Count;
        var nodeStart = new int[nodeCount + 1];
        for (int s = 0; s < realCount; s++)
        {
            nodeStart[segA[s] + 1]++;
            nodeStart[segB[s] + 1]++;
        }

        for (int node = 1; node <= nodeCount; node++)
            nodeStart[node] += nodeStart[node - 1];

        var nodeSegments = new int[realCount * 2];
        var cursor = new int[nodeCount];
        Array.Copy(nodeStart, cursor, nodeCount);
        for (int s = 0; s < realCount; s++)
        {
            nodeSegments[cursor[segA[s]]++] = s;
            nodeSegments[cursor[segB[s]]++] = s;
        }

        var used = new bool[realCount];
        int Other(int segment, int node) => segA[segment] == node ? segB[segment] : segA[segment];

        int Degree(int node) => nodeStart[node + 1] - nodeStart[node];

        int NextUnused(int node)
        {
            for (int slot = nodeStart[node]; slot < nodeStart[node + 1]; slot++)
            {
                int segment = nodeSegments[slot];
                if (!used[segment])
                    return segment;
            }

            return -1;
        }

        var polylines = new List<ContourPolyline>();

        // Chain scratch, reused across seeds. `forward` grows from the seed's second node, `backward`
        // from its first; the emitted order is backward reversed, then forward - exactly the order the
        // former LinkedList's AddFirst/AddLast produced, without a node object per point.
        var forward = new List<int>();
        var backward = new List<int>();

        void Extend(List<int> into, int fromNode)
        {
            int current = fromNode;
            while (true)
            {
                int segment = NextUnused(current);
                if (segment < 0)
                    break;

                used[segment] = true;
                int other = Other(segment, current);
                into.Add(other);
                current = other;
            }
        }

        // Prefer starting at open endpoints (degree 1) so open chains aren't split mid-way. Sorting a
        // (key, index) pair keeps the stable order the LINQ OrderBy gave, without its allocations.
        var seedOrder = new int[realCount];
        var seedKeys = new int[realCount];
        for (int s = 0; s < realCount; s++)
        {
            seedOrder[s] = s;
            seedKeys[s] = Math.Min(Degree(segA[s]), Degree(segB[s]));
        }

        Array.Sort(seedOrder, (left, right) =>
        {
            int compared = seedKeys[left].CompareTo(seedKeys[right]);
            return compared != 0 ? compared : left.CompareTo(right);
        });

        foreach (int seed in seedOrder)
        {
            if (used[seed])
                continue;

            used[seed] = true;
            forward.Clear();
            backward.Clear();
            forward.Add(segA[seed]);
            forward.Add(segB[seed]);
            Extend(forward, segB[seed]);
            Extend(backward, segA[seed]);

            int orderLength = backward.Count + forward.Count;
            int First() => backward.Count > 0 ? backward[^1] : forward[0];
            int Last() => forward[^1];

            bool closed = orderLength > 2 && First() == Last();
            int emitCount = closed ? orderLength - 1 : orderLength;
            var pointsXyz = new double[emitCount * 3];
            for (int i = 0; i < emitCount; i++)
            {
                int node = i < backward.Count ? backward[backward.Count - 1 - i] : forward[i - backward.Count];
                (double X, double Y, double Z) point = nodes[node];
                pointsXyz[i * 3] = point.X;
                pointsXyz[i * 3 + 1] = point.Y;
                pointsXyz[i * 3 + 2] = point.Z;
            }

            polylines.Add(new ContourPolyline { PointsXyz = pointsXyz, IsClosed = closed });
        }

        return polylines;
    }
}
