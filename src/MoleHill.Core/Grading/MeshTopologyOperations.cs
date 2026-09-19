using System.Diagnostics.CodeAnalysis;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal enum CoincidentVertexZPolicy
{
    KeepFirst,
    UseLatest
}

internal static class MeshTopologyOperations
{
    public static bool TryFillSmallBranchedBoundaryLoops(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double tolerance,
        out int[] repairedFaces,
        out int repairedFaceCount,
        out int filledLoopCount)
    {
        repairedFaces = faces;
        repairedFaceCount = faceCount;
        filledLoopCount = 0;
        if (vertexCount <= 0 || faceCount <= 0)
            return false;

        Dictionary<ulong, int> edgeCounts = BuildEdgeCounts(faces, faceCount);
        var adjacency = BuildBoundaryAdjacency(edgeCounts);
        if (adjacency.Count == 0 || !adjacency.Values.Any(static neighbors => neighbors.Count > 2))
            return false;

        double resolvedTolerance = Math.Max(tolerance, 1e-9);
        var addedFaces = new List<int>();
        var filledKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach ((int branch, List<int> neighbors) in adjacency)
        {
            if (neighbors.Count <= 2)
                continue;

            foreach (int neighbor in neighbors)
            {
                if (!TryTraceBoundaryCycle(adjacency, branch, neighbor, out List<int>? cycle))
                    continue;

                int uniqueVertexCount = cycle.Count - 1;
                if (uniqueVertexCount < 3 || uniqueVertexCount > 16)
                    continue;

                string key = CreateCycleKey(cycle, uniqueVertexCount);
                if (!filledKeys.Add(key))
                    continue;

                double area = Math.Abs(ComputeSignedArea(vertices, cycle, uniqueVertexCount));
                double perimeter = ComputePerimeter(vertices, cycle, uniqueVertexCount);
                double maxFillArea = Math.Max(resolvedTolerance * resolvedTolerance * 100.0, resolvedTolerance * perimeter * 0.5);
                if (area <= 0.0 || area > maxFillArea)
                    continue;

                int anchor = cycle[0];
                for (int i = 1; i < uniqueVertexCount - 1; i++)
                {
                    int b = cycle[i];
                    int c = cycle[i + 1];
                    if (anchor == b || b == c || c == anchor)
                        continue;

                    addedFaces.Add(anchor);
                    addedFaces.Add(b);
                    addedFaces.Add(c);
                }

                filledLoopCount++;
            }
        }

        if (addedFaces.Count == 0)
        {
            filledLoopCount = 0;
            return false;
        }

        repairedFaces = new int[(faceCount * 3) + addedFaces.Count];
        Array.Copy(faces, repairedFaces, faceCount * 3);
        addedFaces.CopyTo(repairedFaces, faceCount * 3);
        repairedFaceCount = repairedFaces.Length / 3;
        return true;
    }

    /// <summary>
    /// Enforces the watertight 2.5D invariant on a graded region: first zips boundary "cracks"
    /// (pairs of near-coincident boundary vertices left by seam mismatches that fell just outside the
    /// weld tolerance), then closes any remaining interior holes. The stitch tolerance is scaled to
    /// the local boundary-edge length, so it only ever merges crack pairs (orders of magnitude closer
    /// than real adjacent boundary vertices), never distinct terrain features.
    /// </summary>
    public static (double[] vertices, int[] faces) MakeWatertight(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double weldTolerance,
        out int stitchedVertexCount,
        out int filledLoopCount)
    {
        stitchedVertexCount = 0;
        filledLoopCount = 0;
        if (vertexCount <= 0 || faceCount <= 0)
            return (vertices, faces);

        double stitchTolerance = ComputeBoundaryStitchTolerance(vertices, faces, faceCount, weldTolerance);
        double[] sv = vertices;
        int svc = vertexCount;
        int[] sf = faces;
        int sfc = faceCount;

        // Zip hairline cracks by merging mutual-nearest boundary-vertex pairs. Repeat: zipping one
        // pair can make the next pair mutual-nearest. Safe by construction — a 1:1 pair merge cannot
        // create a branched (non-manifold) boundary vertex.
        for (int pass = 0; pass < 6; pass++)
        {
            (sv, svc, sf, sfc, int merged) = StitchBoundaryCracks(sv, svc, sf, sfc, stitchTolerance);
            stitchedVertexCount += merged;
            if (merged == 0)
                break;
        }

        int[] filled = FillInteriorHoles(sv, sfc, sf, weldTolerance, out filledLoopCount);
        return (sv, filled);
    }

