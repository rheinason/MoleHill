using static MoleHill.Core.Engine.MeshFlipGeometry;

namespace MoleHill.Core.Engine;

/// <summary>
/// Feature topology of a mesh, extracted once before an isotropic remesh and owned for its whole run.
/// Feature edges (boundary ∪ auto-detected creases ∪ constraint breaklines) are chained into polylines;
/// every vertex is classified <see cref="KindFree"/> / <see cref="KindFeature"/> (interior to exactly one
/// chain, addressed by arc-length parameter) / <see cref="KindCorner"/> (chain junctions, endpoints,
/// sharp turns — never moved) / <see cref="KindFrozen"/> (touches a steep retaining-wall face — never
/// moved, split, or collapsed, so walls pass through a remesh verbatim). Chains store their original
/// piecewise-linear geometry, so <see cref="Evaluate"/> returns exact on-polyline XYZ — feature Z always
/// comes from the polyline, never from an XY surface sample (which is ambiguous exactly on a fold line).
/// </summary>
internal sealed class FeaturePolylineGraph
{
    public const byte KindFree = 0;
    public const byte KindFeature = 1;
    public const byte KindCorner = 2;
    public const byte KindFrozen = 3;

    /// <summary>Interior chain turns sharper than this become corners (they would round off if slid).</summary>
    private const double SharpTurnDeg = 40.0;

    public sealed class Chain
    {
        /// <summary>Polyline geometry as flat XYZ, in walk order (closed chains do not repeat the seam point).</summary>
        public double[] Points = Array.Empty<double>();

        /// <summary>Cumulative arc length per point; CumLength[0] = 0.</summary>
        public double[] CumLength = Array.Empty<double>();

        public bool Closed;

        /// <summary>Total arc length (for closed chains this includes the seam segment back to point 0).</summary>
        public double TotalLength;
    }

    public byte[] VertexKind = Array.Empty<byte>();

    /// <summary>Chain id per vertex (only meaningful for <see cref="KindFeature"/>), else -1.</summary>
    public int[] VertexChain = Array.Empty<int>();

    /// <summary>Arc-length parameter on <see cref="VertexChain"/> (only meaningful for <see cref="KindFeature"/>).</summary>
    public double[] VertexParam = Array.Empty<double>();

    /// <summary>Feature edge → chain id, for every mesh edge lying on a feature polyline.</summary>
    public Dictionary<long, int> FeatureEdgeChains = new(IndexedMeshTools.EdgeKeyComparer.Instance);

    /// <summary>Per input face: true when the face is steeper than the wall threshold (frozen).</summary>
    public bool[] FrozenFaces = Array.Empty<bool>();

    /// <summary>
    /// Per input face: true when the face is frozen because it touches a NON-MANIFOLD edge, not because
    /// it is a wall. Both are frozen against motion, but only this one must also never be subdivided —
    /// a duplicated face's children are duplicated too, so splitting one doubles the sickness. Kept apart
    /// from <see cref="FrozenFaces"/> because a wall is healthy geometry that merely must not move, and
    /// conflating the two denied walls any refinement at all.
    /// </summary>
    public bool[] QuarantinedFaces = Array.Empty<bool>();

    public List<Chain> Chains = new();

    /// <summary>
    /// Arc parameter of corner/frozen vertices per chain they lie on (feature vertices use the arrays).
    /// Key = (vertex &lt;&lt; 32) | chainId. Populated at build time only; corners never move or gain chains.
    /// </summary>
    private readonly Dictionary<long, double> _cornerParams = new(IndexedMeshTools.EdgeKeyComparer.Instance);

