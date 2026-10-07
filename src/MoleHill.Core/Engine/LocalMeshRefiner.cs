using static MoleHill.Core.Engine.MeshFlipGeometry;

using MoleHill.Core.Geometry;

namespace MoleHill.Core.Engine;

/// <summary>
/// Connectivity-preserving ("local refine") remesh. Unlike <see cref="SurfaceRemesher"/>, which rebuilds
/// the region from scratch as a global constrained Delaunay triangulation (and so discards grading's
/// surface-aligned topology, chords curved quads the wrong way, and flips across creases), this pass keeps
/// every input edge and only:
/// <list type="number">
/// <item>splits genuinely-coarse triangles in place at edge midpoints (Rivara-style longest-edge
/// bisection with conforming closure), and</item>
/// <item>flips non-feature interior diagonals toward vertex-valence regularity.</item>
/// </list>
/// New vertices are the exact midpoints of existing straight mesh edges, so they sit <b>exactly on the
/// input surface</b> — the refined mesh describes the identical piecewise-linear surface with more, more
/// regular triangles (no off-surface Steiner darts). Feature edges (boundary ∪ creases ∪ constraints) are
/// pinned: never flipped, and their split midpoints stay on the feature line. No vertex ever moves and no
/// vertex is removed — this is a pure retopology, a clean base for a following Smooth.
/// The Remesh modifier now uses <see cref="IsotropicRemesher"/> instead; this refiner remains the engine
/// behind the Sculpt modifier's region-gated DynTopo (which needs the cheap region early-out and the
/// no-vertex-motion guarantee for its displacement-field bookkeeping).
/// </summary>
public static class LocalMeshRefiner
{
    public sealed class Options
    {
        /// <summary>Target triangle edge length; triangles whose longest edge exceeds it are subdivided.</summary>
        public double TargetEdgeLength { get; init; }

        /// <summary>Alternative target expressed as a max triangle area (used when edge length is 0).</summary>
        public double MaxArea { get; init; }

        /// <summary>Auto-detect interior creases folding at least this many degrees and pin them. 0 = off.</summary>
        public double CreaseAngleDeg { get; init; }

        public double Tolerance { get; init; }

        /// <summary>Run the valence-regularizing flip pass after subdivision.</summary>
        public bool DoFlips { get; init; } = true;

        /// <summary>
        /// Optional XY region gate (evaluated at triangle centroids): only triangles whose centroid
        /// passes are subdivided, and flips are skipped when both incident centroids fail. Null =
        /// refine everywhere. Used by the Sculpt modifier's DynTopo to densify only under the
        /// displacement field.
        /// </summary>
        public Func<double, double, bool>? RegionFilter { get; init; }
    }

    public sealed class Result
    {
        public bool Success { get; init; }

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Faces { get; init; } = Array.Empty<int>();

        public string? Warning { get; init; }

        public int AddedVertices { get; init; }

        public int Flips { get; init; }

        /// <summary>
        /// Parent edge of every added vertex, in append order: added vertex (inputVertexCount + k) is
        /// the midpoint of (MidpointParents[k*2], MidpointParents[k*2+1]). Parents always have lower
        /// indices, so dependent values (e.g. a sculpt base Z) can be resolved in order. Lets callers
        /// reconstruct midpoint attributes exactly instead of re-deriving them approximately.
        /// </summary>
        public int[] MidpointParents { get; init; } = Array.Empty<int>();
    }

    // A flip must raise the pair's minimum angle by at least this (radians) to be taken — makes the pass a
    // Lawson/Delaunay quality improvement that strictly converges and can only remove slivers, never add them.
    private const double FlipAngleImproveEps = 1e-3;
    // Crease guard: a flip may not make the local pair fold more than this (in 1-cos units) beyond its
    // current fold — so flips only ever act on near-coplanar (near-tangency) interior diagonals and can't
    // dent a smooth surface. Feature creases are excluded outright.
    private const double FlipCreaseMargin = 0.02;
    private const int MaxSubdivisionRounds = 24;
    private const int MaxFlipSweeps = 16;

