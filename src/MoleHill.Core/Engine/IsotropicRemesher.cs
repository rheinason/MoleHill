using MoleHill.Core.Grading;
using static MoleHill.Core.Engine.MeshFlipGeometry;

namespace MoleHill.Core.Engine;

/// <summary>
/// Incremental isotropic remesh (Botsch–Kobbelt loop): split long edges, collapse short edges, flip for
/// triangle quality, relax tangentially, back-project. The terrain is 2.5D, which makes the full loop
/// safe where connectivity-only passes failed: every moved or added vertex re-samples Z from the
/// ORIGINAL input mesh at its new XY, so the output vertices sit exactly on the input surface — no
/// off-surface smoothing, no volume drift. Feature polylines (boundary ∪ creases ∪ constraint
/// breaklines, see <see cref="FeaturePolylineGraph"/>) are hard-pinned: their edges never flip, their
/// vertices only slide 1-D along the polyline (exact on-polyline positions), corners never move, and
/// collapses never merge across features. Steep retaining-wall faces are frozen outright — never split,
/// collapsed, flipped, or moved — so walls pass through verbatim and can't be buried; wall faces are
/// also excluded from the back-projection grid so near-wall samples always land on the correct terrain
/// side of the crest/toe fold.
/// </summary>
public static class IsotropicRemesher
{
    public sealed class Options
    {
        /// <summary>Target edge length L. Split above 4/3·L, collapse below 4/5·L. Required (&gt; 0).</summary>
        public double TargetEdgeLength { get; init; }

        /// <summary>Auto-detect interior creases folding at least this many degrees as features. 0 = off.</summary>
        public double CreaseAngleDeg { get; init; }

        public double Tolerance { get; init; }

        /// <summary>Faces steeper than this (degrees from horizontal) are frozen retaining walls. 0 = off.</summary>
        public double WallFaceMinSlopeDeg { get; init; }

        /// <summary>Full split/collapse/flip/relax rounds. Preview builds pass fewer.</summary>
        public int Iterations { get; init; } = 5;
    }

    public sealed class Result
    {
        public bool Success { get; init; }

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Faces { get; init; } = Array.Empty<int>();

        public string? Warning { get; init; }

        public int Splits { get; init; }

        public int Collapses { get; init; }

        public int Flips { get; init; }

        public int RelaxedVertices { get; init; }

        /// <summary>Feature edges of the output mesh as vertex-index pairs (for downstream quad pairing).</summary>
        public int[] FeatureEdges { get; init; } = Array.Empty<int>();

        /// <summary>Per output face: true when the face is a frozen (steep retaining-wall) face.</summary>
        public bool[] FrozenFaces { get; init; } = Array.Empty<bool>();
    }

    private const double SplitFactor = 4.0 / 3.0;
    private const double CollapseFactor = 4.0 / 5.0;
    private const double RelaxLambda = 0.5;
    private const double FlipAngleImproveEps = 1e-3;
    private const int MaxSplitRounds = 24;
    private const int MaxFlipSweeps = 16;

    public static Result Remesh(
        double[] vertices,
        int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        Options options)
    {
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;
        if (vertexCount == 0 || faceCount == 0)
            return new Result { Success = false, Vertices = vertices, Faces = faces, Warning = "Input mesh has no usable triangles." };
        if (options.TargetEdgeLength <= 0)
            return new Result { Success = false, Vertices = vertices, Faces = faces, Warning = "Isotropic remesh requires a positive target edge length." };

        var graph = FeaturePolylineGraph.Build(
            vertices, faces, faceCount, constraints, options.CreaseAngleDeg, options.WallFaceMinSlopeDeg, options.Tolerance);

        // Back-projection grid over the ORIGINAL mesh with wall faces excluded: a near-vertical wall is
        // an XY sliver, so sampling it would return a mid-wall Z and smear the wall onto the terrain.
        TerrainFaceGrid? projection = BuildProjectionGrid(vertices, faces, faceCount, graph.FrozenFaces);
        if (projection is null)
            return new Result { Success = true, Vertices = vertices, Faces = faces, Warning = "All faces are steep (frozen); nothing to remesh." };

        var state = new MeshState(vertices, faces, graph);

        double target = options.TargetEdgeLength;
        int totalSplits = 0, totalCollapses = 0, totalFlips = 0, totalRelaxed = 0;
        int iterations = Math.Max(1, options.Iterations);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            int splits = SplitLongEdges(state, target * SplitFactor, projection);
            int collapses = CollapseShortEdges(state, target, projection);
            int flips = FlipForQuality(state);
            int relaxed = RelaxAndProject(state, target, projection);
            totalSplits += splits;
            totalCollapses += collapses;
            totalFlips += flips;
            totalRelaxed += relaxed;
            if (splits == 0 && collapses == 0 && flips == 0 && relaxed == 0)
                break;
        }