    public static FeaturePolylineGraph Build(
        double[] vertices,
        int[] faces,
        int faceCount,
        IReadOnlyList<ConstraintPolyline> constraints,
        double creaseAngleDeg,
        double wallFaceMinSlopeDeg,
        double tolerance,
        double minCreaseChainLength = 0.0,
        bool[]? heldVertices = null,
        bool[]? frozenFaces = null)
    {
        int vertexCount = vertices.Length / 3;
        var graph = new FeaturePolylineGraph
        {
            VertexKind = new byte[vertexCount],
            VertexChain = new int[vertexCount],
            VertexParam = new double[vertexCount],
            // Given, the wall classification is the caller's: the tiled remesher decides it once on the
            // original terrain and carries it through its passes, rather than re-deciding by slope on a mesh
            // that has already been remeshed and has a few steep artifacts of its own.
            FrozenFaces = frozenFaces is { } given && given.Length == faceCount
                ? (bool[])given.Clone()
                : BuildFrozenFaceMask(vertices, faces, faceCount, wallFaceMinSlopeDeg, degenerateAltitude: tolerance),
            QuarantinedFaces = new bool[faceCount]
        };
        Array.Fill(graph.VertexChain, -1);

        // --- Collect the raw feature edge set --------------------------------------------------------
        // One whole-mesh edge-incidence pass serves both readings of it: count == 1 is a boundary edge
        // (a feature), count > 2 is non-manifold. Building it twice - once through AddBoundarySegments,
        // which starts at capacity 8 and rehashes all the way up to ~1.5x the face count - was the
        // single most expensive part of feature setup on a large terrain.
        var edgeIncidence = new Dictionary<long, int>(faceCount * 2, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            CountIncidence(edgeIncidence, a, b);
            CountIncidence(edgeIncidence, b, c);
            CountIncidence(edgeIncidence, c, a);
        }

        var featureEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);

        // Non-manifold edges (imperfect upstream grading welds) are contained, not repaired: pin their
        // endpoints so no operator can touch or spread them. The remesh acceptance gate only requires
        // the output to be no worse than the input.
        var pinnedNonManifold = new List<long>();
        var nonManifoldEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        foreach ((long key, int count) in edgeIncidence)
        {
            if (count == 1)
            {
                // A degenerate self-edge is not a boundary; the boundary-segment builder skipped these.
                if ((int)(key >> 32) != (int)(key & 0xFFFFFFFFL))
                    featureEdges.Add(key);
            }
            else if (count > 2)
            {
                pinnedNonManifold.Add(key);
                nonManifoldEdges.Add(key);
            }
        }

        // Quarantine at FACE level: any face touching a non-manifold edge is frozen like a wall face.
        // Duplicate/overlapping faces (imperfect grading welds) otherwise multiply under subdivision —
        // splitting a duplicated face's healthy-looking edge duplicates its children too, doubling the
        // non-manifold count every round.
        if (nonManifoldEdges.Count > 0)
        {
            for (int f = 0; f < faceCount; f++)
            {
                int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
                if (nonManifoldEdges.Contains(EdgeKey(a, b)) ||
                    nonManifoldEdges.Contains(EdgeKey(b, c)) ||
                    nonManifoldEdges.Contains(EdgeKey(c, a)))
                {
                    graph.FrozenFaces[f] = true;
                    graph.QuarantinedFaces[f] = true;
                }
            }
        }

        if (creaseAngleDeg > 0)
        {
            double cosThreshold = Math.Cos(Math.Clamp(creaseAngleDeg, 1.0, 179.0) * Math.PI / 180.0);
            var creaseEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
            foreach ((int a, int b) in SurfaceRemesher.DetectCreaseEdges(vertices, faces, faceCount, cosThreshold))
            {
                long key = EdgeKey(a, b);
                if (!featureEdges.Contains(key))
                    creaseEdges.Add(key);
            }

            // Real terrain features (batter toes, slope breaks, road shoulders) form LONG coherent
            // crease chains. Badly triangulated fan regions also fold past the crease angle, but as a
            // dense web of short zigzag fragments; pinning those freezes the very topology the remesh
            // exists to clean up. Keep only crease chains longer than the threshold.
                if (minCreaseChainLength > 0)
                FilterShortCreaseChains(vertices, creaseEdges, minCreaseChainLength);
    
            foreach (long key in creaseEdges)
                featureEdges.Add(key);
        }

        MarkConstraintFeatureEdges(vertices, vertexCount, faces, faceCount, constraints, Math.Max(tolerance, 1e-6), featureEdges);