    /// <summary>
    /// Merges pairs of boundary vertices that lie within <paramref name="stitchTolerance"/> of each
    /// other but are not already joined by an edge — the signature of a hairline crack between two
    /// pieces that should have welded. Rebuilds faces against the merged vertices and drops faces that
    /// collapse to a degenerate edge.
    /// </summary>
    private static (double[], int, int[], int, int) StitchBoundaryCracks(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double stitchTolerance)
    {
        if (stitchTolerance <= 0.0)
            return (vertices, vertexCount, faces, faceCount, 0);

        Dictionary<ulong, int> edgeCounts = BuildEdgeCounts(faces, faceCount);
        var boundaryVerts = new List<int>();
        var isBoundary = new bool[vertexCount];
        foreach ((ulong edge, int count) in edgeCounts)
        {
            if (count != 1)
                continue;
            int a = (int)(edge >> 32);
            int b = (int)(edge & 0xFFFFFFFFu);
            if (!isBoundary[a]) { isBoundary[a] = true; boundaryVerts.Add(a); }
            if (!isBoundary[b]) { isBoundary[b] = true; boundaryVerts.Add(b); }
        }

        if (boundaryVerts.Count == 0)
            return (vertices, vertexCount, faces, faceCount, 0);

        // Edges already present between boundary vertices (don't merge across an existing edge).
        var connected = new HashSet<ulong>(edgeCounts.Count, IndexedMeshTools.PackedKeyComparer.Instance);
        foreach ((ulong edge, int _) in edgeCounts)
            connected.Add(edge);

        double inv = 1.0 / stitchTolerance;
        double tolSq = stitchTolerance * stitchTolerance;
        var buckets = new Dictionary<(long, long), List<int>>();
        foreach (int v in boundaryVerts)
        {
            long cx = (long)Math.Floor(vertices[v * 3] * inv);
            long cy = (long)Math.Floor(vertices[v * 3 + 1] * inv);
            if (!buckets.TryGetValue((cx, cy), out List<int>? list)) { list = new List<int>(2); buckets[(cx, cy)] = list; }
            list.Add(v);
        }

        // Nearest boundary partner (not already edge-connected) for each boundary vertex.
        int Nearest(int v)
        {
            double x = vertices[v * 3], y = vertices[v * 3 + 1];
            long cx = (long)Math.Floor(x * inv), cy = (long)Math.Floor(y * inv);
            int best = -1; double bestSq = tolSq;
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            {
                if (!buckets.TryGetValue((cx + dx, cy + dy), out List<int>? list)) continue;
                foreach (int w in list)
                {
                    if (w == v) continue;
                    if (connected.Contains(CreateEdgeKey(v, w))) continue;
                    double ddx = vertices[w * 3] - x, ddy = vertices[w * 3 + 1] - y;
                    double d = (ddx * ddx) + (ddy * ddy);
                    if (d < bestSq) { bestSq = d; best = w; }
                }
            }

            return best;
        }

        // Merge only mutual-nearest pairs, each vertex consumed once: a clean 1:1 zip that cannot
        // create a branched (non-manifold) boundary vertex.
        var remap = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            remap[i] = i;
        var consumed = new bool[vertexCount];
        int merged = 0;
        foreach (int v in boundaryVerts)
        {
            if (consumed[v]) continue;
            int w = Nearest(v);
            if (w < 0 || consumed[w]) continue;
            if (Nearest(w) != v) continue; // require mutual nearest
            remap[Math.Max(v, w)] = Math.Min(v, w);
            consumed[v] = true;
            consumed[w] = true;
            merged++;
        }

        if (merged == 0)
            return (vertices, vertexCount, faces, faceCount, 0);

        int Find(int x) { while (remap[x] != x) x = remap[x]; return x; }

        // Compact survivors, rebuild faces, drop degenerate (collapsed) faces.
        var newIndex = new int[vertexCount];
        Array.Fill(newIndex, -1);
        var outVerts = new List<double>(vertexCount * 3);
        for (int i = 0; i < vertexCount; i++)
        {
            int root = Find(i);
            if (newIndex[root] == -1)
            {
                newIndex[root] = outVerts.Count / 3;
                outVerts.Add(vertices[root * 3]);
                outVerts.Add(vertices[root * 3 + 1]);
                outVerts.Add(vertices[root * 3 + 2]);
            }

            newIndex[i] = newIndex[root];
        }

        var outFaces = new List<int>(faceCount * 3);
        for (int f = 0; f < faceCount; f++)
        {
            int a = newIndex[faces[f * 3]];
            int b = newIndex[faces[f * 3 + 1]];
            int c = newIndex[faces[f * 3 + 2]];
            if (a == b || b == c || c == a)
                continue;
            outFaces.Add(a);
            outFaces.Add(b);
            outFaces.Add(c);
        }

        return (outVerts.ToArray(), outVerts.Count / 3, outFaces.ToArray(), outFaces.Count / 3, merged);
    }


