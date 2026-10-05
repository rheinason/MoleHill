using MoleHill.Core.Engine;

namespace MoleHill.Core.Processing;

/// <summary>
/// Conservative topology cleanup for TIN inputs. Intended as a fallback path
/// when the exact triangulation input fails or produces an invalid mesh.
/// </summary>
public static class TinInputCleaner
{
    private readonly struct VertexData
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;
        public readonly PointCloudProcessor.VertexSource Source;

        public VertexData(double x, double y, double z, PointCloudProcessor.VertexSource source)
        {
            X = x;
            Y = y;
            Z = z;
            Source = source;
        }
    }

    private readonly struct SegmentData
    {
        public readonly int A;
        public readonly int B;

        public SegmentData(int a, int b)
        {
            A = a;
            B = b;
        }
    }

    private readonly record struct SegmentCut(double T, int VertexIndex);

    public readonly struct CleanupResult
    {
        public readonly double[] XyCoords;
        public readonly double[] ZValues;
        public readonly int VertexCount;
        public readonly PointCloudProcessor.VertexSource[] Sources;
        public readonly int[] Segments;
        public readonly int SegmentCount;
        public readonly int DegenerateSegmentsRemoved;
        public readonly int DuplicateSegmentsRemoved;
        public readonly int CollinearVerticesCollapsed;
        public readonly int TinySpikesCollapsed;
        public readonly int IntersectionsSplit;
        public readonly int IntersectionConflictsDetected;

        public CleanupResult(
            double[] xyCoords,
            double[] zValues,
            int vertexCount,
            PointCloudProcessor.VertexSource[] sources,
            int[] segments,
            int segmentCount,
            int degenerateSegmentsRemoved,
            int duplicateSegmentsRemoved,
            int collinearVerticesCollapsed,
            int tinySpikesCollapsed,
            int intersectionsSplit,
            int intersectionConflictsDetected)
        {
            XyCoords = xyCoords;
            ZValues = zValues;
            VertexCount = vertexCount;
            Sources = sources;
            Segments = segments;
            SegmentCount = segmentCount;
            DegenerateSegmentsRemoved = degenerateSegmentsRemoved;
            DuplicateSegmentsRemoved = duplicateSegmentsRemoved;
            CollinearVerticesCollapsed = collinearVerticesCollapsed;
            TinySpikesCollapsed = tinySpikesCollapsed;
            IntersectionsSplit = intersectionsSplit;
            IntersectionConflictsDetected = intersectionConflictsDetected;
        }

        public bool HasChanges =>
            DegenerateSegmentsRemoved > 0 ||
            DuplicateSegmentsRemoved > 0 ||
            CollinearVerticesCollapsed > 0 ||
            TinySpikesCollapsed > 0 ||
            IntersectionsSplit > 0;

        public string ToDiagnosticSummary()
        {
            var parts = new List<string>();
            if (DegenerateSegmentsRemoved > 0)
                parts.Add($"{DegenerateSegmentsRemoved} degenerate segments removed");
            if (DuplicateSegmentsRemoved > 0)
                parts.Add($"{DuplicateSegmentsRemoved} duplicate segments removed");
            if (CollinearVerticesCollapsed > 0)
                parts.Add($"{CollinearVerticesCollapsed} collinear vertices collapsed");
            if (TinySpikesCollapsed > 0)
                parts.Add($"{TinySpikesCollapsed} tiny spikes collapsed");
            if (IntersectionsSplit > 0)
                parts.Add($"{IntersectionsSplit} intersections split");
            if (IntersectionConflictsDetected > 0)
                parts.Add($"{IntersectionConflictsDetected} conflicting intersections left unchanged");

            return parts.Count == 0
                ? "no safe changes found"
                : string.Join(", ", parts);
        }
    }

    public static CleanupResult Clean(PointCloudProcessor.MergedData input, double tolerance)
    {
        double xyTol = Math.Max(tolerance, 1e-9);
        double zTol = Math.Max(tolerance, 1e-6);

        var vertices = new List<VertexData>(input.VertexCount);
        for (int i = 0; i < input.VertexCount; i++)
        {
            vertices.Add(new VertexData(
                input.XyCoords[i * 2],
                input.XyCoords[i * 2 + 1],
                input.ZValues[i],
                input.Sources[i]));
        }

        var segments = new List<SegmentData>(input.SegmentCount);
        var segmentKeys = IndexedMeshTools.CreateEdgeKeySet(input.SegmentCount);
        int degenerateSegmentsRemoved = 0;
        int duplicateSegmentsRemoved = 0;
        int collinearVerticesCollapsed = 0;
        int tinySpikesCollapsed = 0;
        int intersectionsSplit = 0;
        int intersectionConflictsDetected = 0;

        for (int i = 0; i < input.SegmentCount; i++)
        {
            int a = input.Segments[i * 2];
            int b = input.Segments[i * 2 + 1];
            if (a < 0 || b < 0 || a >= input.VertexCount || b >= input.VertexCount || a == b)
            {
                degenerateSegmentsRemoved++;
                continue;
            }

            if (!TryAddSegment(segments, segmentKeys, a, b))
                duplicateSegmentsRemoved++;
        }

        SimplifyChains(
            vertices,
            segments,
            xyTol,
            zTol,
            ref collinearVerticesCollapsed,
            ref tinySpikesCollapsed);

        SplitIntersections(
            vertices,
            segments,
            xyTol,
            zTol,
            ref intersectionsSplit,
            ref intersectionConflictsDetected);

        return BuildResult(
            vertices,
            segments,
            degenerateSegmentsRemoved,
            duplicateSegmentsRemoved,
            collinearVerticesCollapsed,
            tinySpikesCollapsed,
            intersectionsSplit,
            intersectionConflictsDetected);
    }

    private static CleanupResult BuildResult(
        List<VertexData> vertices,
        List<SegmentData> segments,
        int degenerateSegmentsRemoved,
        int duplicateSegmentsRemoved,
        int collinearVerticesCollapsed,
        int tinySpikesCollapsed,
        int intersectionsSplit,
        int intersectionConflictsDetected)
    {
        var keepVertex = new bool[vertices.Count];
        foreach (var segment in segments)
        {
            keepVertex[segment.A] = true;
            keepVertex[segment.B] = true;
        }

        for (int i = 0; i < vertices.Count; i++)
        {
            if ((vertices[i].Source & PointCloudProcessor.VertexSource.Spot) != 0)
                keepVertex[i] = true;
        }

        int keptCount = keepVertex.Count(keep => keep);
        var remap = new int[vertices.Count];
        Array.Fill(remap, -1);

        var xyCoords = new double[keptCount * 2];
        var zValues = new double[keptCount];
        var sources = new PointCloudProcessor.VertexSource[keptCount];
        int nextIndex = 0;
        for (int i = 0; i < vertices.Count; i++)
        {
            if (!keepVertex[i])
                continue;

            remap[i] = nextIndex;
            xyCoords[nextIndex * 2] = vertices[i].X;
            xyCoords[nextIndex * 2 + 1] = vertices[i].Y;
            zValues[nextIndex] = vertices[i].Z;
            sources[nextIndex] = vertices[i].Source;
            nextIndex++;
        }

        var segmentArray = new int[segments.Count * 2];
        for (int i = 0; i < segments.Count; i++)
        {
            segmentArray[i * 2] = remap[segments[i].A];
            segmentArray[i * 2 + 1] = remap[segments[i].B];
        }

        return new CleanupResult(
            xyCoords,
            zValues,
            keptCount,
            sources,
            segmentArray,
            segments.Count,
            degenerateSegmentsRemoved,
            duplicateSegmentsRemoved,
            collinearVerticesCollapsed,
            tinySpikesCollapsed,
            intersectionsSplit,
            intersectionConflictsDetected);
    }

    private static void SimplifyChains(
        List<VertexData> vertices,
        List<SegmentData> segments,
        double xyTol,
        double zTol,
        ref int collinearVerticesCollapsed,
        ref int tinySpikesCollapsed)
    {
        if (segments.Count == 0)
            return;

        // Collapses degree-2 chain vertices one at a time, always the lowest-index vertex that qualifies,
        // replacing its two segments with one (a, b) appended after the survivors. That order defines
        // the result (and so the triangulation), and is kept exactly. It used to be reached by
        // rebuilding the adjacency and the segment list and rescanning from vertex 0 after every single
        // collapse: on a terrain with 7,262 collinear vertices that was ~6 s of a 7.7 s Triangulate.
        // A collapse only changes the neighbourhoods of a and b, so a min-queue of vertices that might
        // qualify — every vertex once, then a and b after each collapse — visits the same sequence.
        var alive = new List<bool>(segments.Count);
        var incident = new List<int>[vertices.Count];
        var keys = IndexedMeshTools.CreateEdgeKeySet(segments.Count);
        for (int s = 0; s < segments.Count; s++)
        {
            alive.Add(true);
            keys.Add(SegmentKey(segments[s].A, segments[s].B));
            AddIncident(incident, segments[s].A, s);
            AddIncident(incident, segments[s].B, s);
        }

        var queue = new PriorityQueue<int, int>(vertices.Count);
        var queued = new bool[vertices.Count];
        for (int v = 0; v < vertices.Count; v++)
        {
            if (incident[v] != null)
            {
                queue.Enqueue(v, v);
                queued[v] = true;
            }
        }

        while (queue.TryDequeue(out int vertexIndex, out _))
        {
            queued[vertexIndex] = false;
            if (!TryGetTwoNeighbors(segments, alive, incident[vertexIndex], vertexIndex, out int a, out int b))
                continue;

            bool spike = ShouldCollapseSpike(vertices, a, vertexIndex, b, xyTol, zTol);
            if (!spike && !ShouldCollapseCollinear(vertices, a, vertexIndex, b, xyTol, zTol))
                continue;

            foreach (int s in incident[vertexIndex])
            {
                if (!alive[s])
                    continue;

                alive[s] = false;
                keys.Remove(SegmentKey(segments[s].A, segments[s].B));
            }

            if (keys.Add(SegmentKey(a, b)))
            {
                int added = segments.Count;
                segments.Add(new SegmentData(a, b));
                alive.Add(true);
                AddIncident(incident, a, added);
                AddIncident(incident, b, added);
            }

            if (spike)
                tinySpikesCollapsed++;
            else
                collinearVerticesCollapsed++;

            foreach (int neighbor in new[] { a, b })
            {
                if (!queued[neighbor])
                {
                    queue.Enqueue(neighbor, neighbor);
                    queued[neighbor] = true;
                }
            }
        }

        int write = 0;
        for (int s = 0; s < segments.Count; s++)
        {
            if (alive[s])
                segments[write++] = segments[s];
        }

        segments.RemoveRange(write, segments.Count - write);
    }

    private static void AddIncident(List<int>[] incident, int vertex, int segment) =>
        (incident[vertex] ??= new List<int>(2)).Add(segment);

    /// <summary>
    /// The vertex's distinct neighbours in segment order, when there are exactly two. Segment ids only
    /// grow (a collapse appends), so incident lists stay in the order the segment list has them.
    /// </summary>
    private static bool TryGetTwoNeighbors(
        List<SegmentData> segments,
        List<bool> alive,
        List<int>? incident,
        int vertex,
        out int a,
        out int b)
    {
        a = b = -1;
        if (incident == null)
            return false;

        // Drop dead segments in place, keeping order. Without this the end vertex of a long chain keeps
        // every segment its collapses ever replaced, and is re-read after each one: quadratic again.
        int write = 0;
        for (int read = 0; read < incident.Count; read++)
        {
            if (alive[incident[read]])
                incident[write++] = incident[read];
        }

        incident.RemoveRange(write, incident.Count - write);

        int count = 0;
        foreach (int s in incident)
        {
            int other = segments[s].A == vertex ? segments[s].B : segments[s].A;
            if (other == a || other == b)
                continue;

            if (count == 0)
                a = other;
            else if (count == 1)
                b = other;
            else
                return false;
            count++;
        }

        return count == 2;
    }

    private static bool ShouldCollapseSpike(
        List<VertexData> vertices,
        int a,
        int vertex,
        int b,
        double xyTol,
        double zTol)
    {
        if (DistanceSq(vertices[a], vertices[vertex]) <= xyTol * xyTol &&
            Math.Abs(vertices[a].Z - vertices[vertex].Z) <= zTol)
            return true;

        if (DistanceSq(vertices[vertex], vertices[b]) <= xyTol * xyTol &&
            Math.Abs(vertices[vertex].Z - vertices[b].Z) <= zTol)
            return true;

        return DistanceSq(vertices[a], vertices[b]) <= xyTol * xyTol &&
               Math.Abs(vertices[a].Z - vertices[b].Z) <= zTol;
    }

    private static bool ShouldCollapseCollinear(
        List<VertexData> vertices,
        int a,
        int vertex,
        int b,
        double xyTol,
        double zTol)
    {
        double dx = vertices[b].X - vertices[a].X;
        double dy = vertices[b].Y - vertices[a].Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq <= xyTol * xyTol)
            return false;

        double t = ((vertices[vertex].X - vertices[a].X) * dx + (vertices[vertex].Y - vertices[a].Y) * dy) / lenSq;
        if (t <= 1e-6 || t >= 1.0 - 1e-6)
            return false;

        double projX = vertices[a].X + t * dx;
        double projY = vertices[a].Y + t * dy;
        double lineTol = Math.Max(xyTol * 0.25, 1e-9);
        double distSq = (vertices[vertex].X - projX) * (vertices[vertex].X - projX) +
                        (vertices[vertex].Y - projY) * (vertices[vertex].Y - projY);
        if (distSq > lineTol * lineTol)
            return false;

        double expectedZ = vertices[a].Z + t * (vertices[b].Z - vertices[a].Z);
        return Math.Abs(vertices[vertex].Z - expectedZ) <= zTol;
    }

    private static void SplitIntersections(
        List<VertexData> vertices,
        List<SegmentData> segments,
        double xyTol,
        double zTol,
        ref int intersectionsSplit,
        ref int intersectionConflictsDetected)
    {
        if (segments.Count == 0)
            return;

        var segmentIndex = BuildSegmentSpatialIndex(vertices, segments, xyTol, out var bounds, out double invCell);
        var cuts = new List<SegmentCut>[segments.Count];
        var candidateMarks = new int[segments.Count];
        var candidates = new List<int>(16);
        int candidateStamp = 0;
        var vertexGrid = BuildVertexGrid(vertices, xyTol);

        for (int i = 0; i < segments.Count; i++)
        {
            FillSegmentCandidates(
                segmentIndex,
                bounds[i],
                invCell,
                candidateMarks,
                ref candidateStamp,
                candidates);

            foreach (int j in candidates)
            {
                if (j <= i)
                    continue;

                var left = segments[i];
                var right = segments[j];
                if (SharesEndpoint(left, right))
                    continue;

                var kind = TryGetIntersection(
                    vertices[left.A],
                    vertices[left.B],
                    vertices[right.A],
                    vertices[right.B],
                    xyTol,
                    out double leftT,
                    out double rightT,
                    out double x,
                    out double y);

                if (kind == SegmentIntersectionKind.None)
                    continue;

                if (kind == SegmentIntersectionKind.Overlap)
                {
                    intersectionConflictsDetected++;
                    continue;
                }

                double leftZ = Lerp(vertices[left.A].Z, vertices[left.B].Z, leftT);
                double rightZ = Lerp(vertices[right.A].Z, vertices[right.B].Z, rightT);
                if (Math.Abs(leftZ - rightZ) > zTol)
                {
                    intersectionConflictsDetected++;
                    continue;
                }

                bool leftAtEndpoint = IsEndpointParameter(vertices[left.A], vertices[left.B], leftT, xyTol);
                bool rightAtEndpoint = IsEndpointParameter(vertices[right.A], vertices[right.B], rightT, xyTol);

                if (leftAtEndpoint && rightAtEndpoint)
                    continue;

                if (leftAtEndpoint)
                {
                    AddCut(cuts, j, rightT, leftT <= 0.5 ? left.A : left.B);
                    intersectionsSplit++;
                    continue;
                }

                if (rightAtEndpoint)
                {
                    AddCut(cuts, i, leftT, rightT <= 0.5 ? right.A : right.B);
                    intersectionsSplit++;
                    continue;
                }

                int intersectionVertex = GetOrCreateBreaklineVertex(vertices, vertexGrid, x, y, (leftZ + rightZ) * 0.5, xyTol, zTol);
                AddCut(cuts, i, leftT, intersectionVertex);
                AddCut(cuts, j, rightT, intersectionVertex);
                intersectionsSplit++;
            }
        }

        if (intersectionsSplit == 0)
            return;

        RebuildSegmentsWithCuts(segments, cuts);
    }

    private static Dictionary<(long X, long Y), List<int>> BuildVertexGrid(List<VertexData> vertices, double xyTol)
    {
        double invCell = 1.0 / Math.Max(xyTol * 2.0, 1e-9);
        var grid = new Dictionary<(long X, long Y), List<int>>();

        for (int i = 0; i < vertices.Count; i++)
        {
            if ((vertices[i].Source & PointCloudProcessor.VertexSource.Breakline) == 0)
                continue;

            var key = ((long)Math.Floor(vertices[i].X * invCell), (long)Math.Floor(vertices[i].Y * invCell));
            if (!grid.TryGetValue(key, out var list))
            {
                list = new List<int>();
                grid[key] = list;
            }

            list.Add(i);
        }

        return grid;
    }

    private static int GetOrCreateBreaklineVertex(
        List<VertexData> vertices,
        Dictionary<(long X, long Y), List<int>> vertexGrid,
        double x,
        double y,
        double z,
        double xyTol,
        double zTol)
    {
        double invCell = 1.0 / Math.Max(xyTol * 2.0, 1e-9);
        long cx = (long)Math.Floor(x * invCell);
        long cy = (long)Math.Floor(y * invCell);

        for (long dx = -1; dx <= 1; dx++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                if (!vertexGrid.TryGetValue((cx + dx, cy + dy), out var indices))
                    continue;

                foreach (int index in indices)
                {
                    double distSq = (vertices[index].X - x) * (vertices[index].X - x) +
                                    (vertices[index].Y - y) * (vertices[index].Y - y);
                    if (distSq > xyTol * xyTol)
                        continue;

                    if (Math.Abs(vertices[index].Z - z) <= zTol)
                        return index;
                }
            }
        }

        int newIndex = vertices.Count;
        vertices.Add(new VertexData(x, y, z, PointCloudProcessor.VertexSource.Breakline));
        if (!vertexGrid.TryGetValue((cx, cy), out var list))
        {
            list = new List<int>();
            vertexGrid[(cx, cy)] = list;
        }

        list.Add(newIndex);
        return newIndex;
    }

    private static void RebuildSegmentsWithCuts(List<SegmentData> segments, List<SegmentCut>[] cuts)
    {
        var rebuilt = new List<SegmentData>(segments.Count * 2);
        var keys = IndexedMeshTools.CreateEdgeKeySet(segments.Count);

        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (cuts[i] == null || cuts[i].Count == 0)
            {
                TryAddSegment(rebuilt, keys, segment.A, segment.B);
                continue;
            }

            var ordered = new List<SegmentCut>(cuts[i].Count + 2)
            {
                new SegmentCut(0.0, segment.A),
                new SegmentCut(1.0, segment.B)
            };
            ordered.AddRange(cuts[i]);
            ordered.Sort(static (left, right) => left.T.CompareTo(right.T));

            var unique = new List<SegmentCut>(ordered.Count);
            foreach (var cut in ordered)
            {
                if (unique.Count == 0)
                {
                    unique.Add(cut);
                    continue;
                }

                var previous = unique[^1];
                if (Math.Abs(previous.T - cut.T) <= 1e-9 || previous.VertexIndex == cut.VertexIndex)
                {
                    if (cut.T < previous.T)
                        unique[^1] = cut;
                    continue;
                }

                unique.Add(cut);
            }

            for (int cutIndex = 0; cutIndex < unique.Count - 1; cutIndex++)
            {
                TryAddSegment(rebuilt, keys, unique[cutIndex].VertexIndex, unique[cutIndex + 1].VertexIndex);
            }
        }

        segments.Clear();
        segments.AddRange(rebuilt);
    }

    private static Dictionary<(long X, long Y), List<int>> BuildSegmentSpatialIndex(
        List<VertexData> vertices,
        List<SegmentData> segments,
        double xyTol,
        out (double MinX, double MaxX, double MinY, double MaxY)[] bounds,
        out double invCell)
    {
        bounds = new (double MinX, double MaxX, double MinY, double MaxY)[segments.Count];

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        foreach (var segment in segments)
        {
            var a = vertices[segment.A];
            var b = vertices[segment.B];
            minX = Math.Min(minX, Math.Min(a.X, b.X));
            maxX = Math.Max(maxX, Math.Max(a.X, b.X));
            minY = Math.Min(minY, Math.Min(a.Y, b.Y));
            maxY = Math.Max(maxY, Math.Max(a.Y, b.Y));
        }

        double span = Math.Max(maxX - minX, maxY - minY);
        double cellSize = Math.Max(xyTol * 4.0, span / Math.Max(8.0, Math.Sqrt(segments.Count)));
        invCell = 1.0 / Math.Max(cellSize, 1e-9);

        var index = new Dictionary<(long X, long Y), List<int>>();
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var a = vertices[segment.A];
            var b = vertices[segment.B];
            double segMinX = Math.Min(a.X, b.X) - xyTol;
            double segMaxX = Math.Max(a.X, b.X) + xyTol;
            double segMinY = Math.Min(a.Y, b.Y) - xyTol;
            double segMaxY = Math.Max(a.Y, b.Y) + xyTol;
            bounds[i] = (segMinX, segMaxX, segMinY, segMaxY);

            long minCellX = (long)Math.Floor(segMinX * invCell);
            long maxCellX = (long)Math.Floor(segMaxX * invCell);
            long minCellY = (long)Math.Floor(segMinY * invCell);
            long maxCellY = (long)Math.Floor(segMaxY * invCell);

            for (long cellY = minCellY; cellY <= maxCellY; cellY++)
            {
                for (long cellX = minCellX; cellX <= maxCellX; cellX++)
                {
                    if (!index.TryGetValue((cellX, cellY), out var list))
                    {
                        list = new List<int>();
                        index[(cellX, cellY)] = list;
                    }

                    list.Add(i);
                }
            }
        }

        return index;
    }

    private static void FillSegmentCandidates(
        Dictionary<(long X, long Y), List<int>> index,
        (double MinX, double MaxX, double MinY, double MaxY) bounds,
        double invCell,
        int[] marks,
        ref int stamp,
        List<int> candidates)
    {
        candidates.Clear();
        if (stamp == int.MaxValue)
        {
            Array.Clear(marks, 0, marks.Length);
            stamp = 1;
        }
        else
        {
            stamp++;
        }

        long minCellX = (long)Math.Floor(bounds.MinX * invCell);
        long maxCellX = (long)Math.Floor(bounds.MaxX * invCell);
        long minCellY = (long)Math.Floor(bounds.MinY * invCell);
        long maxCellY = (long)Math.Floor(bounds.MaxY * invCell);

        for (long cellY = minCellY; cellY <= maxCellY; cellY++)
        {
            for (long cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                if (!index.TryGetValue((cellX, cellY), out var list))
                    continue;

                foreach (int candidate in list)
                {
                    if (marks[candidate] == stamp)
                        continue;

                    marks[candidate] = stamp;
                    candidates.Add(candidate);
                }
            }
        }
    }

    private static void AddCut(List<SegmentCut>[] cuts, int segmentIndex, double t, int vertexIndex)
    {
        cuts[segmentIndex] ??= new List<SegmentCut>();
        foreach (var existing in cuts[segmentIndex])
        {
            if (existing.VertexIndex == vertexIndex || Math.Abs(existing.T - t) <= 1e-9)
                return;
        }

        cuts[segmentIndex].Add(new SegmentCut(Math.Max(0.0, Math.Min(1.0, t)), vertexIndex));
    }

    private static bool IsEndpointParameter(VertexData start, VertexData end, double t, double xyTol)
    {
        double length = Math.Sqrt(DistanceSq(start, end));
        if (length <= xyTol)
            return true;

        double paramTol = Math.Min(0.25, xyTol / length);
        return t <= paramTol || t >= 1.0 - paramTol;
    }

    private enum SegmentIntersectionKind
    {
        None,
        Point,
        Overlap
    }

    private static SegmentIntersectionKind TryGetIntersection(
        VertexData a0,
        VertexData a1,
        VertexData b0,
        VertexData b1,
        double xyTol,
        out double t,
        out double u,
        out double x,
        out double y)
    {
        t = 0;
        u = 0;
        x = 0;
        y = 0;

        double rX = a1.X - a0.X;
        double rY = a1.Y - a0.Y;
        double sX = b1.X - b0.X;
        double sY = b1.Y - b0.Y;
        double qpX = b0.X - a0.X;
        double qpY = b0.Y - a0.Y;
        double denom = Cross(rX, rY, sX, sY);
        double collinear = Cross(qpX, qpY, rX, rY);
        double eps = Math.Max(xyTol * 0.25, 1e-12);

        if (Math.Abs(denom) <= eps)
        {
            if (Math.Abs(collinear) > eps)
                return SegmentIntersectionKind.None;

            bool overlap =
                Math.Max(Math.Min(a0.X, a1.X), Math.Min(b0.X, b1.X)) <= Math.Min(Math.Max(a0.X, a1.X), Math.Max(b0.X, b1.X)) + xyTol &&
                Math.Max(Math.Min(a0.Y, a1.Y), Math.Min(b0.Y, b1.Y)) <= Math.Min(Math.Max(a0.Y, a1.Y), Math.Max(b0.Y, b1.Y)) + xyTol;

            return overlap ? SegmentIntersectionKind.Overlap : SegmentIntersectionKind.None;
        }

        t = Cross(qpX, qpY, sX, sY) / denom;
        u = Cross(qpX, qpY, rX, rY) / denom;

        double aTol = ParameterTolerance(rX, rY, xyTol);
        double bTol = ParameterTolerance(sX, sY, xyTol);
        if (t < -aTol || t > 1.0 + aTol || u < -bTol || u > 1.0 + bTol)
            return SegmentIntersectionKind.None;

        t = Math.Max(0.0, Math.Min(1.0, t));
        u = Math.Max(0.0, Math.Min(1.0, u));
        x = a0.X + t * rX;
        y = a0.Y + t * rY;

        double pointTolSq = xyTol * xyTol;
        if (DistancePointToSegmentSq(x, y, a0, a1) > pointTolSq ||
            DistancePointToSegmentSq(x, y, b0, b1) > pointTolSq)
            return SegmentIntersectionKind.None;

        return SegmentIntersectionKind.Point;
    }

    private static double ParameterTolerance(double dx, double dy, double xyTol)
    {
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= xyTol)
            return 0.25;

        return Math.Min(0.25, xyTol / length);
    }

    private static double DistancePointToSegmentSq(double x, double y, VertexData a, VertexData b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq <= 1e-20)
            return (x - a.X) * (x - a.X) + (y - a.Y) * (y - a.Y);

        double t = ((x - a.X) * dx + (y - a.Y) * dy) / lenSq;
        t = Math.Max(0.0, Math.Min(1.0, t));
        double projX = a.X + t * dx;
        double projY = a.Y + t * dy;
        double diffX = x - projX;
        double diffY = y - projY;
        return diffX * diffX + diffY * diffY;
    }

    private static bool SharesEndpoint(SegmentData left, SegmentData right)
    {
        return left.A == right.A || left.A == right.B || left.B == right.A || left.B == right.B;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    private static bool TryAddSegment(List<SegmentData> segments, HashSet<long> keys, int a, int b)
    {
        if (a == b)
            return false;

        long key = SegmentKey(a, b);
        if (!keys.Add(key))
            return false;

        segments.Add(new SegmentData(a, b));
        return true;
    }

    private static long SegmentKey(int a, int b)
    {
        uint min = (uint)Math.Min(a, b);
        uint max = (uint)Math.Max(a, b);
        return ((long)min << 32) | max;
    }

    private static double DistanceSq(VertexData a, VertexData b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