        // --- Frozen vertices (walls) take precedence over every other classification ------------------
        for (int f = 0; f < faceCount; f++)
        {
            if (!graph.FrozenFaces[f])
                continue;
            graph.VertexKind[faces[f * 3]] = KindFrozen;
            graph.VertexKind[faces[f * 3 + 1]] = KindFrozen;
            graph.VertexKind[faces[f * 3 + 2]] = KindFrozen;
        }

        // Held vertices (the ends of a tile's cut edges) are pinned the same way, and before the chains are
        // walked, so a chain through one records it as a fixed point with a parameter.
        if (heldVertices is { } held && held.Length == vertexCount)
        {
            for (int v = 0; v < vertexCount; v++)
            {
                if (held[v])
                    graph.VertexKind[v] = KindFrozen;
            }
        }

        // --- Feature-vertex adjacency and corner detection --------------------------------------------
        var neighbors = new Dictionary<int, List<int>>();
        foreach (long key in featureEdges)
        {
            int a = (int)(key >> 32);
            int b = (int)(key & 0xFFFFFFFFL);
            AddNeighbor(neighbors, a, b);
            AddNeighbor(neighbors, b, a);
        }

        var isCorner = new HashSet<int>();
        double cosSharp = Math.Cos(SharpTurnDeg * Math.PI / 180.0);
        foreach (KeyValuePair<int, List<int>> entry in neighbors)
        {
            int v = entry.Key;
            List<int> n = entry.Value;
            if (n.Count != 2)
            {
                isCorner.Add(v);
                continue;
            }

            // Degree-2 interior vertex: a sharp tangent turn also pins it (sliding would round the kink).
            if (TangentTurnCos(vertices, n[0], v, n[1]) < cosSharp)
                isCorner.Add(v);
        }

        // --- Walk chains: corner → corner, then leftover pure loops -----------------------------------
        var visited = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        var cornerList = new List<int>(isCorner);
        cornerList.Sort();
        foreach (int corner in cornerList)
        {
            foreach (int next in SortedCopy(neighbors[corner]))
            {
                long firstKey = EdgeKey(corner, next);
                if (visited.Contains(firstKey))
                    continue;
                graph.WalkChain(vertices, neighbors, isCorner, visited, corner, next);
            }
        }

        // Pure loops (no corner anywhere on them): start at the lowest-index vertex with unvisited edges.
        var loopStarts = new List<int>(neighbors.Keys);
        loopStarts.Sort();
        foreach (int v in loopStarts)
        {
            foreach (int next in SortedCopy(neighbors[v]))
            {
                long key = EdgeKey(v, next);
                if (visited.Contains(key))
                    continue;
                graph.WalkChain(vertices, neighbors, isCorner, visited, v, next);
            }
        }

        // --- Final vertex classification ---------------------------------------------------------------
        foreach (int v in isCorner)
        {
            if (graph.VertexKind[v] != KindFrozen)
                graph.VertexKind[v] = KindCorner;
        }

        // Pin non-manifold edges as chainless features (endpoints become corners below).
        foreach (long key in pinnedNonManifold)
            featureEdges.Add(key);

        // Any raw feature edge that did not land in a chain (degenerate-length chain, defensive walk
        // break) is still a feature: register it chainless (-1) and pin its endpoints so nothing can
        // slide or collapse it into a different shape.
        foreach (long key in featureEdges)
        {
            if (graph.FeatureEdgeChains.ContainsKey(key))
                continue;
            graph.FeatureEdgeChains[key] = -1;
            int a = (int)(key >> 32);
            int b = (int)(key & 0xFFFFFFFFL);
            if (graph.VertexKind[a] == KindFree || graph.VertexKind[a] == KindFeature)
            {
                graph.VertexKind[a] = KindCorner;
                graph.VertexChain[a] = -1;
            }

            if (graph.VertexKind[b] == KindFree || graph.VertexKind[b] == KindFeature)
            {
                graph.VertexKind[b] = KindCorner;
                graph.VertexChain[b] = -1;
            }
        }