    /// <summary>Stitch tolerance = a small fraction of the median boundary-edge length (clamped).</summary>
    private static double ComputeBoundaryStitchTolerance(double[] vertices, int[] faces, int faceCount, double weldTolerance)
    {
        Dictionary<ulong, int> edgeCounts = BuildEdgeCounts(faces, faceCount);
        var lengths = new List<double>();
        foreach ((ulong edge, int count) in edgeCounts)
        {
            if (count != 1)
                continue;
            int a = (int)(edge >> 32);
            int b = (int)(edge & 0xFFFFFFFFu);
            double dx = vertices[a * 3] - vertices[b * 3];
            double dy = vertices[a * 3 + 1] - vertices[b * 3 + 1];
            lengths.Add(Math.Sqrt((dx * dx) + (dy * dy)));
        }

        if (lengths.Count == 0)
            return 0.0;

        lengths.Sort();
        double median = lengths[lengths.Count / 2];
        // A crack is far shorter than a real boundary edge; 30% of the median catches cracks while
        // staying well below the spacing of genuine adjacent boundary vertices.
        return Math.Max(weldTolerance, median * 0.3);
    }

    /// <summary>
    /// Enforces the watertight 2.5D invariant: closes every interior hole in <paramref name="faces"/>
    /// so the mesh is left with a single boundary loop (the outer terrain outline). All boundary loops
    /// are traced; the largest-area loop is kept as the outer outline and every other loop is ear-clipped
    /// closed using its existing vertices (upward winding, no new vertices, so the surface stays a
    /// continuous 2.5D CDT). Returns the original face array unchanged when there is nothing to fill.
    /// </summary>
    public static int[] FillInteriorHoles(
        double[] vertices,
        int faceCount,
        int[] faces,
        double tolerance,
        out int filledLoopCount)
    {
        filledLoopCount = 0;
        if (faceCount <= 0)
            return faces;

        Dictionary<ulong, int> edgeCounts = BuildEdgeCounts(faces, faceCount);
        var loops = TraceClosedBoundaryLoops(edgeCounts);
        if (loops.Count <= 1)
            return faces;

        // The outer terrain outline is the largest-area loop; everything else is an interior hole.
        int outerIndex = -1;
        double outerArea = -1.0;
        for (int i = 0; i < loops.Count; i++)
        {
            double area = Math.Abs(SignedAreaXy(vertices, loops[i]));
            if (area > outerArea)
            {
                outerArea = area;
                outerIndex = i;
            }
        }

        var addedFaces = new List<int>();
        for (int i = 0; i < loops.Count; i++)
        {
            if (i == outerIndex)
                continue;

            if (EarClipLoop(vertices, loops[i], tolerance, addedFaces))
                filledLoopCount++;
        }

        if (addedFaces.Count == 0)
            return faces;

        var result = new int[(faceCount * 3) + addedFaces.Count];
        Array.Copy(faces, result, faceCount * 3);
        addedFaces.CopyTo(result, faceCount * 3);
        return result;
    }