        return state.ToResult(vertices, faces, totalSplits, totalCollapses, totalFlips, totalRelaxed);
    }

    private static TerrainFaceGrid? BuildProjectionGrid(double[] vertices, int[] faces, int faceCount, bool[] frozenFaces)
    {
        int activeCount = 0;
        for (int f = 0; f < faceCount; f++)
        {
            if (!frozenFaces[f])
                activeCount++;
        }

        if (activeCount == 0)
            return null;
        if (activeCount == faceCount)
            return new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faceCount);

        var activeFaces = new int[activeCount * 3];
        int next = 0;
        for (int f = 0; f < faceCount; f++)
        {
            if (frozenFaces[f])
                continue;
            activeFaces[next * 3] = faces[f * 3];
            activeFaces[next * 3 + 1] = faces[f * 3 + 1];
            activeFaces[next * 3 + 2] = faces[f * 3 + 2];
            next++;
        }

        return new TerrainFaceGrid(vertices, vertices.Length / 3, activeFaces, activeCount);
    }

    // === Working state =================================================================================

    /// <summary>
    /// Growable mesh + per-vertex classification, shared by all four operator phases. Faces removed by
    /// collapses are tombstoned (first index -1) and swept out at the end of the collapse phase; vertices
    /// orphaned by collapses stay in the arrays and are dropped by the final compaction.
    /// </summary>
    private sealed class MeshState
    {
        public readonly List<double> Verts;
        public readonly List<int> Tris;
        public readonly List<bool> FaceFrozen;
        public readonly List<byte> Kind;
        public readonly List<int> Chain;
        public readonly List<double> Param;
        public readonly Dictionary<long, int> FeatureEdges;
        public readonly FeaturePolylineGraph Graph;

        public MeshState(double[] vertices, int[] faces, FeaturePolylineGraph graph)
        {
            Verts = new List<double>(vertices);
            Tris = new List<int>(faces);
            Graph = graph;
            Kind = new List<byte>(graph.VertexKind);
            Chain = new List<int>(graph.VertexChain);
            Param = new List<double>(graph.VertexParam);
            FeatureEdges = new Dictionary<long, int>(graph.FeatureEdgeChains, IndexedMeshTools.EdgeKeyComparer.Instance);
            FaceFrozen = new List<bool>(graph.FrozenFaces);
        }

        public int FaceCount => Tris.Count / 3;

        public int VertexCount => Verts.Count / 3;

        public bool IsLive(int face) => Tris[face * 3] >= 0;

        public bool TryGetParam(int vertex, int chainId, out double t) =>
            Graph.TryGetParam(vertex, chainId, Param, Chain, Kind, out t);

        public int AddVertex(double x, double y, double z, byte kind, int chain, double param)
        {
            int index = VertexCount;
            Verts.Add(x);
            Verts.Add(y);
            Verts.Add(z);
            Kind.Add(kind);
            Chain.Add(chain);
            Param.Add(param);
            return index;
        }

        /// <summary>Drops tombstoned faces in place, keeping the frozen flags aligned.</summary>
        public void SweepTombstones()
        {
            int write = 0;
            int count = FaceCount;
            for (int t = 0; t < count; t++)
            {
                if (Tris[t * 3] < 0)
                    continue;
                if (write != t)
                {
                    Tris[write * 3] = Tris[t * 3];
                    Tris[write * 3 + 1] = Tris[t * 3 + 1];
                    Tris[write * 3 + 2] = Tris[t * 3 + 2];
                    FaceFrozen[write] = FaceFrozen[t];
                }

                write++;
            }

            Tris.RemoveRange(write * 3, (count - write) * 3);
            FaceFrozen.RemoveRange(write, count - write);
        }

        public Result ToResult(double[] inputVertices, int[] inputFaces, int splits, int collapses, int flips, int relaxed)
        {
            var faces = Tris.ToArray();
            var compaction = IndexedMeshTools.Compact(VertexCount, faces, faces.Length / 3);
            double[] outVertices = IndexedMeshTools.CompactDoubleData(Verts.ToArray(), 3, compaction.NewToOld, compaction.VertexCount);

            var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(compaction.Faces, compaction.FaceCount);
            if (topology.NonManifoldEdgeCount != 0 || topology.HasOpenBoundaryChains)
            {
                return new Result
                {
                    Success = false,
                    Vertices = inputVertices,
                    Faces = inputFaces,
                    Warning = "Isotropic remesh produced invalid topology; kept the input mesh unchanged."
                };
            }

            var featurePairs = new List<int>(FeatureEdges.Count * 2);
            foreach (long key in FeatureEdges.Keys)
            {
                int a = compaction.OldToNew[(int)(key >> 32)];
                int b = compaction.OldToNew[(int)(key & 0xFFFFFFFFL)];
                if (a >= 0 && b >= 0)
                {
                    featurePairs.Add(a);
                    featurePairs.Add(b);
                }
            }

            return new Result
            {
                Success = true,
                Vertices = outVertices,
                Faces = compaction.Faces,
                Splits = splits,
                Collapses = collapses,
                Flips = flips,
                RelaxedVertices = relaxed,
                FeatureEdges = featurePairs.ToArray(),
                FrozenFaces = FaceFrozen.ToArray()
            };
        }
    }

    // === Phase 1: split ================================================================================

    /// <summary>
    /// Splits every triangle whose longest edge exceeds the threshold at that edge's midpoint, round by
    /// round (Rivara-style; midpoints keyed per edge so both incident triangles conform). Feature-edge
    /// midpoints are evaluated ON the feature polyline; free midpoints take the chord XY and re-sample Z
    /// from the original surface. Frozen faces and their edges are never touched.
    /// </summary>
    private static int SplitLongEdges(MeshState state, double threshold, TerrainFaceGrid projection)
    {
        double thresholdSquared = threshold * threshold;
        int added = 0;

        for (int round = 0; round < MaxSplitRounds; round++)
        {
            int faceCount = state.FaceCount;
            var frozenEdges = CollectFrozenEdges(state);

            var marked = new List<long>();
            var markedSet = new HashSet<long>();
            for (int t = 0; t < faceCount; t++)
            {
                if (state.FaceFrozen[t])
                    continue;
                int v0 = state.Tris[t * 3], v1 = state.Tris[t * 3 + 1], v2 = state.Tris[t * 3 + 2];
                long longest = LongestEdgeKey(state.Verts, v0, v1, v2, out double longestSquared);
                if (longestSquared <= thresholdSquared || frozenEdges.Contains(longest))
                    continue;
                if (markedSet.Add(longest))
                    marked.Add(longest);
            }

            if (marked.Count == 0)
                break;

            marked.Sort();
            var midpoints = new Dictionary<long, int>(marked.Count, IndexedMeshTools.EdgeKeyComparer.Instance);
            foreach (long edgeKey in marked)
            {
                int a = (int)(edgeKey >> 32);
                int b = (int)(edgeKey & 0xFFFFFFFFL);
                midpoints[edgeKey] = CreateMidpoint(state, projection, edgeKey, a, b);
                added++;
            }

            var next = new List<int>(state.Tris.Count * 2);
            var nextFrozen = new List<bool>(state.FaceFrozen.Count * 2);
            for (int t = 0; t < faceCount; t++)
            {
                int v0 = state.Tris[t * 3], v1 = state.Tris[t * 3 + 1], v2 = state.Tris[t * 3 + 2];
                int m0 = midpoints.TryGetValue(EdgeKey(v0, v1), out int i0) ? i0 : -1;
                int m1 = midpoints.TryGetValue(EdgeKey(v1, v2), out int i1) ? i1 : -1;
                int m2 = midpoints.TryGetValue(EdgeKey(v2, v0), out int i2) ? i2 : -1;
                int before = next.Count / 3;
                EmitRefinedTriangle(state.Verts, next, v0, v1, v2, m0, m1, m2);
                int emitted = next.Count / 3 - before;
                for (int i = 0; i < emitted; i++)
                    nextFrozen.Add(state.FaceFrozen[t]);
            }

            state.Tris.Clear();
            state.Tris.AddRange(next);
            state.FaceFrozen.Clear();
            state.FaceFrozen.AddRange(nextFrozen);
        }

        return added;
    }

    private static int CreateMidpoint(MeshState state, TerrainFaceGrid projection, long edgeKey, int a, int b)
    {
        double chordX = (state.Verts[a * 3] + state.Verts[b * 3]) * 0.5;
        double chordY = (state.Verts[a * 3 + 1] + state.Verts[b * 3 + 1]) * 0.5;
        double chordZ = (state.Verts[a * 3 + 2] + state.Verts[b * 3 + 2]) * 0.5;

        if (state.FeatureEdges.TryGetValue(edgeKey, out int chain))
        {
            if (chain >= 0 &&
                state.TryGetParam(a, chain, out double ta) &&
                state.TryGetParam(b, chain, out double tb))
            {
                double tm = state.Graph.MidParam(chain, ta, tb);
                state.Graph.Evaluate(chain, tm, out double px, out double py, out double pz);
                int mid = state.AddVertex(px, py, pz, FeaturePolylineGraph.KindFeature, chain, tm);
                InheritFeatureEdge(state, a, b, mid, chain);
                return mid;
            }

            // Chainless/degenerate feature edge: keep the midpoint pinned so it can't drift off the line.
            int pinned = state.AddVertex(chordX, chordY, chordZ, FeaturePolylineGraph.KindCorner, -1, 0.0);
            InheritFeatureEdge(state, a, b, pinned, chain);
            return pinned;
        }

        // Free midpoint: chord XY, Z re-sampled from the original surface so later-round splits of
        // already-relaxed edges stay exactly on the input surface (a chord of a moved edge is not).
        double z = projection.TryInterpolateZ(chordX, chordY, out double projected) ? projected : chordZ;
        return state.AddVertex(chordX, chordY, z, FeaturePolylineGraph.KindFree, -1, 0.0);
    }

    private static void InheritFeatureEdge(MeshState state, int a, int b, int mid, int chain)
    {
        state.FeatureEdges.Remove(EdgeKey(a, b));
        state.FeatureEdges[EdgeKey(a, mid)] = chain;
        state.FeatureEdges[EdgeKey(mid, b)] = chain;
    }

    private static HashSet<long> CollectFrozenEdges(MeshState state)
    {
        var frozen = new HashSet<long>();
        int faceCount = state.FaceCount;
        for (int t = 0; t < faceCount; t++)
        {
            if (!state.FaceFrozen[t])
                continue;
            int v0 = state.Tris[t * 3], v1 = state.Tris[t * 3 + 1], v2 = state.Tris[t * 3 + 2];
            frozen.Add(EdgeKey(v0, v1));
            frozen.Add(EdgeKey(v1, v2));
            frozen.Add(EdgeKey(v2, v0));
        }

        return frozen;
    }

    // === Phase 2: collapse =============================================================================

    /// <summary>
    /// Collapses edges shorter than 4/5·L, shortest first. The survivor is always the more-constrained
    /// endpoint (feature/corner/frozen keep their exact position); two free endpoints merge at the
    /// midpoint re-projected onto the original surface; two feature endpoints merge only along their own
    /// polyline (mid arc parameter, exact on-polyline position). Guards: link condition, XY inversion
    /// check, no-overlong-result veto, never across different feature polylines, never removing a
    /// corner/frozen vertex, and never a non-feature edge between two feature vertices (which would pinch
    /// two feature lines through one vertex).
    /// </summary>
    private static int CollapseShortEdges(MeshState state, double target, TerrainFaceGrid projection)
    {
        double collapseSquared = target * CollapseFactor * target * CollapseFactor;
        double maxResultSquared = target * SplitFactor * target * SplitFactor;
        int faceCount = state.FaceCount;

        // vertex → incident live faces, vertex → neighbor set
        var vertexFaces = new Dictionary<int, List<int>>();
        var neighbors = new Dictionary<int, HashSet<int>>();
        var candidates = new List<(double lengthSquared, long key)>();
        var seen = new HashSet<long>();
        for (int t = 0; t < faceCount; t++)
        {
            if (!state.IsLive(t))
                continue;
            for (int corner = 0; corner < 3; corner++)
            {
                int u = state.Tris[t * 3 + corner];
                int v = state.Tris[t * 3 + ((corner + 1) % 3)];
                AddVertexFace(vertexFaces, u, t);
                AddNeighborSet(neighbors, u, v);
                AddNeighborSet(neighbors, v, u);
                long key = EdgeKey(u, v);
                if (seen.Add(key))
                {
                    double lengthSquared = DistanceSquared(state.Verts, u, v);
                    if (lengthSquared < collapseSquared)
                        candidates.Add((lengthSquared, key));
                }
            }
        }

        candidates.Sort((x, y) => x.lengthSquared != y.lengthSquared
            ? x.lengthSquared.CompareTo(y.lengthSquared)
            : x.key.CompareTo(y.key));

        var dirty = new HashSet<int>();
        int collapses = 0;
        foreach ((double _, long key) in candidates)
        {
            int a = (int)(key >> 32);
            int b = (int)(key & 0xFFFFFFFFL);
            if (dirty.Contains(a) || dirty.Contains(b))
                continue;
            if (TryCollapse(state, projection, vertexFaces, neighbors, a, b, maxResultSquared, out int survivor, out int removed))
            {
                collapses++;
                dirty.Add(survivor);
                dirty.Add(removed);
                foreach (int n in neighbors[removed])
                    dirty.Add(n);
                foreach (int n in neighbors[survivor])
                    dirty.Add(n);
            }
        }

        if (collapses > 0)
            state.SweepTombstones();

        return collapses;
    }

    private static bool TryCollapse(
        MeshState state,
        TerrainFaceGrid projection,
        Dictionary<int, List<int>> vertexFaces,
        Dictionary<int, HashSet<int>> neighbors,
        int a,
        int b,
        double maxResultSquared,
        out int survivor,
        out int removed)
    {
        survivor = -1;
        removed = -1;

        int rankA = ConstraintRank(state.Kind[a]);
        int rankB = ConstraintRank(state.Kind[b]);
        if (rankA == 2 && rankB == 2)
            return false; // both corner/frozen — never remove either

        long edgeKey = EdgeKey(a, b);
        bool isFeatureEdge = state.FeatureEdges.TryGetValue(edgeKey, out int edgeChain);

        double newX, newY, newZ;
        byte newKind;
        int newChain;
        double newParam;
        if (rankA == 1 && rankB == 1)
        {
            // Two feature vertices merge only along their own polyline via a feature edge.
            if (!isFeatureEdge || edgeChain < 0)
                return false;
            if (state.Chain[a] != edgeChain || state.Chain[b] != edgeChain)
                return false;

            double tm = state.Graph.MidParam(edgeChain, state.Param[a], state.Param[b]);
            state.Graph.Evaluate(edgeChain, tm, out newX, out newY, out newZ);
            survivor = Math.Min(a, b);
            removed = Math.Max(a, b);
            newKind = FeaturePolylineGraph.KindFeature;
            newChain = edgeChain;
            newParam = tm;
        }
        else if (rankA != rankB)
        {
            survivor = rankA > rankB ? a : b;
            removed = rankA > rankB ? b : a;
            // Removing a feature vertex into a free one is excluded by survivor choice; but a feature
            // vertex may only be removed along its own chain (handled above), so block it here too.
            if (state.Kind[removed] != FeaturePolylineGraph.KindFree)
                return false;
            newX = state.Verts[survivor * 3];
            newY = state.Verts[survivor * 3 + 1];
            newZ = state.Verts[survivor * 3 + 2];
            newKind = state.Kind[survivor];
            newChain = state.Chain[survivor];
            newParam = state.Param[survivor];
        }
        else
        {
            // Both free: midpoint XY re-projected onto the original surface. If the projection fails
            // (concave boundary notch), fall back to the surviving endpoint's exact position.
            survivor = Math.Min(a, b);
            removed = Math.Max(a, b);
            double midX = (state.Verts[a * 3] + state.Verts[b * 3]) * 0.5;
            double midY = (state.Verts[a * 3 + 1] + state.Verts[b * 3 + 1]) * 0.5;
            if (projection.TryInterpolateZ(midX, midY, out double projected))
            {
                newX = midX;
                newY = midY;
                newZ = projected;
            }
            else
            {
                newX = state.Verts[survivor * 3];
                newY = state.Verts[survivor * 3 + 1];
                newZ = state.Verts[survivor * 3 + 2];
            }

            newKind = FeaturePolylineGraph.KindFree;
            newChain = -1;
            newParam = 0.0;
        }

        // Link condition: shared neighbors must be exactly the faces on the edge (2 interior, 1 boundary).
        int facesOnEdge = 0;
        List<int> facesOfRemoved = vertexFaces.TryGetValue(removed, out List<int>? rf) ? rf : new List<int>();
        foreach (int t in facesOfRemoved)
        {
            if (!state.IsLive(t))
                continue;
            if (FaceContains(state.Tris, t, survivor))
                facesOnEdge++;
        }

        if (facesOnEdge < 1 || facesOnEdge > 2)
            return false;

        int sharedNeighbors = 0;
        foreach (int n in neighbors[a])
        {
            if (n != a && n != b && neighbors[b].Contains(n))
                sharedNeighbors++;
        }

        if (sharedNeighbors != facesOnEdge)
            return false;

        // Simulate: every surviving face of the merged one-ring must stay CCW in XY and not grow overlong.
        double areaEps = Math.Max(1e-12, maxResultSquared * 1e-9);
        foreach (int t in facesOfRemoved)
        {
            if (!state.IsLive(t) || FaceContains(state.Tris, t, survivor))
                continue;
            if (!SimulatedFaceValid(state, t, removed, newX, newY, areaEps))
                return false;
        }

        List<int> facesOfSurvivor = vertexFaces.TryGetValue(survivor, out List<int>? sf) ? sf : new List<int>();
        foreach (int t in facesOfSurvivor)
        {
            if (!state.IsLive(t) || FaceContains(state.Tris, t, removed))
                continue;
            if (!SimulatedFaceValid(state, t, survivor, newX, newY, areaEps))
                return false;
        }

        foreach (int n in neighbors[removed])
        {
            if (n == survivor || n == removed)
                continue;
            double dx = state.Verts[n * 3] - newX;
            double dy = state.Verts[n * 3 + 1] - newY;
            double dz = state.Verts[n * 3 + 2] - newZ;
            if ((dx * dx) + (dy * dy) + (dz * dz) > maxResultSquared)
                return false;
        }

        // --- Commit -----------------------------------------------------------------------------------
        state.Verts[survivor * 3] = newX;
        state.Verts[survivor * 3 + 1] = newY;
        state.Verts[survivor * 3 + 2] = newZ;
        state.Kind[survivor] = newKind;
        state.Chain[survivor] = newChain;
        state.Param[survivor] = newParam;

        foreach (int t in facesOfRemoved)
        {
            if (!state.IsLive(t))
                continue;
            if (FaceContains(state.Tris, t, survivor))
            {
                state.Tris[t * 3] = -1; // tombstone the two (or one) faces on the collapsed edge
                continue;
            }

            for (int corner = 0; corner < 3; corner++)
            {
                if (state.Tris[t * 3 + corner] == removed)
                    state.Tris[t * 3 + corner] = survivor;
            }

            AddVertexFace(vertexFaces, survivor, t);
        }

        // Re-key feature edges that touched the removed vertex.
        state.FeatureEdges.Remove(edgeKey);
        foreach (int n in neighbors[removed])
        {
            if (n == survivor)
                continue;
            long oldKey = EdgeKey(removed, n);
            if (state.FeatureEdges.TryGetValue(oldKey, out int chain))
            {
                state.FeatureEdges.Remove(oldKey);
                state.FeatureEdges[EdgeKey(survivor, n)] = chain;
            }
        }

        return true;
    }

    private static bool SimulatedFaceValid(MeshState state, int face, int movedVertex, double newX, double newY, double areaEps)
    {
        Span<double> x = stackalloc double[3];
        Span<double> y = stackalloc double[3];
        for (int corner = 0; corner < 3; corner++)
        {
            int v = state.Tris[face * 3 + corner];
            if (v == movedVertex)
            {
                x[corner] = newX;
                y[corner] = newY;
            }
            else
            {
                x[corner] = state.Verts[v * 3];
                y[corner] = state.Verts[v * 3 + 1];
            }
        }

        double area2 = ((x[1] - x[0]) * (y[2] - y[0])) - ((y[1] - y[0]) * (x[2] - x[0]));
        return area2 > areaEps;
    }

    private static int ConstraintRank(byte kind) => kind switch
    {
        FeaturePolylineGraph.KindFree => 0,
        FeaturePolylineGraph.KindFeature => 1,
        _ => 2 // corner or frozen
    };

    // === Phase 3: flips ================================================================================

    /// <summary>
    /// Lawson max-min-angle flips of non-feature interior diagonals (XY angles — the 2.5D quality
    /// notion). Unlike the parked connectivity-only passes there is no near-coplanarity guard: vertices
    /// move and re-project in this loop, re-forming the mesh is the contract, and every structure that
    /// must survive is expressed as a pinned feature or frozen wall.
    /// </summary>
    private static int FlipForQuality(MeshState state)
    {
        int totalFlips = 0;
        double[] vertices = state.Verts.ToArray(); // positions don't change during the flip phase
        for (int sweep = 0; sweep < MaxFlipSweeps; sweep++)
        {
            int faceCount = state.FaceCount;
            var adjacency = new Dictionary<long, (int t0, int o0, int t1, int o1, int count)>(faceCount * 2, IndexedMeshTools.EdgeKeyComparer.Instance);
            for (int t = 0; t < faceCount; t++)
            {
                if (!state.IsLive(t))
                    continue;
                int a = state.Tris[t * 3], b = state.Tris[t * 3 + 1], c = state.Tris[t * 3 + 2];
                AddIncidence(adjacency, a, b, t, c);
                AddIncidence(adjacency, b, c, t, a);
                AddIncidence(adjacency, c, a, t, b);
            }

            var touched = new bool[faceCount];
            int flips = 0;
            foreach (KeyValuePair<long, (int t0, int o0, int t1, int o1, int count)> entry in adjacency)
            {
                (int t0, int o0, int t1, int o1, int count) e = entry.Value;
                if (e.count != 2)
                    continue;
                if (state.FeatureEdges.ContainsKey(entry.Key))
                    continue;
                if (state.FaceFrozen[e.t0] || state.FaceFrozen[e.t1])
                    continue;
                if (touched[e.t0] || touched[e.t1])
                    continue;

                int p = (int)(entry.Key >> 32);
                int q = (int)(entry.Key & 0xFFFFFFFFL);
                int c = e.o0;
                int d = e.o1;

                if (adjacency.ContainsKey(EdgeKey(c, d)))
                    continue;
                if (!QuadIsConvexForFlip(vertices, p, q, c, d))
                    continue;

                double minBefore = Math.Min(MinTriangleAngle(vertices, p, q, c), MinTriangleAngle(vertices, p, q, d));
                double minAfter = Math.Min(MinTriangleAngle(vertices, p, c, d), MinTriangleAngle(vertices, c, q, d));
                if (minAfter <= minBefore + FlipAngleImproveEps)
                    continue;

                WriteOrientedFaceToList(vertices, state.Tris, e.t0, p, c, d);
                WriteOrientedFaceToList(vertices, state.Tris, e.t1, c, q, d);
                touched[e.t0] = true;
                touched[e.t1] = true;
                flips++;
            }

            totalFlips += flips;
            if (flips == 0)
                break;
        }

        return totalFlips;
    }

    private static void WriteOrientedFaceToList(double[] vertices, List<int> tris, int triangle, int p, int q, int r)
    {
        if (Cross2D(vertices, p, q, r) < 0.0)
            (q, r) = (r, q);
        tris[triangle * 3] = p;
        tris[triangle * 3 + 1] = q;
        tris[triangle * 3 + 2] = r;
    }

    // === Phase 4: tangential relaxation + back-projection =============================================

    /// <summary>
    /// One damped Gauss–Seidel sweep. Free vertices move toward their one-ring XY centroid and re-sample
    /// Z from the original surface (exact 2.5D back-projection; the move is reverted entirely when the
    /// new XY has no original face under it). Feature vertices slide 1-D along their polyline, clamped
    /// between their chain neighbors, and take exact on-polyline positions. Corner and frozen vertices
    /// never move. Every move must keep all incident faces CCW in XY, which doubles as the guard that
    /// makes crossing a feature line impossible (the line is always a set of mesh edges — crossing it
    /// would invert a triangle first).
    /// </summary>
    private static int RelaxAndProject(MeshState state, double target, TerrainFaceGrid projection)
    {
        int faceCount = state.FaceCount;
        int vertexCount = state.VertexCount;
        var vertexFaces = new Dictionary<int, List<int>>();
        var neighbors = new Dictionary<int, HashSet<int>>();
        for (int t = 0; t < faceCount; t++)
        {
            if (!state.IsLive(t))
                continue;
            for (int corner = 0; corner < 3; corner++)
            {
                int u = state.Tris[t * 3 + corner];
                int v = state.Tris[t * 3 + ((corner + 1) % 3)];
                AddVertexFace(vertexFaces, u, t);
                AddNeighborSet(neighbors, u, v);
                AddNeighborSet(neighbors, v, u);
            }
        }

        double areaEps = Math.Max(1e-12, target * target * 1e-9);
        int moved = 0;
        for (int v = 0; v < vertexCount; v++)
        {
            if (!neighbors.ContainsKey(v))
                continue; // orphaned by a collapse

            byte kind = state.Kind[v];
            if (kind == FeaturePolylineGraph.KindCorner || kind == FeaturePolylineGraph.KindFrozen)
                continue;

            if (kind == FeaturePolylineGraph.KindFree)
            {
                if (RelaxFreeVertex(state, projection, vertexFaces, neighbors, v, areaEps))
                    moved++;
            }
            else if (RelaxFeatureVertex(state, vertexFaces, neighbors, v, areaEps))
            {
                moved++;
            }
        }

        return moved;
    }

    private static bool RelaxFreeVertex(
        MeshState state,
        TerrainFaceGrid projection,
        Dictionary<int, List<int>> vertexFaces,
        Dictionary<int, HashSet<int>> neighbors,
        int v,
        double areaEps)
    {
        HashSet<int> ring = neighbors[v];
        double cx = 0, cy = 0;
        foreach (int n in ring)
        {
            cx += state.Verts[n * 3];
            cy += state.Verts[n * 3 + 1];
        }

        cx /= ring.Count;
        cy /= ring.Count;

        double px = state.Verts[v * 3];
        double py = state.Verts[v * 3 + 1];
        double dx = (cx - px) * RelaxLambda;
        double dy = (cy - py) * RelaxLambda;
        if ((dx * dx) + (dy * dy) < 1e-24)
            return false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            double newX = px + dx;
            double newY = py + dy;
            if (AllIncidentFacesValid(state, vertexFaces[v], v, newX, newY, areaEps) &&
                projection.TryInterpolateZ(newX, newY, out double newZ))
            {
                state.Verts[v * 3] = newX;
                state.Verts[v * 3 + 1] = newY;
                state.Verts[v * 3 + 2] = newZ;
                return true;
            }

            dx *= 0.5;
            dy *= 0.5;
        }

        return false;
    }

    private static bool RelaxFeatureVertex(
        MeshState state,
        Dictionary<int, List<int>> vertexFaces,
        Dictionary<int, HashSet<int>> neighbors,
        int v,
        double areaEps)
    {
        int chain = state.Chain[v];
        if (chain < 0)
            return false;

        // Chain neighbors = mesh neighbors joined to v by a feature edge of the same chain.
        int n0 = -1, n1 = -1;
        foreach (int n in neighbors[v])
        {
            if (state.FeatureEdges.TryGetValue(EdgeKey(v, n), out int edgeChain) && edgeChain == chain)
            {
                if (n0 < 0)
                    n0 = n;
                else if (n1 < 0)
                    n1 = n;
                else
                    return false; // more than two chain edges — treat as a junction, don't slide
            }
        }

        if (n0 < 0 || n1 < 0)
            return false;
        if (!state.TryGetParam(n0, chain, out double t0) || !state.TryGetParam(n1, chain, out double t1))
            return false;

        FeaturePolylineGraph.Chain chainData = state.Graph.Chains[chain];
        if (chainData.Closed && Math.Abs(t0 - t1) > chainData.TotalLength * 0.5)
            return false; // neighbors straddle the loop seam — skip rather than mis-clamp

        double t = state.Param[v];
        double tTarget = (t0 + t1) * 0.5;
        double tNew = t + ((tTarget - t) * RelaxLambda);
        double lo = Math.Min(t0, t1);
        double hi = Math.Max(t0, t1);
        double margin = (hi - lo) * 1e-3;
        tNew = Math.Clamp(tNew, lo + margin, hi - margin);
        if (Math.Abs(tNew - t) < 1e-12)
            return false;

        state.Graph.Evaluate(chain, tNew, out double newX, out double newY, out double newZ);
        if (!AllIncidentFacesValid(state, vertexFaces[v], v, newX, newY, areaEps))
            return false;

        state.Verts[v * 3] = newX;
        state.Verts[v * 3 + 1] = newY;
        state.Verts[v * 3 + 2] = newZ;
        state.Param[v] = tNew;
        return true;
    }

    private static bool AllIncidentFacesValid(MeshState state, List<int> faces, int movedVertex, double newX, double newY, double areaEps)
    {
        foreach (int t in faces)
        {
            if (!state.IsLive(t))
                continue;
            if (!SimulatedFaceValid(state, t, movedVertex, newX, newY, areaEps))
                return false;
        }

        return true;
    }

    // === Shared helpers ================================================================================

    private static void EmitRefinedTriangle(List<double> verts, List<int> outTris, int v0, int v1, int v2, int m0, int m1, int m2)
    {
        int count = (m0 >= 0 ? 1 : 0) + (m1 >= 0 ? 1 : 0) + (m2 >= 0 ? 1 : 0);
        switch (count)
        {
            case 0:
                Emit(outTris, v0, v1, v2);
                break;

            case 1:
                if (m0 >= 0) { Emit(outTris, v0, m0, v2); Emit(outTris, m0, v1, v2); }
                else if (m1 >= 0) { Emit(outTris, v1, m1, v0); Emit(outTris, m1, v2, v0); }
                else { Emit(outTris, v2, m2, v1); Emit(outTris, m2, v0, v1); }
                break;

            case 2:
                if (m1 < 0) EmitTwoSplit(verts, outTris, v0, v1, v2, m0, m2);
                else if (m2 < 0) EmitTwoSplit(verts, outTris, v1, v2, v0, m1, m0);
                else EmitTwoSplit(verts, outTris, v2, v0, v1, m2, m1);
                break;

            default:
                Emit(outTris, v0, m0, m2);
                Emit(outTris, m0, v1, m1);
                Emit(outTris, m2, m1, v2);
                Emit(outTris, m0, m1, m2);
                break;
        }
    }

    private static void EmitTwoSplit(List<double> verts, List<int> outTris, int a, int b, int c, int mAB, int mCA)
    {
        Emit(outTris, a, mAB, mCA);
        if (DistanceSquared(verts, mAB, c) <= DistanceSquared(verts, b, mCA))
        {
            Emit(outTris, mAB, b, c);
            Emit(outTris, mAB, c, mCA);
        }
        else
        {
            Emit(outTris, mAB, b, mCA);
            Emit(outTris, b, c, mCA);
        }
    }

    private static void Emit(List<int> outTris, int a, int b, int c)
    {
        outTris.Add(a);
        outTris.Add(b);
        outTris.Add(c);
    }

    private static long LongestEdgeKey(List<double> v, int a, int b, int c, out double longestSquared)
    {
        double d0 = DistanceSquared(v, a, b);
        double d1 = DistanceSquared(v, b, c);
        double d2 = DistanceSquared(v, c, a);
        if (d0 >= d1 && d0 >= d2) { longestSquared = d0; return EdgeKey(a, b); }
        if (d1 >= d2) { longestSquared = d1; return EdgeKey(b, c); }
        longestSquared = d2;
        return EdgeKey(c, a);
    }

    private static double DistanceSquared(List<double> v, int a, int b)
    {
        double dx = v[a * 3] - v[b * 3];
        double dy = v[a * 3 + 1] - v[b * 3 + 1];
        double dz = v[a * 3 + 2] - v[b * 3 + 2];
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static bool FaceContains(List<int> tris, int face, int vertex) =>
        tris[face * 3] == vertex || tris[face * 3 + 1] == vertex || tris[face * 3 + 2] == vertex;

    private static void AddVertexFace(Dictionary<int, List<int>> vertexFaces, int v, int face)
    {
        if (!vertexFaces.TryGetValue(v, out List<int>? list))
        {
            list = new List<int>(6);
            vertexFaces.Add(v, list);
        }

        if (!list.Contains(face))
            list.Add(face);
    }

    private static void AddNeighborSet(Dictionary<int, HashSet<int>> neighbors, int v, int n)
    {
        if (!neighbors.TryGetValue(v, out HashSet<int>? set))
        {
            set = new HashSet<int>();
            neighbors.Add(v, set);
        }

        set.Add(n);
    }
}