        return graph;
    }

    /// <summary>Exact position on a chain at arc parameter <paramref name="t"/> (wrapped when closed).</summary>
    public void Evaluate(int chainId, double t, out double x, out double y, out double z)
    {
        Chain chain = Chains[chainId];
        int pointCount = chain.Points.Length / 3;
        if (chain.Closed)
        {
            t %= chain.TotalLength;
            if (t < 0)
                t += chain.TotalLength;
        }
        else
        {
            t = Math.Clamp(t, 0.0, chain.TotalLength);
        }

        // Find the segment containing t (CumLength is ascending).
        int hi = Array.BinarySearch(chain.CumLength, t);
        if (hi >= 0)
        {
            x = chain.Points[hi * 3];
            y = chain.Points[hi * 3 + 1];
            z = chain.Points[hi * 3 + 2];
            return;
        }

        hi = ~hi; // first index with CumLength > t
        int lo = hi - 1;
        double segStart = chain.CumLength[lo];
        double segEnd;
        int loPoint = lo;
        int hiPoint;
        if (hi >= pointCount)
        {
            // Only reachable on a closed chain: the seam segment back to point 0.
            segEnd = chain.TotalLength;
            hiPoint = 0;
        }
        else
        {
            segEnd = chain.CumLength[hi];
            hiPoint = hi;
        }

        double span = segEnd - segStart;
        double f = span > 1e-15 ? (t - segStart) / span : 0.0;
        x = chain.Points[loPoint * 3] + (chain.Points[hiPoint * 3] - chain.Points[loPoint * 3]) * f;
        y = chain.Points[loPoint * 3 + 1] + (chain.Points[hiPoint * 3 + 1] - chain.Points[loPoint * 3 + 1]) * f;
        z = chain.Points[loPoint * 3 + 2] + (chain.Points[hiPoint * 3 + 2] - chain.Points[loPoint * 3 + 2]) * f;
    }

    /// <summary>
    /// Arc parameter of <paramref name="vertex"/> on <paramref name="chainId"/>. Works for feature
    /// vertices (their own chain) and for corner/frozen vertices on any chain they lie on.
    /// </summary>
    public bool TryGetParam(int vertex, int chainId, IReadOnlyList<double> vertexParam, IReadOnlyList<int> vertexChain, IReadOnlyList<byte> vertexKind, out double t)
    {
        if (vertexKind[vertex] == KindFeature && vertexChain[vertex] == chainId)
        {
            t = vertexParam[vertex];
            return true;
        }

        return _cornerParams.TryGetValue(CornerKey(vertex, chainId), out t);
    }

    /// <summary>Arc midpoint of two parameters on a chain, taking the short way around a closed loop.</summary>
    public double MidParam(int chainId, double ta, double tb)
    {
        Chain chain = Chains[chainId];
        if (!chain.Closed)
            return (ta + tb) * 0.5;

        double total = chain.TotalLength;
        double diff = Math.Abs(ta - tb);
        if (diff <= total - diff)
            return (ta + tb) * 0.5;
        double m = (ta + tb + total) * 0.5;
        return m >= total ? m - total : m;
    }

    // === Chain walking =================================================================================

    private void WalkChain(
        double[] vertices,
        Dictionary<int, List<int>> neighbors,
        HashSet<int> isCorner,
        HashSet<long> visited,
        int start,
        int second)
    {
        var walk = new List<int> { start, second };
        visited.Add(EdgeKey(start, second));

        int previous = start;
        int current = second;
        bool closed = false;
        while (!isCorner.Contains(current))
        {
            List<int> n = neighbors[current];
            if (n.Count != 2)
                break; // defensive: should be a corner already

            int next = n[0] == previous ? n[1] : n[0];
            long key = EdgeKey(current, next);
            if (visited.Contains(key))
                break;
            visited.Add(key);

            if (next == start)
            {
                closed = true;
                break;
            }

            walk.Add(next);
            previous = current;
            current = next;
        }

        int chainId = Chains.Count;
        var chain = new Chain { Closed = closed };
        int count = walk.Count;
        chain.Points = new double[count * 3];
        chain.CumLength = new double[count];
        double cum = 0;
        for (int i = 0; i < count; i++)
        {
            int v = walk[i];
            chain.Points[i * 3] = vertices[v * 3];
            chain.Points[i * 3 + 1] = vertices[v * 3 + 1];
            chain.Points[i * 3 + 2] = vertices[v * 3 + 2];
            if (i > 0)
            {
                cum += Distance(vertices, walk[i - 1], v);
                chain.CumLength[i] = cum;
            }
        }

        chain.TotalLength = closed ? cum + Distance(vertices, walk[count - 1], walk[0]) : cum;
        if (chain.TotalLength <= 1e-12)
            return; // degenerate; leave these edges chainless (they stay plain feature-free edges)

        Chains.Add(chain);

        for (int i = 0; i < count; i++)
        {
            int v = walk[i];
            double t = chain.CumLength[i];
            if (VertexKind[v] == KindFrozen || isCorner.Contains(v))
                _cornerParams[CornerKey(v, chainId)] = t;
            else
            {
                VertexKind[v] = KindFeature;
                VertexChain[v] = chainId;
                VertexParam[v] = t;
            }

            if (i > 0)
                FeatureEdgeChains[EdgeKey(walk[i - 1], v)] = chainId;
        }

        if (closed)
            FeatureEdgeChains[EdgeKey(walk[count - 1], walk[0])] = chainId;
    }

    // === Helpers =======================================================================================

    private static long CornerKey(int vertex, int chainId) => ((long)vertex << 32) | (uint)chainId;

    private static void CountIncidence(Dictionary<long, int> edgeIncidence, int a, int b)
    {
        long key = EdgeKey(a, b);
        edgeIncidence[key] = edgeIncidence.TryGetValue(key, out int count) ? count + 1 : 1;
    }

    /// <summary>
    /// Removes crease edges that only form short chains. Chains are walked corner-to-corner (degree ≠ 2
    /// or a sharp turn breaks a chain, matching the main walk), then every chain whose polyline length
    /// is under <paramref name="minLength"/> has its edges dropped from the set.
    /// </summary>
    private static void FilterShortCreaseChains(double[] vertices, HashSet<long> creaseEdges, double minLength)
    {
        if (creaseEdges.Count == 0)
            return;

        var neighbors = new Dictionary<int, List<int>>();
        foreach (long key in creaseEdges)
        {
            int a = (int)(key >> 32);
            int b = (int)(key & 0xFFFFFFFFL);
            AddNeighbor(neighbors, a, b);
            AddNeighbor(neighbors, b, a);
        }

        double cosSharp = Math.Cos(SharpTurnDeg * Math.PI / 180.0);
        var breaks = new HashSet<int>();
        foreach (KeyValuePair<int, List<int>> entry in neighbors)
        {
            List<int> n = entry.Value;
            if (n.Count != 2 || TangentTurnCos(vertices, n[0], entry.Key, n[1]) < cosSharp)
                breaks.Add(entry.Key);
        }

        var visited = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        var toDrop = new List<long>();
        var chain = new List<long>();

        void Walk(int start, int second)
        {
            chain.Clear();
            double length = 0;
            int previous = start;
            int current = second;
            long key = EdgeKey(start, second);
            visited.Add(key);
            chain.Add(key);
            length += Distance(vertices, start, second);
            while (!breaks.Contains(current))
            {
                List<int> n = neighbors[current];
                if (n.Count != 2)
                    break;
                int next = n[0] == previous ? n[1] : n[0];
                long nextKey = EdgeKey(current, next);
                if (!visited.Add(nextKey))
                    break;
                chain.Add(nextKey);
                length += Distance(vertices, current, next);
                if (next == start)
                    break;
                previous = current;
                current = next;
            }

            if (length < minLength)
                toDrop.AddRange(chain);
        }

        var breakList = new List<int>(breaks);
        breakList.Sort();
        foreach (int start in breakList)
        {
            foreach (int next in SortedCopy(neighbors[start]))
            {
                if (!visited.Contains(EdgeKey(start, next)))
                    Walk(start, next);
            }
        }

        var loopStarts = new List<int>(neighbors.Keys);
        loopStarts.Sort();
        foreach (int start in loopStarts)
        {
            foreach (int next in SortedCopy(neighbors[start]))
            {
                if (!visited.Contains(EdgeKey(start, next)))
                    Walk(start, next);
            }
        }

        foreach (long key in toDrop)
            creaseEdges.Remove(key);
    }

    /// <summary>
    /// Per-face steep test: frozen where the 3-D normal leans further than the wall threshold.
    /// <para>
    /// A face thinner than <paramref name="degenerateAltitude"/> (the height over its longest edge) has no
    /// meaningful normal: three nearly collinear points along a breakline read as anything from 70° to 90°
    /// on rounding alone. Such a face stays frozen only when it is edge-connected, through other thin
    /// faces, to a genuine wall face — a sliver inside a wall is still wall. A stray one on open terrain is
    /// released: frozen, it pinned every vertex of the line it lay on, and wall bisection then refined that
    /// line to a fraction of the target with vertices collapse could never remove. 0 = keep every one.
    /// </para>
    /// </summary>
    internal static bool[] BuildFrozenFaceMask(
        double[] vertices, int[] faces, int faceCount, double wallFaceMinSlopeDeg, double degenerateAltitude = 0.0)
    {
        var frozen = new bool[faceCount];
        if (wallFaceMinSlopeDeg <= 0)
            return frozen;

        double cosThreshold = Math.Cos(Math.Clamp(wallFaceMinSlopeDeg, 1.0, 89.0) * Math.PI / 180.0);
        List<int>? thin = null;
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            TriangleNormal(vertices, a, b, c, out double nx, out double ny, out double nz);
            double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            frozen[f] = len > 1e-15 && Math.Abs(nz) / len < cosThreshold;
            if (!frozen[f] || degenerateAltitude <= 0)
                continue;

            // |n| is twice the area, so |n| / longest edge is the altitude onto that edge.
            double longest = Math.Max(Distance(vertices, a, b), Math.Max(Distance(vertices, b, c), Distance(vertices, c, a)));
            if (len < degenerateAltitude * longest)
                (thin ??= new List<int>()).Add(f);
        }

        if (thin != null)
            ReleaseStrayThinFaces(faces, faceCount, frozen, thin);

        return frozen;
    }

    private static void ReleaseStrayThinFaces(int[] faces, int faceCount, bool[] frozen, List<int> thin)
    {
        var isThin = new bool[faceCount];
        foreach (int f in thin)
            isThin[f] = true;

        // Edge → frozen faces on it. Only frozen faces take part, so the map stays small.
        var edgeFaces = IndexedMeshTools.CreateEdgeKeyMap<List<int>>(thin.Count * 3);
        for (int f = 0; f < faceCount; f++)
        {
            if (!frozen[f])
                continue;
            for (int k = 0; k < 3; k++)
            {
                long key = EdgeKey(faces[f * 3 + k], faces[f * 3 + ((k + 1) % 3)]);
                if (!edgeFaces.TryGetValue(key, out List<int>? list))
                    edgeFaces[key] = list = new List<int>(2);
                list.Add(f);
            }
        }

        // Flood outward from every genuine wall face across thin frozen faces; what is not reached is stray.
        var anchored = new bool[faceCount];
        var queue = new Queue<int>();
        for (int f = 0; f < faceCount; f++)
        {
            if (frozen[f] && !isThin[f])
            {
                anchored[f] = true;
                queue.Enqueue(f);
            }
        }

        while (queue.Count > 0)
        {
            int f = queue.Dequeue();
            for (int k = 0; k < 3; k++)
            {
                long key = EdgeKey(faces[f * 3 + k], faces[f * 3 + ((k + 1) % 3)]);
                foreach (int n in edgeFaces[key])
                {
                    if (anchored[n])
                        continue;
                    anchored[n] = true;
                    queue.Enqueue(n);
                }
            }
        }

        foreach (int f in thin)
        {
            if (!anchored[f])
                frozen[f] = false;
        }
    }

    /// <summary>Cosine of the 3-D tangent deviation walking n0 → v → n1 (1 = straight through).</summary>
    private static double TangentTurnCos(double[] vertices, int n0, int v, int n1)
    {
        double d1x = vertices[v * 3] - vertices[n0 * 3];
        double d1y = vertices[v * 3 + 1] - vertices[n0 * 3 + 1];
        double d1z = vertices[v * 3 + 2] - vertices[n0 * 3 + 2];
        double d2x = vertices[n1 * 3] - vertices[v * 3];
        double d2y = vertices[n1 * 3 + 1] - vertices[v * 3 + 1];
        double d2z = vertices[n1 * 3 + 2] - vertices[v * 3 + 2];
        double l1 = Math.Sqrt((d1x * d1x) + (d1y * d1y) + (d1z * d1z));
        double l2 = Math.Sqrt((d2x * d2x) + (d2y * d2y) + (d2z * d2z));
        if (l1 <= 1e-15 || l2 <= 1e-15)
            return 1.0;
        return ((d1x * d2x) + (d1y * d2y) + (d1z * d2z)) / (l1 * l2);
    }

    private static double Distance(double[] vertices, int a, int b)
    {
        double dx = vertices[a * 3] - vertices[b * 3];
        double dy = vertices[a * 3 + 1] - vertices[b * 3 + 1];
        double dz = vertices[a * 3 + 2] - vertices[b * 3 + 2];
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static void AddNeighbor(Dictionary<int, List<int>> neighbors, int v, int n)
    {
        if (!neighbors.TryGetValue(v, out List<int>? list))
        {
            list = new List<int>(2);
            neighbors.Add(v, list);
        }

        if (!list.Contains(n))
            list.Add(n);
    }

    private static List<int> SortedCopy(List<int> list)
    {
        var copy = new List<int>(list);
        copy.Sort();
        return copy;
    }

    /// <summary>
    /// Marks the mesh edges running along each constraint polyline segment as features. Every mesh vertex
    /// lying on a segment (in plan, within tolerance) is collected, and actual mesh edges joining those
    /// vertices are pinned. Nearby unrelated vertices must not interrupt the chain. A segment is routinely a CHAIN of mesh edges, not one:
    /// upstream stages put vertices partway along constraints (the retaining-wall quality patch refines
    /// every rail and breakline it crosses). Matching only a mesh edge spanning the whole segment dropped
    /// every such constraint from the remesh, which then flipped, collapsed and relaxed straight across it.
    /// </summary>
    private static void MarkConstraintFeatureEdges(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance,
        HashSet<long> featureEdges)
    {
        bool hasWork = false;
        foreach (ConstraintPolyline constraint in constraints)
        {
            if (constraint.PointCount >= 2)
            {
                hasWork = true;
                break;
            }
        }

        if (!hasWork || vertexCount == 0)
            return;

        var adjacency = MeshVertexAdjacency.Build(new List<int>(faces), faceCount, vertexCount);
        var nearSegment = new bool[vertexCount];

        // Cells sized to the mesh's typical spacing, so a segment walk visits a handful of cells per edge
        // rather than thousands of tolerance-sized ones. Never smaller than the matching radius, so the
        // 3x3 block around each step always covers it.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            minX = Math.Min(minX, vertices[i * 3]);
            maxX = Math.Max(maxX, vertices[i * 3]);
            minY = Math.Min(minY, vertices[i * 3 + 1]);
            maxY = Math.Max(maxY, vertices[i * 3 + 1]);
        }

        double spacing = Math.Sqrt(Math.Max((maxX - minX) * (maxY - minY), 0.0) / vertexCount);
        var index = new VertexXYGrid(vertices, vertexCount, Math.Max(Math.Max(spacing, tolerance * 4.0), 1e-6));
        var onSegment = new List<(double T, int Vertex)>();
        foreach (ConstraintPolyline constraint in constraints)
        {
            int pointCount = constraint.PointCount;
            if (pointCount < 2)
                continue;

            int segmentCount = constraint.IsClosed ? pointCount : pointCount - 1;
            for (int s = 0; s < segmentCount; s++)
            {
                int e = (s + 1) % pointCount;
                index.CollectNearSegment(
                    constraint.Points[s * 3], constraint.Points[s * 3 + 1],
                    constraint.Points[e * 3], constraint.Points[e * 3 + 1],
                    tolerance, onSegment);
                foreach (var point in onSegment) nearSegment[point.Vertex] = true;
                foreach (var point in onSegment)
                    foreach (int neighbour in adjacency.NeighborsOf(point.Vertex))
                        if (neighbour > point.Vertex && nearSegment[neighbour])
                            featureEdges.Add(EdgeKey(point.Vertex, neighbour));
                foreach (var point in onSegment) nearSegment[point.Vertex] = false;
            }
        }
    }

    /// <summary>Minimal XY spatial hash for finding the mesh vertices that lie along a constraint segment.</summary>
    private sealed class VertexXYGrid
    {
        private readonly double[] _vertices;
        private readonly Dictionary<(long X, long Y), List<int>> _cells = new();
        private readonly int _vertexCount;
        private readonly double _inverseCellSize;

        public VertexXYGrid(double[] vertices, int vertexCount, double cellSize)
        {
            _vertices = vertices;
            _vertexCount = vertexCount;
            _inverseCellSize = 1.0 / cellSize;
            for (int i = 0; i < vertexCount; i++)
            {
                var key = CellKey(vertices[i * 3], vertices[i * 3 + 1]);
                if (!_cells.TryGetValue(key, out List<int>? bucket))
                {
                    bucket = new List<int>(2);
                    _cells.Add(key, bucket);
                }

                bucket.Add(i);
            }
        }

        /// <summary>
        /// Fills <paramref name="result"/> with every vertex within <paramref name="tolerance"/> of segment
        /// A→B in plan. Walks the segment in cell-sized steps and
        /// scans the 3x3 block around each, which covers the tolerance because cells are never smaller.
        /// </summary>
        public void CollectNearSegment(double ax, double ay, double bx, double by, double tolerance, List<(double T, int Vertex)> result)
        {
            result.Clear();
            double dx = bx - ax, dy = by - ay;
            double lengthSquared = dx * dx + dy * dy;
            if (!double.IsFinite(lengthSquared) || lengthSquared <= 1e-24)
                return;

            double length = Math.Sqrt(lengthSquared);
            double toleranceT = tolerance / length;
            double toleranceSquared = tolerance * tolerance;
            void AddCandidate(int candidate)
            {
                double px = _vertices[candidate * 3] - ax;
                double py = _vertices[candidate * 3 + 1] - ay;
                double t = (px * dx + py * dy) / lengthSquared;
                if (t < -toleranceT || t > 1.0 + toleranceT) return;
                double ox = px - t * dx, oy = py - t * dy;
                if (ox * ox + oy * oy <= toleranceSquared) result.Add((t, candidate));
            }

            double requestedSteps = Math.Ceiling(length * _inverseCellSize) + 1;
            // A constraint may extend kilometres beyond a small mesh. Bound work by the mesh size,
            // and avoid overflowing an int step count or allocating millions of empty visited cells.
            if (requestedSteps > _vertexCount)
            {
                for (int candidate = 0; candidate < _vertexCount; candidate++) AddCandidate(candidate);
                return;
            }
            int steps = (int)requestedSteps;
            var visitedCells = new HashSet<(long X, long Y)>();
            for (int step = 0; step <= steps; step++)
            {
                double f = (double)step / steps;
                long centerX = (long)Math.Floor((ax + dx * f) * _inverseCellSize);
                long centerY = (long)Math.Floor((ay + dy * f) * _inverseCellSize);
                for (long cy = centerY - 1; cy <= centerY + 1; cy++)
                {
                    for (long cx = centerX - 1; cx <= centerX + 1; cx++)
                    {
                        var key = (cx, cy);
                        if (!visitedCells.Add(key) || !_cells.TryGetValue(key, out List<int>? bucket))
                            continue;

                        foreach (int candidate in bucket)
                        {
                            AddCandidate(candidate);
                        }
                    }
                }
            }

        }

        private (long X, long Y) CellKey(double x, double y)
        {
            long cx = (long)Math.Floor(x * _inverseCellSize);
            long cy = (long)Math.Floor(y * _inverseCellSize);
            return (cx, cy);
        }
    }
}