    /// <summary>Traces closed loops of the boundary-edge graph (edges used by exactly one face).</summary>
    private static List<List<int>> TraceClosedBoundaryLoops(Dictionary<ulong, int> edgeCounts)
    {
        var neighbors = new Dictionary<int, List<int>>();
        var remaining = new HashSet<(int, int)>();
        foreach ((ulong edge, int count) in edgeCounts)
        {
            if (count != 1)
                continue;

            int a = (int)(edge >> 32);
            int b = (int)(edge & 0xFFFFFFFFu);
            AddNeighbor(neighbors, a, b);
            AddNeighbor(neighbors, b, a);
            remaining.Add(a < b ? (a, b) : (b, a));
        }

        var loops = new List<List<int>>();
        while (remaining.Count > 0)
        {
            (int start, int next) = First(remaining);
            var loop = new List<int> { start };
            int previous = start;
            int current = next;
            remaining.Remove(Key(start, next));

            bool closed = false;
            while (true)
            {
                loop.Add(current);
                int step = -1;
                foreach (int candidate in neighbors[current])
                {
                    if (candidate == previous)
                        continue;
                    if (!remaining.Contains(Key(current, candidate)))
                        continue;

                    step = candidate;
                    break;
                }

                if (step < 0)
                    break;

                remaining.Remove(Key(current, step));
                if (step == start)
                {
                    closed = true;
                    break;
                }

                previous = current;
                current = step;
                if (loop.Count > neighbors.Count + 2)
                    break;
            }

            if (closed && loop.Count >= 3)
                loops.Add(loop);
        }

        return loops;

        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
        static (int, int) First(HashSet<(int, int)> set)
        {
            foreach ((int, int) e in set)
                return e;
            return (-1, -1);
        }
    }