    public static Result Refine(
        double[] vertices,
        int[] faces,
        IReadOnlyList<ConstraintPolyline> constraints,
        Options options)
    {
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;
        if (vertexCount == 0 || faceCount == 0)
            return new Result { Success = false, Vertices = vertices, Faces = faces, Warning = "Input mesh has no usable triangles." };

        double target = ResolveTargetEdgeLength(options);

        // Region-limited early-out (the sculpt session calls this per stroke segment): when no
        // in-region triangle is coarse there is nothing to do — skip feature detection and the list
        // copies entirely.
        if (options.RegionFilter != null && target > 0 &&
            !HasCoarseTriangleInRegion(vertices, faces, faceCount, target, options.RegionFilter))
        {
            return new Result { Success = true, Vertices = vertices, Faces = faces };
        }

        // Feature edges are pinned for the whole pass: never flipped, split children stay on the feature.
        var featureEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        var boundarySegments = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(boundarySegments, new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance), faces, faceCount);
        foreach ((int a, int b) in boundarySegments)
            featureEdges.Add(EdgeKey(a, b));

        if (options.CreaseAngleDeg > 0)
        {
            double cosThreshold = Math.Cos(Math.Clamp(options.CreaseAngleDeg, 1.0, 179.0) * Math.PI / 180.0);
            foreach ((int a, int b) in SurfaceRemesher.DetectCreaseEdges(vertices, faces, faceCount, cosThreshold))
                featureEdges.Add(EdgeKey(a, b));
        }

        MarkConstraintFeatureEdges(vertices, vertexCount, faces, faceCount, constraints, Math.Max(options.Tolerance, 1e-6), featureEdges);

        var verts = new List<double>(vertices);
        var tris = new List<int>(faces);

        int addedVertices = 0;
        var midpointParents = new List<int>();
        if (target > 0)
            addedVertices = SubdivideCoarse(verts, ref tris, featureEdges, target, options.RegionFilter, midpointParents);

        double[] outVertices = verts.ToArray();
        int[] outFaces = tris.ToArray();

        int flips = 0;
        if (options.DoFlips)
            flips = RegularizeFlips(outVertices, outFaces, featureEdges, options.RegionFilter);

        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(outFaces, outFaces.Length / 3);
        if (topology.NonManifoldEdgeCount != 0 || topology.HasOpenBoundaryChains)
        {
            return new Result
            {
                Success = false,
                Vertices = vertices,
                Faces = faces,
                Warning = "Local refine produced invalid topology; kept the input mesh unchanged."
            };
        }