    /// <summary>Ear-clips a simple polygon loop (XY) into upward-facing triangles using existing indices.</summary>
    private static bool EarClipLoop(double[] vertices, List<int> loop, double tolerance, List<int> outFaces)
    {
        int n = loop.Count;
        if (n < 3)
            return false;

        // Work on a mutable copy ordered CCW so emitted triangles have upward normals.
        var poly = new List<int>(loop);
        if (SignedAreaXy(vertices, poly) < 0.0)
            poly.Reverse();

        double epsilon = Math.Max(tolerance, 1e-9);
        int guard = 0;
        int emitted = 0;
        while (poly.Count > 3 && guard++ < n * n + 8)
        {
            bool clipped = false;
            int m = poly.Count;
            for (int i = 0; i < m; i++)
            {
                int ia = poly[(i + m - 1) % m];
                int ib = poly[i];
                int ic = poly[(i + 1) % m];
                if (!IsEar(vertices, poly, ia, ib, ic, epsilon))
                    continue;

                outFaces.Add(ia);
                outFaces.Add(ib);
                outFaces.Add(ic);
                emitted++;
                poly.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
                break; // degenerate/self-touching loop; leave the remainder to the topology gate
        }

        if (poly.Count == 3)
        {
            outFaces.Add(poly[0]);
            outFaces.Add(poly[1]);
            outFaces.Add(poly[2]);
            emitted++;
        }

        return emitted > 0;
    }

    private static bool IsEar(double[] vertices, List<int> poly, int ia, int ib, int ic, double epsilon)
    {
        double ax = vertices[ia * 3], ay = vertices[ia * 3 + 1];
        double bx = vertices[ib * 3], by = vertices[ib * 3 + 1];
        double cx = vertices[ic * 3], cy = vertices[ic * 3 + 1];

        double cross = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        if (cross <= epsilon)
            return false; // reflex or collinear (poly is CCW, so an ear turns left)

        foreach (int idx in poly)
        {
            if (idx == ia || idx == ib || idx == ic)
                continue;

            if (PointInTriangle(vertices[idx * 3], vertices[idx * 3 + 1], ax, ay, bx, by, cx, cy))
                return false;
        }

        return true;
    }

    private static bool PointInTriangle(double px, double py, double ax, double ay, double bx, double by, double cx, double cy)
    {
        double d1 = ((px - bx) * (ay - by)) - ((ax - bx) * (py - by));
        double d2 = ((px - cx) * (by - cy)) - ((bx - cx) * (py - cy));
        double d3 = ((px - ax) * (cy - ay)) - ((cx - ax) * (py - ay));
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private static double SignedAreaXy(double[] vertices, List<int> loop)
    {
        double area2 = 0.0;
        int n = loop.Count;
        for (int i = 0; i < n; i++)
        {
            int a = loop[i];
            int b = loop[(i + 1) % n];
            area2 += (vertices[a * 3] * vertices[b * 3 + 1]) - (vertices[b * 3] * vertices[a * 3 + 1]);
        }

        return area2 * 0.5;
    }

    public static void MergeMeshes(
        double[] firstVertices,
        int firstVertexCount,
        int[] firstFaces,
        int firstFaceCount,
        double[] secondVertices,
        int secondVertexCount,
        int[] secondFaces,
        int secondFaceCount,
        double tolerance,
        CoincidentVertexZPolicy coincidentZPolicy,
        out double[] mergedVertices,
        out int mergedVertexCount,
        out int[] mergedFaces,
        out int mergedFaceCount)
    {
        var xyList = new List<double>(firstVertexCount * 2 + secondVertexCount * 2);
        var zList = new List<double>(firstVertexCount + secondVertexCount);
        var vertHash = new SpatialVertexHash(tolerance);
        var seenFaces = new HashSet<ulong>(firstFaceCount + secondFaceCount, IndexedMeshTools.PackedKeyComparer.Instance);

        int AddVertex(double x, double y, double z)
        {
            int near = vertHash.FindNearest(xyList, x, y, tolerance);
            if (near >= 0)
            {
                if (coincidentZPolicy == CoincidentVertexZPolicy.UseLatest)
                    zList[near] = z;

                return near;
            }

            int index = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(z);
            vertHash.Insert(index, x, y);
            return index;
        }

        bool TryAddFace(List<int> faceList, int a, int b, int c)
        {
            if (a == b || b == c || c == a)
                return false;

            double ax = xyList[a * 2];
            double ay = xyList[a * 2 + 1];
            double bx = xyList[b * 2];
            double by = xyList[b * 2 + 1];
            double cx = xyList[c * 2];
            double cy = xyList[c * 2 + 1];
            double area2 = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            if (Math.Abs(area2) <= tolerance * tolerance)
                return false;

            ulong key = CreateFaceKey(a, b, c);
            if (!seenFaces.Add(key))
                return false;

            faceList.Add(a);
            faceList.Add(b);
            faceList.Add(c);
            return true;
        }

        var faceList = new List<int>((firstFaceCount + secondFaceCount) * 3);
        var firstRemap = new int[firstVertexCount];
        for (int i = 0; i < firstVertexCount; i++)
            firstRemap[i] = AddVertex(firstVertices[i * 3], firstVertices[i * 3 + 1], firstVertices[i * 3 + 2]);

        for (int i = 0; i < firstFaceCount; i++)
        {
            int a = firstRemap[firstFaces[i * 3]];
            int b = firstRemap[firstFaces[i * 3 + 1]];
            int c = firstRemap[firstFaces[i * 3 + 2]];
            TryAddFace(faceList, a, b, c);
        }

        var secondRemap = new int[secondVertexCount];
        for (int i = 0; i < secondVertexCount; i++)
            secondRemap[i] = AddVertex(secondVertices[i * 3], secondVertices[i * 3 + 1], secondVertices[i * 3 + 2]);

        for (int i = 0; i < secondFaceCount; i++)
        {
            int a = secondRemap[secondFaces[i * 3]];
            int b = secondRemap[secondFaces[i * 3 + 1]];
            int c = secondRemap[secondFaces[i * 3 + 2]];
            TryAddFace(faceList, a, b, c);
        }

        mergedVertexCount = zList.Count;
        mergedVertices = new double[mergedVertexCount * 3];
        for (int i = 0; i < mergedVertexCount; i++)
        {
            mergedVertices[i * 3] = xyList[i * 2];
            mergedVertices[i * 3 + 1] = xyList[i * 2 + 1];
            mergedVertices[i * 3 + 2] = zList[i];
        }

        mergedFaces = faceList.ToArray();
        mergedFaceCount = mergedFaces.Length / 3;
    }

    private static ulong CreateFaceKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return ((ulong)(uint)a << 42) | ((ulong)(uint)b << 21) | (uint)c;
    }

    private static Dictionary<ulong, int> BuildEdgeCounts(int[] faces, int faceCount)
    {
        var edgeCounts = new Dictionary<ulong, int>(faceCount * 2, IndexedMeshTools.PackedKeyComparer.Instance);
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[(faceIndex * 3) + 1];
            int c = faces[(faceIndex * 3) + 2];
            CountEdge(edgeCounts, a, b);
            CountEdge(edgeCounts, b, c);
            CountEdge(edgeCounts, c, a);
        }

        return edgeCounts;
    }

    private static Dictionary<int, List<int>> BuildBoundaryAdjacency(Dictionary<ulong, int> edgeCounts)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach ((ulong edge, int count) in edgeCounts)
        {
            if (count != 1)
                continue;

            int a = (int)(edge >> 32);
            int b = (int)(edge & 0xFFFFFFFFu);
            AddNeighbor(adjacency, a, b);
            AddNeighbor(adjacency, b, a);
        }

        return adjacency;
    }

    private static void CountEdge(Dictionary<ulong, int> edgeCounts, int a, int b)
    {
        ulong key = CreateEdgeKey(a, b);
        edgeCounts[key] = edgeCounts.GetValueOrDefault(key) + 1;
    }

    private static ulong CreateEdgeKey(int a, int b)
    {
        return a < b
            ? ((ulong)(uint)a << 32) | (uint)b
            : ((ulong)(uint)b << 32) | (uint)a;
    }

    private static void AddNeighbor(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out List<int>? neighbors))
        {
            neighbors = new List<int>(2);
            adjacency[from] = neighbors;
        }

        if (!neighbors.Contains(to))
            neighbors.Add(to);
    }

    private static bool TryTraceBoundaryCycle(
        Dictionary<int, List<int>> adjacency,
        int branch,
        int firstNeighbor,
        [NotNullWhen(true)] out List<int>? cycle)
    {
        cycle = null;
        var path = new List<int> { branch, firstNeighbor };
        int previous = branch;
        int current = firstNeighbor;

        while (true)
        {
            if (!adjacency.TryGetValue(current, out List<int>? neighbors))
                return false;

            if (current != branch && neighbors.Count != 2)
                return false;

            int next = -1;
            foreach (int candidate in neighbors)
            {
                if (candidate == previous)
                    continue;

                next = candidate;
                break;
            }

            if (next < 0)
                return false;

            path.Add(next);
            if (next == branch)
            {
                cycle = path;
                return true;
            }

            previous = current;
            current = next;
            if (path.Count > adjacency.Count + 1)
                return false;
        }
    }

    private static string CreateCycleKey(List<int> cycle, int uniqueVertexCount)
    {
        int[] vertices = new int[uniqueVertexCount];
        for (int i = 0; i < uniqueVertexCount; i++)
            vertices[i] = cycle[i];

        Array.Sort(vertices);
        return string.Join(",", vertices);
    }

    private static double ComputeSignedArea(double[] vertices, List<int> cycle, int uniqueVertexCount)
    {
        double area2 = 0.0;
        for (int i = 0; i < uniqueVertexCount; i++)
        {
            int current = cycle[i];
            int next = cycle[(i + 1) % uniqueVertexCount];
            double ax = vertices[current * 3];
            double ay = vertices[(current * 3) + 1];
            double bx = vertices[next * 3];
            double by = vertices[(next * 3) + 1];
            area2 += (ax * by) - (bx * ay);
        }

        return area2 * 0.5;
    }

    private static double ComputePerimeter(double[] vertices, List<int> cycle, int uniqueVertexCount)
    {
        double perimeter = 0.0;
        for (int i = 0; i < uniqueVertexCount; i++)
        {
            int current = cycle[i];
            int next = cycle[(i + 1) % uniqueVertexCount];
            double dx = vertices[current * 3] - vertices[next * 3];
            double dy = vertices[(current * 3) + 1] - vertices[(next * 3) + 1];
            perimeter += Math.Sqrt((dx * dx) + (dy * dy));
        }

        return perimeter;
    }
}