        return new Result
        {
            Success = true,
            Vertices = outVertices,
            Faces = outFaces,
            AddedVertices = addedVertices,
            Flips = flips,
            MidpointParents = midpointParents.ToArray()
        };
    }

    /// <summary>Allocation-free scan backing the region early-out: is any triangle whose centroid
    /// passes the filter longer-edged than the target?</summary>
    private static bool HasCoarseTriangleInRegion(
        double[] vertices,
        int[] faces,
        int faceCount,
        double target,
        Func<double, double, bool> regionFilter)
    {
        double targetSquared = target * target;
        for (int t = 0; t < faceCount; t++)
        {
            int v0 = faces[t * 3], v1 = faces[t * 3 + 1], v2 = faces[t * 3 + 2];
            double d0 = EdgeLengthSquared(vertices, v0, v1);
            double d1 = EdgeLengthSquared(vertices, v1, v2);
            double d2 = EdgeLengthSquared(vertices, v2, v0);
            if (d0 <= targetSquared && d1 <= targetSquared && d2 <= targetSquared)
                continue;

            if (regionFilter(
                    (vertices[v0 * 3] + vertices[v1 * 3] + vertices[v2 * 3]) / 3.0,
                    (vertices[v0 * 3 + 1] + vertices[v1 * 3 + 1] + vertices[v2 * 3 + 1]) / 3.0))
                return true;
        }

        return false;
    }

    private static double EdgeLengthSquared(double[] v, int a, int b)
    {
        double dx = v[a * 3] - v[b * 3];
        double dy = v[a * 3 + 1] - v[b * 3 + 1];
        double dz = v[a * 3 + 2] - v[b * 3 + 2];
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static double ResolveTargetEdgeLength(Options options)
    {
        if (options.TargetEdgeLength > 0)
            return options.TargetEdgeLength;

        // Same edge-length ⇄ area mapping the Delaunay remesh uses (equilateral triangle of the max area).
        if (options.MaxArea > 0)
            return Math.Sqrt(options.MaxArea * 4.0 / Math.Sqrt(3.0));

        return 0.0;
    }

    // === Phase 1: adaptive longest-edge subdivision ==================================================

    /// <summary>
    /// Splits coarse triangles (longest edge &gt; <paramref name="target"/>) at edge midpoints, round by
    /// round, until none remain (or a round cap). Conformity is automatic: midpoints are keyed per edge
    /// (<see cref="EdgeKey"/>) and shared by both incident triangles, so a split edge is honoured by
    /// whichever triangles touch it. Fine triangles not adjacent to any split edge are re-emitted verbatim.
    /// </summary>
    private static int SubdivideCoarse(
        List<double> verts,
        ref List<int> tris,
        HashSet<long> featureEdges,
        double target,
        Func<double, double, bool>? regionFilter,
        List<int> midpointParents)
    {
        double targetSquared = target * target;
        int startVertexCount = verts.Count / 3;

        for (int round = 0; round < MaxSubdivisionRounds; round++)
        {
            int faceCount = tris.Count / 3;

            // Mark the longest edge of every coarse triangle. Only longest edges are split, so each split is
            // a well-shaped bisection; a neighbour that shares that edge is closed conformingly by template.
            var marked = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
            for (int t = 0; t < faceCount; t++)
            {
                int v0 = tris[t * 3], v1 = tris[t * 3 + 1], v2 = tris[t * 3 + 2];
                if (regionFilter != null && !regionFilter(
                        (verts[v0 * 3] + verts[v1 * 3] + verts[v2 * 3]) / 3.0,
                        (verts[v0 * 3 + 1] + verts[v1 * 3 + 1] + verts[v2 * 3 + 1]) / 3.0))
                    continue;

                long longest = LongestEdgeKey(verts, v0, v1, v2, out double longestSquared);
                if (longestSquared > targetSquared)
                    marked.Add(longest);
            }

            if (marked.Count == 0)
                break;

            // One midpoint per marked edge, shared by both incident triangles. Midpoint = exact edge
            // midpoint ⇒ exactly on the current surface (the edge is a straight chord of the mesh). A split
            // feature edge hands its flag to both child edges so creases stay pinned through later rounds.
            var midpoints = new Dictionary<long, int>(marked.Count, IndexedMeshTools.EdgeKeyComparer.Instance);
            foreach (long edgeKey in marked)
            {
                int a = (int)(edgeKey >> 32);
                int b = (int)(edgeKey & 0xFFFFFFFFL);
                int m = verts.Count / 3;
                verts.Add((verts[a * 3] + verts[b * 3]) * 0.5);
                verts.Add((verts[a * 3 + 1] + verts[b * 3 + 1]) * 0.5);
                verts.Add((verts[a * 3 + 2] + verts[b * 3 + 2]) * 0.5);
                midpoints[edgeKey] = m;
                midpointParents.Add(a);
                midpointParents.Add(b);

                if (featureEdges.Contains(edgeKey))
                {
                    featureEdges.Add(EdgeKey(a, m));
                    featureEdges.Add(EdgeKey(m, b));
                }
            }

            var next = new List<int>(tris.Count * 2);
            for (int t = 0; t < faceCount; t++)
            {
                int v0 = tris[t * 3], v1 = tris[t * 3 + 1], v2 = tris[t * 3 + 2];
                int m0 = midpoints.TryGetValue(EdgeKey(v0, v1), out int i0) ? i0 : -1;
                int m1 = midpoints.TryGetValue(EdgeKey(v1, v2), out int i1) ? i1 : -1;
                int m2 = midpoints.TryGetValue(EdgeKey(v2, v0), out int i2) ? i2 : -1;
                EmitRefinedTriangle(verts, next, v0, v1, v2, m0, m1, m2);
            }

            tris = next;
        }

        return verts.Count / 3 - startVertexCount;
    }

    /// <summary>
    /// Re-triangulates one parent triangle from the midpoints on its split edges. Every sub-triangle lies
    /// in the parent's plane, so the surface is unchanged. m0/m1/m2 are the midpoints of edges (v0,v1),
    /// (v1,v2), (v2,v0) or -1 when that edge is not split.
    /// </summary>
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
                // Apex = the vertex between the two split edges; the third (unsplit) edge stays whole.
                if (m1 < 0) EmitTwoSplit(verts, outTris, v0, v1, v2, m0, m2);
                else if (m2 < 0) EmitTwoSplit(verts, outTris, v1, v2, v0, m1, m0);
                else EmitTwoSplit(verts, outTris, v2, v0, v1, m2, m1);
                break;

            default: // 3 — regular 1→4
                Emit(outTris, v0, m0, m2);
                Emit(outTris, m0, v1, m1);
                Emit(outTris, m2, m1, v2);
                Emit(outTris, m0, m1, m2);
                break;
        }
    }

    /// <summary>
    /// Two split edges: apex A with edges A-B and C-A split at mAB / mCA (edge B-C whole). Cuts the apex
    /// corner off, then splits the remaining quad (mAB,B,C,mCA) by its shorter diagonal for better shape.
    /// </summary>
    private static void EmitTwoSplit(List<double> verts, List<int> outTris, int a, int b, int c, int mAB, int mCA)
    {
        Emit(outTris, a, mAB, mCA);
        if (Geometry2D.DistanceSquared3(verts, mAB, c) <= Geometry2D.DistanceSquared3(verts, b, mCA))
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
        double d0 = Geometry2D.DistanceSquared3(v, a, b);
        double d1 = Geometry2D.DistanceSquared3(v, b, c);
        double d2 = Geometry2D.DistanceSquared3(v, c, a);
        if (d0 >= d1 && d0 >= d2) { longestSquared = d0; return EdgeKey(a, b); }
        if (d1 >= d2) { longestSquared = d1; return EdgeKey(b, c); }
        longestSquared = d2;
        return EdgeKey(c, a);
    }

    // === Phase 2: valence-regularizing flips =========================================================

    /// <summary>
    /// Flips interior non-feature diagonals to raise triangle quality (Lawson / max-min-angle, the
    /// empty-circumcircle criterion) — this removes the slivers an irregular input TIN carries, which
    /// subdivision alone only shortens. A valence objective was tried first but only regularizes
    /// connectivity, not shape, so slivers survived. The crease guard (<see cref="FlipCreaseMargin"/>)
    /// restricts flips to near-coplanar (near-tangency) diagonals, so cleaning up shape can never facet a
    /// smoothly curved area; feature creases/breaklines are excluded outright. Requiring a strict min-angle
    /// gain makes the pass converge (it can only ever improve). Mutates <paramref name="faces"/> in place.
    /// </summary>
    private static int RegularizeFlips(
        double[] vertices,
        int[] faces,
        HashSet<long> featureEdges,
        Func<double, double, bool>? regionFilter = null)
    {
        int faceCount = faces.Length / 3;
        if (faceCount < 2)
            return 0;

        int totalFlips = 0;
        for (int sweep = 0; sweep < MaxFlipSweeps; sweep++)
        {
            var adjacency = new Dictionary<long, (int t0, int o0, int t1, int o1, int count)>(faceCount * 2, IndexedMeshTools.EdgeKeyComparer.Instance);
            for (int t = 0; t < faceCount; t++)
            {
                int a = faces[t * 3], b = faces[t * 3 + 1], c = faces[t * 3 + 2];
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
                if (featureEdges.Contains(entry.Key))
                    continue;
                if (touched[e.t0] || touched[e.t1])
                    continue;

                int p = (int)(entry.Key >> 32);
                int q = (int)(entry.Key & 0xFFFFFFFFL);
                int c = e.o0;
                int d = e.o1;

                // Region-limited refine: leave diagonals alone when both incident triangles sit outside.
                if (regionFilter != null &&
                    !regionFilter((vertices[p * 3] + vertices[q * 3] + vertices[c * 3]) / 3.0, (vertices[p * 3 + 1] + vertices[q * 3 + 1] + vertices[c * 3 + 1]) / 3.0) &&
                    !regionFilter((vertices[p * 3] + vertices[q * 3] + vertices[d * 3]) / 3.0, (vertices[p * 3 + 1] + vertices[q * 3 + 1] + vertices[d * 3 + 1]) / 3.0))
                    continue;

                // Flipping to c–d would duplicate an existing edge (non-manifold) — skip.
                if (adjacency.ContainsKey(EdgeKey(c, d)))
                    continue;
                if (!QuadIsConvexForFlip(vertices, p, q, c, d))
                    continue;

                // Only take the flip when it strictly improves the pair's worst angle (removes a sliver).
                double minBefore = Math.Min(MinTriangleAngle(vertices, p, q, c), MinTriangleAngle(vertices, p, q, d));
                double minAfter = Math.Min(MinTriangleAngle(vertices, p, c, d), MinTriangleAngle(vertices, c, q, d));
                if (minAfter <= minBefore + FlipAngleImproveEps)
                    continue;

                // Only flip near-coplanar diagonals: the flipped pair may not fold more than the current one.
                double creaseBefore = Crease(vertices, p, q, c, p, q, d);
                double creaseAfter = Crease(vertices, p, c, d, c, q, d);
                if (creaseAfter > creaseBefore + FlipCreaseMargin)
                    continue;

                WriteOrientedFace(vertices, faces, e.t0, p, c, d);
                WriteOrientedFace(vertices, faces, e.t1, c, q, d);
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

    // === Constraint feature-edge marking =============================================================

    /// <summary>
    /// Marks mesh edges that coincide with a constraint polyline segment (a breakline the caller passed in)
    /// as feature edges. Constraint vertices are embedded in the mesh, so each segment maps to an existing
    /// edge via a nearest-vertex lookup; a segment whose endpoints resolve to an adjacent mesh vertex pair
    /// pins that edge.
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
        if (constraints.Count == 0)
            return;

        bool hasWork = false;
        foreach (ConstraintPolyline constraint in constraints)
        {
            if (constraint.PointCount >= 2)
            {
                hasWork = true;
                break;
            }
        }

        if (!hasWork)
            return;

        // Existing undirected mesh edges (so a matched vertex pair is only pinned when it is a real edge).
        var meshEdges = new HashSet<long>(faceCount * 3, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int t = 0; t < faceCount; t++)
        {
            int a = faces[t * 3], b = faces[t * 3 + 1], c = faces[t * 3 + 2];
            meshEdges.Add(EdgeKey(a, b));
            meshEdges.Add(EdgeKey(b, c));
            meshEdges.Add(EdgeKey(c, a));
        }

        var index = new VertexHashGrid(vertices, vertexCount, Math.Max(tolerance * 4.0, 1e-6));

        foreach (ConstraintPolyline constraint in constraints)
        {
            int pointCount = constraint.PointCount;
            if (pointCount < 2)
                continue;

            int previous = index.FindNearest(constraint.Points[0], constraint.Points[1], tolerance);
            for (int i = 1; i < pointCount; i++)
            {
                int current = index.FindNearest(constraint.Points[i * 3], constraint.Points[i * 3 + 1], tolerance);
                TryPinEdge(featureEdges, meshEdges, previous, current);
                previous = current;
            }

            if (constraint.IsClosed)
            {
                int first = index.FindNearest(constraint.Points[0], constraint.Points[1], tolerance);
                TryPinEdge(featureEdges, meshEdges, previous, first);
            }
        }
    }

    private static void TryPinEdge(HashSet<long> featureEdges, HashSet<long> meshEdges, int a, int b)
    {
        if (a < 0 || b < 0 || a == b)
            return;
        long key = EdgeKey(a, b);
        if (meshEdges.Contains(key))
            featureEdges.Add(key);
    }

    /// <summary>Minimal XY spatial hash for nearest-vertex lookup when matching constraint points to mesh vertices.</summary>
    private sealed class VertexHashGrid
    {
        private readonly double[] _vertices;
        private readonly Dictionary<long, List<int>> _cells = new(IndexedMeshTools.CellKeyComparer.Instance);
        private readonly double _inverseCellSize;

        public VertexHashGrid(double[] vertices, int vertexCount, double cellSize)
        {
            _vertices = vertices;
            _inverseCellSize = 1.0 / cellSize;
            for (int i = 0; i < vertexCount; i++)
            {
                long key = CellKey(vertices[i * 3], vertices[i * 3 + 1]);
                if (!_cells.TryGetValue(key, out List<int>? bucket))
                {
                    bucket = new List<int>(2);
                    _cells.Add(key, bucket);
                }

                bucket.Add(i);
            }
        }

        public int FindNearest(double x, double y, double tolerance)
        {
            double bestDistanceSquared = tolerance * tolerance;
            int best = -1;
            long centerX = (long)Math.Floor(x * _inverseCellSize);
            long centerY = (long)Math.Floor(y * _inverseCellSize);
            for (long cy = centerY - 1; cy <= centerY + 1; cy++)
            {
                for (long cx = centerX - 1; cx <= centerX + 1; cx++)
                {
                    if (!_cells.TryGetValue((cx << 32) | (uint)cy, out List<int>? bucket))
                        continue;

                    foreach (int index in bucket)
                    {
                        double dx = _vertices[index * 3] - x;
                        double dy = _vertices[index * 3 + 1] - y;
                        double distanceSquared = (dx * dx) + (dy * dy);
                        if (distanceSquared < bestDistanceSquared)
                        {
                            bestDistanceSquared = distanceSquared;
                            best = index;
                        }
                    }
                }
            }

            return best;
        }

        private long CellKey(double x, double y)
        {
            long cx = (long)Math.Floor(x * _inverseCellSize);
            long cy = (long)Math.Floor(y * _inverseCellSize);
            return (cx << 32) | (uint)cy;
        }
    }
}
