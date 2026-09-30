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
/// collapses never merge across features. Steep retaining-wall faces are frozen against MOTION — never
/// collapsed, flipped, or moved, so every input wall vertex passes through bit-identical and walls can't
/// be buried — but they ARE refined, because bisecting an edge puts the new point exactly ON it and so
/// changes no geometry at all. Denying that pinned the terrain's resolution to the wall's own sampling,
/// and a wall base line coarser than the target left the faces bridging that mismatch collapsing toward
/// zero area, worse on every iteration. Faces frozen by the non-manifold QUARANTINE are a separate mask
/// (<see cref="FeaturePolylineGraph.QuarantinedFaces"/>) and are still never subdivided — splitting a
/// duplicated face duplicates its children too. Wall faces are also excluded from the back-projection
/// grid so near-wall samples always land on the correct terrain side of the crest/toe fold.
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

        /// <summary>
        /// Optional per-INPUT-vertex 4-RoSy cross-field angle θ ∈ [0, π/2) (from
        /// <c>CrossFieldSolver</c>). When set, tangential relaxation becomes field-aligned: the
        /// displacement component ACROSS the local quad direction is damped, so vertices
        /// preferentially slide along field lines and edges straighten into the quad flow — the base
        /// for downstream tri-to-quad pairing. Null = plain isotropic relaxation.
        /// </summary>
        public double[]? FieldTheta { get; init; }

        /// <summary>
        /// Optional cooperative cancellation, consulted between phases, rounds and sweeps and at
        /// bounded intervals inside the long per-face and per-vertex loops. Cancelling throws
        /// <see cref="OperationCanceledException"/>: a remesh abandoned part-way has no valid output,
        /// and throwing keeps a caller from publishing one. Null never cancels.
        /// </summary>
        public Func<bool>? ShouldCancel { get; init; }

        /// <summary>
        /// Optional input edges to keep exactly, as vertex-index pairs: their endpoints never move and the
        /// edges are never split or collapsed away, while everything around them is remeshed normally. A
        /// tile of <see cref="TiledIsotropicRemesher"/> holds its cut so neighbouring tiles still meet vertex
        /// for vertex. Holding whole faces instead left faces held in both passes wherever the two grids'
        /// cuts cross, and a long graded sliver there was never remeshed.
        /// </summary>
        public int[]? HoldEdges { get; init; }

        /// <summary>
        /// Optional surface to project onto instead of the input's own. The tiled remesher projects every
        /// tile of both passes onto the ORIGINAL terrain; projecting the second pass onto the first pass's
        /// output compounded the two approximations (9.6 cm off the input on a graded road).
        /// </summary>
        internal TerrainFaceGrid? Projection { get; init; }

        /// <summary>
        /// Optional per-input-face wall flags, in place of classifying walls by slope. The tiled remesher
        /// classifies once on the original terrain: re-classifying a remeshed mesh froze its steep artifacts
        /// as walls, kept them, and split their edges at chord points off the terrain (up to 0.86 m on the
        /// 1 m park).
        /// </summary>
        internal bool[]? FrozenFaces { get; init; }

        /// <summary>
        /// Optional grid of the original wall faces, used with <see cref="Projection"/>: a feature vertex whose
        /// plan position lies on a wall keeps its chain height, since the terrain surface there is the wall's
        /// top or foot; every other feature vertex takes the original surface's height.
        /// </summary>
        internal TerrainFaceGrid? WallFaces { get; init; }
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

        /// <summary>
        /// Phase timing (milliseconds), for build diagnostics. Settable within Core because the
        /// closing phase is itself timed, so the string cannot exist until the result does.
        /// </summary>
        public string Timing { get; internal set; } = string.Empty;
    }

    // Keep the split/collapse bands disjoint: splitting an edge at this threshold creates two
    // half-edges no shorter than the collapse threshold. The former 4/3 threshold produced edges at
    // 2/3 L that the next collapse pass immediately removed, causing severe operator churn on meshes
    // with mixed local density.
    private const double SplitFactor = 8.0 / 5.0;
    private const double CollapseFactor = 4.0 / 5.0;
    private const double RelaxLambda = 0.5;
    private const double FlipAngleImproveEps = 1e-3;
    // Field-aligned flip objective (retopo only, when a cross-field is present): prefer the diagonal
    // that runs at ~45° to the field (the hypotenuse of an axis-aligned quad), guarded by a hard
    // min-angle floor so a noisy/singular field never carves slivers.
    private const double FlipMinAngleFloorRad = 20.0 * Math.PI / 180.0;
    private const double FlipFieldImproveEps = 0.05; // hysteresis on the [0,1] score → stable fixpoint
    // Split rounds per phase are capped low so extreme anisotropic fans (long thin grading triangles)
    // don't cascade to enormous intermediate face counts before the next collapse phase can coarsen
    // them; the outer iterations provide the remaining rounds where genuinely needed.
    private const int MaxSplitRounds = 4;
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

        CancellationProbe cancellation = CancellationProbe.For(options.ShouldCancel);
        cancellation.ThrowIfCancelled();

        long tsMethod = System.Diagnostics.Stopwatch.GetTimestamp();

        long tsStart = tsMethod;
        var graph = FeaturePolylineGraph.Build(
            vertices, faces, faceCount, constraints, options.CreaseAngleDeg, options.WallFaceMinSlopeDeg, options.Tolerance,
            minCreaseChainLength: options.TargetEdgeLength * 3.0,
            heldVertices: HeldVertexMask(options.HoldEdges, vertexCount),
            frozenFaces: options.FrozenFaces);
        double msGraph = System.Diagnostics.Stopwatch.GetElapsedTime(tsStart).TotalMilliseconds;

        // Back-projection grid over the ORIGINAL mesh with wall faces excluded: a near-vertical wall is
        // an XY sliver, so sampling it would return a mid-wall Z and smear the wall onto the terrain.
        // Cell size = half the target so queries in dense graded corridors stay near-constant time.
        long tsSetup = System.Diagnostics.Stopwatch.GetTimestamp();
        TerrainFaceGrid? projection = options.Projection ?? BuildProjectionGrid(
            vertices, faces, faceCount, graph.FrozenFaces, options.TargetEdgeLength * 0.5);
        if (projection is null)
            return new Result { Success = true, Vertices = vertices, Faces = faces, Warning = "All faces are steep (frozen); nothing to remesh." };
        double msGrid = System.Diagnostics.Stopwatch.GetElapsedTime(tsSetup).TotalMilliseconds;

        long tsState = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = new MeshState(vertices, faces, graph);
        if (options.Projection != null)
        {
            state.FeatureSurface = projection;
            state.FeatureWalls = options.WallFaces;
        }

        if (options.HoldEdges is { Length: > 1 } holdEdges)
        {
            for (int i = 0; i + 1 < holdEdges.Length; i += 2)
                state.HeldEdges.Add(EdgeKey(holdEdges[i], holdEdges[i + 1]));
        }
        // The sampler gets its own grid over the FULL input mesh: the projection grid excludes wall
        // faces and renumbers the survivors, so its face indices don't match the theta array's mesh.
        state.Field = options.FieldTheta != null && options.FieldTheta.Length == vertexCount
            ? new FieldSampler(faces, options.FieldTheta,
                new TerrainFaceGrid(vertices, vertexCount, faces, faceCount, options.TargetEdgeLength * 0.5))
            : null;

        // Imperfect upstream grading can hand us a mesh that is already non-manifold or has open
        // chains. The acceptance gate is therefore relative: the output must be no WORSE than the
        // input (the sick edges themselves are pinned by the feature graph and pass through).
        double msState = System.Diagnostics.Stopwatch.GetElapsedTime(tsState).TotalMilliseconds;

        long tsTopology = System.Diagnostics.Stopwatch.GetTimestamp();
        var inputTopology = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        double msTopology = System.Diagnostics.Stopwatch.GetElapsedTime(tsTopology).TotalMilliseconds;

        double target = options.TargetEdgeLength;
        int totalSplits = 0, totalCollapses = 0, totalFlips = 0, totalRelaxed = 0;
        double msSplit = 0, msCollapse = 0, msFlip = 0, msRelax = 0;
        int iterations = Math.Max(1, options.Iterations);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            // Split before collapse so refinement and the coarsening it enables settle within the same
            // outer round. The disjoint 1.6 L / 0.8 L thresholds keep a split from immediately creating
            // short half-edges, avoiding the split/collapse oscillation this order used to trigger.
            cancellation.ThrowIfCancelled();
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            int splits = SplitLongEdges(state, target * SplitFactor, projection, cancellation);
            msSplit += System.Diagnostics.Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            cancellation.ThrowIfCancelled();
            ts = System.Diagnostics.Stopwatch.GetTimestamp();
            int collapses = CollapseShortEdges(state, target, projection, cancellation);
            msCollapse += System.Diagnostics.Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            cancellation.ThrowIfCancelled();
            ts = System.Diagnostics.Stopwatch.GetTimestamp();
            int flips = FlipForQuality(state, cancellation);
            msFlip += System.Diagnostics.Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            cancellation.ThrowIfCancelled();
            ts = System.Diagnostics.Stopwatch.GetTimestamp();
            int relaxed = RelaxAndProject(state, target, projection, cancellation);
            msRelax += System.Diagnostics.Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

            totalSplits += splits;
            totalCollapses += collapses;
            totalFlips += flips;
            totalRelaxed += relaxed;
            if (splits == 0 && collapses == 0 && flips == 0 && relaxed == 0)
                break;
        }

        // Every interval, with an explicit remainder rather than a sum that hides the gaps. The first
        // profile of this method reported only the four operator phases and the graph, which left 32%
        // of the stage unattributed and unrankable. "finish" is ToResult: compaction plus a second
        // boundary-graph analysis. "setup" is the projection grid, the MeshState copy and the input
        // topology analysis — all whole-mesh passes made before any operator runs.
        long tsFinish = System.Diagnostics.Stopwatch.GetTimestamp();
        Result result = state.ToResult(
            vertices, faces, inputTopology, totalSplits, totalCollapses, totalFlips, totalRelaxed, timing: string.Empty);
        double msFinish = System.Diagnostics.Stopwatch.GetElapsedTime(tsFinish).TotalMilliseconds;

        double msTotal = System.Diagnostics.Stopwatch.GetElapsedTime(tsMethod).TotalMilliseconds;
        double msOther = msTotal -
            (msGraph + msGrid + msState + msTopology + msSplit + msCollapse + msFlip + msRelax + msFinish);
        string timing =
            $"graph {msGraph:0} ms, grid {msGrid:0} ms, state {msState:0} ms, topology {msTopology:0} ms, " +
            $"split {msSplit:0} ms, collapse {msCollapse:0} ms, flip {msFlip:0} ms, relax {msRelax:0} ms, " +
            $"finish {msFinish:0} ms, other {msOther:0} ms, total {msTotal:0} ms";
        result.Timing = timing;
        return result;
    }

    /// <summary>
    /// Estimates the equilateral edge length that represents the input mesh's current plan-area per
    /// face. Unlike a median edge, this keeps the requested global density stable when a terrain mixes
    /// densely sampled features with large, sparse outer faces.
    /// </summary>
    public static double EstimateFaceCountPreservingTarget(double[] vertices, int[] faces)
    {
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;
        if (vertexCount == 0 || faceCount == 0)
            return 0.0;

        double planArea = 0.0;
        int validFaceCount = 0;
        for (int face = 0; face < faceCount; face++)
        {
            int a = faces[face * 3];
            int b = faces[face * 3 + 1];
            int c = faces[face * 3 + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                continue;

            double ax = vertices[a * 3];
            double ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3];
            double by = vertices[b * 3 + 1];
            double cx = vertices[c * 3];
            double cy = vertices[c * 3 + 1];
            double twiceArea = Math.Abs(((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax)));
            if (!double.IsFinite(twiceArea) || twiceArea <= 0.0)
                continue;

            planArea += twiceArea * 0.5;
            validFaceCount++;
        }

        if (!double.IsFinite(planArea) || planArea <= 0.0 || validFaceCount == 0)
            return 0.0;

        return Math.Sqrt((4.0 * planArea) / (Math.Sqrt(3.0) * validFaceCount));
    }

    /// <summary>
    /// Samples the per-input-vertex 4-RoSy angle at any XY by barycentric interpolation in
    /// (cos 4θ, sin 4θ) space — never raw θ, which is discontinuous across the π/2 symmetry. Near a
    /// field singularity the interpolated vector vanishes; the caller's fallback angle is returned.
    /// </summary>
    internal sealed class FieldSampler
    {
        private readonly int[] _faces;
        private readonly double[] _cos4;
        private readonly double[] _sin4;
        private readonly TerrainFaceGrid _grid;

        public FieldSampler(int[] faces, double[] theta, TerrainFaceGrid grid)
        {
            _faces = faces;
            _grid = grid;
            _cos4 = new double[theta.Length];
            _sin4 = new double[theta.Length];
            for (int i = 0; i < theta.Length; i++)
            {
                _cos4[i] = Math.Cos(4.0 * theta[i]);
                _sin4[i] = Math.Sin(4.0 * theta[i]);
            }
        }

        public double SampleTheta(double x, double y, double fallback)
        {
            if (!_grid.TryFindFace(x, y, out int face, out double w0, out double w1, out double w2))
                return fallback;

            int i0 = _faces[face * 3], i1 = _faces[face * 3 + 1], i2 = _faces[face * 3 + 2];
            double c = (w0 * _cos4[i0]) + (w1 * _cos4[i1]) + (w2 * _cos4[i2]);
            double s = (w0 * _sin4[i0]) + (w1 * _sin4[i1]) + (w2 * _sin4[i2]);
            if ((c * c) + (s * s) < 1e-6)
                return fallback; // singularity — no reliable direction here

            double theta = Math.Atan2(s, c) / 4.0;
            if (theta < 0)
                theta += Math.PI / 2.0;
            return theta;
        }
    }

    internal static TerrainFaceGrid? BuildProjectionGrid(double[] vertices, int[] faces, int faceCount, bool[] frozenFaces, double cellSizeHint)
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
            return new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faceCount, cellSizeHint);

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

        return new TerrainFaceGrid(vertices, vertices.Length / 3, activeFaces, activeCount, cellSizeHint);
    }

    // === Working state =================================================================================

    /// <summary>
    /// Growable mesh + per-vertex classification, shared by all four operator phases. Faces removed by
    /// collapses are tombstoned (first index -1) and swept out at the end of the collapse phase; vertices
    /// orphaned by collapses stay in the arrays and are dropped by the final compaction.
    /// </summary>
    internal sealed class MeshState
    {
        public readonly List<double> Verts;
        public readonly List<int> Tris;
        public readonly List<bool> FaceFrozen;

        /// <summary>Parallel to <see cref="FaceFrozen"/>: frozen because non-manifold, never subdividable.</summary>
        public readonly List<bool> FaceQuarantined;
        public readonly List<byte> Kind;
        public readonly List<int> Chain;
        public readonly List<double> Param;
        public readonly Dictionary<long, int> FeatureEdges;
        public readonly FeaturePolylineGraph Graph;

        /// <summary>Edges no operator may split or collapse away (<see cref="Options.HoldEdges"/>). Their endpoints are frozen.</summary>
        public readonly HashSet<long> HeldEdges = IndexedMeshTools.CreateEdgeKeySet(0);

        /// <summary>
        /// The original surface, when this mesh is not it (<see cref="Options.Projection"/>). A chain built
        /// from an already-remeshed mesh is a chord of the original feature, and after the first tiled pass it
        /// can be a remeshed edge that crosses a real crease diagonally, so a vertex placed on it can sit well
        /// off the terrain (0.8 m on the 1 m park). Its height is taken from this surface instead, except over
        /// a wall (<see cref="FeatureWalls"/>), where the surface's height is the wall's top or foot.
        /// </summary>
        public TerrainFaceGrid? FeatureSurface;

        public TerrainFaceGrid? FeatureWalls;

        public void EvaluateFeature(int chain, double t, out double x, out double y, out double z)
        {
            Graph.Evaluate(chain, t, out x, out y, out z);
            if (FeatureSurface != null &&
                (FeatureWalls == null || !FeatureWalls.TryFindFace(x, y, out _, out _, out _, out _)) &&
                FeatureSurface.TryInterpolateZ(x, y, out double surfaceZ))
            {
                z = surfaceZ;
            }
        }

        /// <summary>Cross-field sampler over the ORIGINAL mesh; null = plain isotropic relaxation.</summary>
        public FieldSampler? Field;

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
            FaceQuarantined = new List<bool>(graph.QuarantinedFaces);
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
                    FaceQuarantined[write] = FaceQuarantined[t];
                }

                write++;
            }

            Tris.RemoveRange(write * 3, (count - write) * 3);
            FaceFrozen.RemoveRange(write, count - write);
            FaceQuarantined.RemoveRange(write, count - write);
        }

        public Result ToResult(
            double[] inputVertices,
            int[] inputFaces,
            MeshTopologyValidator.BoundaryGraphAnalysis inputTopology,
            int splits,
            int collapses,
            int flips,
            int relaxed,
            string timing)
        {
            var faces = Tris.ToArray();
            var compaction = IndexedMeshTools.Compact(VertexCount, faces, faces.Length / 3);
            double[] outVertices = IndexedMeshTools.CompactDoubleData(Verts.ToArray(), 3, compaction.NewToOld, compaction.VertexCount);

            // Relative gate: never accept an output sicker than the input (non-manifold edges that
            // were already there are pinned and pass through unchanged).
            var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(compaction.Faces, compaction.FaceCount);
            bool worseNonManifold = topology.NonManifoldEdgeCount > inputTopology.NonManifoldEdgeCount;
            bool worseOpenChains = topology.HasOpenBoundaryChains && !inputTopology.HasOpenBoundaryChains;
            if (worseNonManifold || worseOpenChains)
            {
                return new Result
                {
                    Success = false,
                    Vertices = inputVertices,
                    Faces = inputFaces,
                    Warning = "Isotropic remesh produced invalid topology " +
                        $"(non-manifold {inputTopology.NonManifoldEdgeCount} -> {topology.NonManifoldEdgeCount}, " +
                        $"open chains {inputTopology.HasOpenBoundaryChains} -> {topology.HasOpenBoundaryChains}); " +
                        "kept the input mesh unchanged.",
                    Timing = timing
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
                FrozenFaces = FaceFrozen.ToArray(),
                Timing = timing
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
    internal static int SplitLongEdges(MeshState state, double threshold, TerrainFaceGrid projection)
    {
        return SplitLongEdges(state, threshold, projection, CancellationProbe.None);
    }

    internal static int SplitLongEdges(MeshState state, double threshold, TerrainFaceGrid projection, CancellationProbe cancellation)
    {
        double thresholdSquared = threshold * threshold;
        int added = 0;

        for (int round = 0; round < MaxSplitRounds; round++)
        {
            cancellation.ThrowIfCancelled();
            int faceCount = state.FaceCount;
            var quarantinedEdges = CollectQuarantinedEdges(state);
            var wallEdges = CollectWallEdges(state);

            var marked = new List<long>();
            var markedSet = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
            for (int t = 0; t < faceCount; t++)
            {
                cancellation.ThrowIfCancelledOften();
                // Only the QUARANTINE blocks subdivision. A wall is healthy geometry that must not move,
                // and bisecting it moves nothing; a non-manifold face's children are duplicated too.
                if (state.FaceQuarantined[t])
                    continue;
                int v0 = state.Tris[t * 3], v1 = state.Tris[t * 3 + 1], v2 = state.Tris[t * 3 + 2];
                long longest = LongestEdgeKey(state.Verts, v0, v1, v2, out double longestSquared);
                if (state.HeldEdges.Count > 0 && state.HeldEdges.Contains(longest))
                    longest = LongestUnheldEdgeKey(state, v0, v1, v2, out longestSquared);
                if (longest < 0 || longestSquared <= thresholdSquared || quarantinedEdges.Contains(longest))
                    continue;
                // Chainless pinned features are contained sickness (non-manifold edges from imperfect
                // upstream welds): splitting one would double its non-manifold count. Leave it alone.
                if (state.FeatureEdges.TryGetValue(longest, out int longestChain) && longestChain < 0)
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
                midpoints[edgeKey] = CreateMidpoint(state, projection, wallEdges, edgeKey, a, b);
                added++;
            }

            var next = new List<int>(state.Tris.Count * 2);
            var nextFrozen = new List<bool>(state.FaceFrozen.Count * 2);
            var nextQuarantined = new List<bool>(state.FaceQuarantined.Count * 2);
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
                {
                    nextFrozen.Add(state.FaceFrozen[t]);
                    nextQuarantined.Add(state.FaceQuarantined[t]);
                }
            }

            state.Tris.Clear();
            state.Tris.AddRange(next);
            state.FaceFrozen.Clear();
            state.FaceFrozen.AddRange(nextFrozen);
            state.FaceQuarantined.Clear();
            state.FaceQuarantined.AddRange(nextQuarantined);
        }

        return added;
    }

    private static int CreateMidpoint(
        MeshState state, TerrainFaceGrid projection, HashSet<long> wallEdges, long edgeKey, int a, int b)
    {
        double chordX = (state.Verts[a * 3] + state.Verts[b * 3]) * 0.5;
        double chordY = (state.Verts[a * 3 + 1] + state.Verts[b * 3 + 1]) * 0.5;
        double chordZ = (state.Verts[a * 3 + 2] + state.Verts[b * 3 + 2]) * 0.5;

        // A wall edge is pinned so the wall is never MOVED — but a point bisecting it lies exactly on the
        // edge, so inserting one moves nothing and buries nothing. The midpoint takes the exact chord,
        // never the back-projected surface (which would smear a near-vertical wall onto the terrain), and
        // is itself frozen so no later phase can drift it off the line.
        if (wallEdges.Contains(edgeKey))
        {
            int wallMid = state.AddVertex(chordX, chordY, chordZ, FeaturePolylineGraph.KindFrozen, -1, 0.0);
            if (state.FeatureEdges.TryGetValue(edgeKey, out int wallChain))
                InheritFeatureEdge(state, a, b, wallMid, wallChain);
            return wallMid;
        }

        if (state.FeatureEdges.TryGetValue(edgeKey, out int chain))
        {
            if (chain >= 0 &&
                state.TryGetParam(a, chain, out double ta) &&
                state.TryGetParam(b, chain, out double tb))
            {
                double tm = state.Graph.MidParam(chain, ta, tb);
                state.EvaluateFeature(chain, tm, out double px, out double py, out double pz);
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

    /// <summary>
    /// Every edge of a frozen face. A midpoint on one of these must take the exact chord and stay pinned:
    /// it lies on the wall, and back-projecting it would sample the terrain grid instead.
    /// </summary>
    private static HashSet<long> CollectWallEdges(MeshState state)
    {
        var wall = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        int faceCount = state.FaceCount;
        for (int t = 0; t < faceCount; t++)
        {
            if (!state.FaceFrozen[t])
                continue;
            int v0 = state.Tris[t * 3], v1 = state.Tris[t * 3 + 1], v2 = state.Tris[t * 3 + 2];
            wall.Add(EdgeKey(v0, v1));
            wall.Add(EdgeKey(v1, v2));
            wall.Add(EdgeKey(v2, v0));
        }

        return wall;
    }

    private static HashSet<long> CollectQuarantinedEdges(MeshState state)
    {
        var frozen = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        int faceCount = state.FaceCount;
        for (int t = 0; t < faceCount; t++)
        {
            if (!state.FaceQuarantined[t])
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
    /// <summary>
    /// Runs collapse rounds until no short edge can be collapsed (each round only takes an
    /// independent set — the one-ring locks — so dense regions need several rounds to fully coarsen;
    /// stopping after one would let the split phase outrun collapsing on fan-heavy grading output).
    /// </summary>
    internal static int CollapseShortEdges(MeshState state, double target, TerrainFaceGrid projection)
    {
        return CollapseShortEdges(state, target, projection, CancellationProbe.None);
    }

    internal static int CollapseShortEdges(MeshState state, double target, TerrainFaceGrid projection, CancellationProbe cancellation)
    {
        const int maxRounds = 8;
        int total = 0;
        MeshVertexAdjacency? adjacency = null;

        // Round scratch, allocated once. A round's candidate list and its locked-vertex set are both
        // whole-mesh sized on a dense terrain, and there are up to maxRounds of them.
        var candidates = new List<(double lengthSquared, long key)>();
        var locked = new CollapseRoundLocks(state.VertexCount);

        for (int round = 0; round < maxRounds; round++)
        {
            cancellation.ThrowIfCancelled();
            int collapses = CollapseShortEdgesRound(state, target, projection, ref adjacency, candidates, locked, cancellation);
            total += collapses;
            if (collapses == 0)
                break;
        }

        return total;
    }

    /// <summary>
    /// Per-round one-ring locks for the collapse phase. A round takes an independent set, so a vertex is
    /// either free or locked - membership only, never enumerated. A stamped array reused across rounds
    /// replaces a fresh <see cref="HashSet{T}"/> per round without changing which edges a round accepts.
    /// </summary>
    private sealed class CollapseRoundLocks
    {
        private int[] _stamp;
        private int _round;

        public CollapseRoundLocks(int vertexCount)
        {
            _stamp = new int[Math.Max(vertexCount, 1)];
        }

        public void BeginRound(int vertexCount)
        {
            if (_stamp.Length < vertexCount)
            {
                _stamp = new int[vertexCount];
                _round = 0;
            }
            else if (_round == int.MaxValue)
            {
                Array.Clear(_stamp);
                _round = 0;
            }

            _round++;
        }

        public bool IsLocked(int vertex) => vertex >= 0 && vertex < _stamp.Length && _stamp[vertex] == _round;

        public void Lock(int vertex)
        {
            if (vertex >= 0 && vertex < _stamp.Length)
                _stamp[vertex] = _round;
        }
    }

    private static int CollapseShortEdgesRound(
        MeshState state,
        double target,
        TerrainFaceGrid projection,
        ref MeshVertexAdjacency? adjacency,
        List<(double lengthSquared, long key)> candidates,
        CollapseRoundLocks locked,
        CancellationProbe cancellation)
    {
        double collapseSquared = target * CollapseFactor * target * CollapseFactor;
        double maxResultSquared = target * SplitFactor * target * SplitFactor;
        int faceCount = state.FaceCount;

        // vertex → incident live faces, vertex → neighbor set (flat CSR, reused across rounds)
        adjacency = MeshVertexAdjacency.Build(state.Tris, faceCount, state.VertexCount, adjacency);

        // Each undirected edge is visited once, from its lower-indexed endpoint only — that replaces
        // the per-round HashSet<long> of already-seen edge keys. Edge keys are unique, so the sorted list
        // does not depend on the order the scan found them in, and a large mesh scans in parallel.
        candidates.Clear();
        MeshVertexAdjacency scanAdjacency = adjacency;
        int scanVertexCount = scanAdjacency.VertexCount;
        if (scanVertexCount >= CollapseScanParallelMinimum)
        {
            int blockCount = (scanVertexCount + CollapseScanBlock - 1) / CollapseScanBlock;
            var found = new List<(double lengthSquared, long key)>?[blockCount];
            System.Threading.Tasks.Parallel.For(0, blockCount, block =>
            {
                var local = new List<(double lengthSquared, long key)>();
                CollectCollapseCandidates(state, scanAdjacency, block * CollapseScanBlock,
                    Math.Min(scanVertexCount, (block + 1) * CollapseScanBlock), collapseSquared, local);
                found[block] = local;
            });
            cancellation.ThrowIfCancelled();
            foreach (List<(double lengthSquared, long key)>? local in found)
                candidates.AddRange(local!);
        }
        else
        {
            CollectCollapseCandidates(state, scanAdjacency, 0, scanVertexCount, collapseSquared, candidates);
        }

        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidates).Sort(default(CollapseCandidateOrder));

        // Candidates are planned a chunk at a time, in parallel, then committed in order. A plan reads only
        // the one-rings of the edge's endpoints, and every collapse locks the one-rings it changes, so a
        // candidate still unlocked when its turn comes plans the same on the chunk-start state as it would
        // at its turn: the result is the sequential one. Planning was most of the phase, and it is mostly
        // memory latency (shortest-first visits the mesh in no spatial order), so it parallelizes well.
        locked.BeginRound(state.VertexCount);
        int collapses = 0;
        MeshVertexAdjacency roundAdjacency = adjacency;
        int candidateCount = candidates.Count;
        CollapsePlan[] plans = RentCollapsePlans(Math.Min(candidateCount, CollapsePlanChunk));
        for (int chunkStart = 0; chunkStart < candidateCount; chunkStart += CollapsePlanChunk)
        {
            cancellation.ThrowIfCancelled();
            int chunkEnd = Math.Min(candidateCount, chunkStart + CollapsePlanChunk);
            int chunkLength = chunkEnd - chunkStart;
            if (chunkLength >= CollapsePlanParallelMinimum)
            {
                int blockCount = (chunkLength + CollapsePlanBlock - 1) / CollapsePlanBlock;
                System.Threading.Tasks.Parallel.For(0, blockCount, block =>
                {
                    int blockEnd = Math.Min(chunkEnd, chunkStart + ((block + 1) * CollapsePlanBlock));
                    for (int i = chunkStart + (block * CollapsePlanBlock); i < blockEnd; i++)
                        PlanCandidate(state, projection, roundAdjacency, candidates[i].key, locked, maxResultSquared, out plans[i - chunkStart]);
                });
            }
            else
            {
                for (int i = chunkStart; i < chunkEnd; i++)
                    PlanCandidate(state, projection, roundAdjacency, candidates[i].key, locked, maxResultSquared, out plans[i - chunkStart]);
            }

            for (int i = 0; i < chunkLength; i++)
            {
                ref CollapsePlan plan = ref plans[i];
                if (!plan.Accepted || locked.IsLocked(plan.Survivor) || locked.IsLocked(plan.Removed))
                    continue;

                CommitCollapse(state, roundAdjacency, in plan);
                collapses++;
                locked.Lock(plan.Survivor);
                locked.Lock(plan.Removed);
                foreach (int n in roundAdjacency.NeighborsOf(plan.Removed))
                    locked.Lock(n);
                foreach (int n in roundAdjacency.NeighborsOf(plan.Survivor))
                    locked.Lock(n);
            }
        }

        if (collapses > 0)
            state.SweepTombstones();

        return collapses;
    }

    private const int CollapseScanParallelMinimum = 65_536;
    private const int CollapseScanBlock = 16_384;

    private static void CollectCollapseCandidates(
        MeshState state,
        MeshVertexAdjacency adjacency,
        int fromVertex,
        int toVertex,
        double collapseSquared,
        List<(double lengthSquared, long key)> candidates)
    {
        for (int u = fromVertex; u < toVertex; u++)
        {
            foreach (int v in adjacency.NeighborsOf(u))
            {
                if (v < u)
                    continue;
                double lengthSquared = DistanceSquared(state.Verts, u, v);
                if (lengthSquared < collapseSquared)
                    candidates.Add((lengthSquared, EdgeKey(u, v)));
            }
        }
    }

    /// <summary>Shortest first, ties by edge key: a total order, since keys are unique.</summary>
    private readonly struct CollapseCandidateOrder : IComparer<(double lengthSquared, long key)>
    {
        public int Compare((double lengthSquared, long key) x, (double lengthSquared, long key) y) =>
            x.lengthSquared != y.lengthSquared
                ? x.lengthSquared.CompareTo(y.lengthSquared)
                : x.key.CompareTo(y.key);
    }

    private const int CollapsePlanChunk = 65_536;
    private const int CollapsePlanBlock = 2_048;
    private const int CollapsePlanParallelMinimum = 8_192;

    [ThreadStatic]
    private static CollapsePlan[]? t_collapsePlans;

    private static CollapsePlan[] RentCollapsePlans(int length)
    {
        CollapsePlan[]? plans = t_collapsePlans;
        if (plans == null || plans.Length < length)
            t_collapsePlans = plans = new CollapsePlan[Math.Max(length, 16)];
        return plans;
    }

    /// <summary>What committing one collapse writes, decided before anything is written.</summary>
    private struct CollapsePlan
    {
        public bool Accepted;
        public int Survivor;
        public int Removed;
        public long EdgeKey;
        public double X;
        public double Y;
        public double Z;
        public double Param;
        public int Chain;
        public byte Kind;
    }

    private static void PlanCandidate(
        MeshState state,
        TerrainFaceGrid projection,
        MeshVertexAdjacency adjacency,
        long key,
        CollapseRoundLocks locked,
        double maxResultSquared,
        out CollapsePlan plan)
    {
        int a = (int)(key >> 32);
        int b = (int)(key & 0xFFFFFFFFL);
        if (locked.IsLocked(a) || locked.IsLocked(b))
        {
            plan = default;
            return;
        }

        plan.Accepted = TryPlanCollapse(state, projection, adjacency, a, b, maxResultSquared, out plan);
    }

    private static bool TryPlanCollapse(
        MeshState state,
        TerrainFaceGrid projection,
        MeshVertexAdjacency adjacency,
        int a,
        int b,
        double maxResultSquared,
        out CollapsePlan plan)
    {
        plan = default;
        int survivor;
        int removed;

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
            state.EvaluateFeature(edgeChain, tm, out newX, out newY, out newZ);
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
        MeshVertexAdjacency.FaceEnumerable facesOfRemoved = adjacency.FacesOf(removed);
        foreach (int t in facesOfRemoved)
        {
            if (!state.IsLive(t))
                continue;
            if (FaceContains(state.Tris, t, survivor))
                facesOnEdge++;
        }

        if (facesOnEdge < 1 || facesOnEdge > 2)
            return false;

        // A face this collapse would remove must not carry a held edge: the edge would go with it.
        if (state.HeldEdges.Count > 0)
        {
            foreach (int t in facesOfRemoved)
            {
                if (state.IsLive(t) && FaceContains(state.Tris, t, survivor) && FaceHasHeldEdge(state, t))
                    return false;
            }
        }

        int sharedNeighbors = 0;
        foreach (int n in adjacency.NeighborsOf(a))
        {
            if (n != a && n != b && adjacency.NeighborsContain(b, n))
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

        foreach (int t in adjacency.FacesOf(survivor))
        {
            if (!state.IsLive(t) || FaceContains(state.Tris, t, removed))
                continue;
            if (!SimulatedFaceValid(state, t, survivor, newX, newY, areaEps))
                return false;
        }

        foreach (int n in adjacency.NeighborsOf(removed))
        {
            if (n == survivor || n == removed)
                continue;
            double dx = state.Verts[n * 3] - newX;
            double dy = state.Verts[n * 3 + 1] - newY;
            // XY footprint only (no dz): a narrow high-relief sliver's merged edge is long in Z but
            // small in plan, so a 3D cap would veto exactly the collapses that coarsen pinched batter
            // ridges (e.g. off wall ends). The collapse candidate gate is still 3D (CollapseShortEdgesRound,
            // length² < (0.8·L)²), which confines this relaxation to already-over-refined regions —
            // broad steep slopes sit at 3D ≈ L and are never candidates, so they are never decimated.
            if ((dx * dx) + (dy * dy) > maxResultSquared)
                return false;
        }

        plan.Survivor = survivor;
        plan.Removed = removed;
        plan.EdgeKey = edgeKey;
        plan.X = newX;
        plan.Y = newY;
        plan.Z = newZ;
        plan.Kind = newKind;
        plan.Chain = newChain;
        plan.Param = newParam;
        return true;
    }

    private static void CommitCollapse(MeshState state, MeshVertexAdjacency adjacency, in CollapsePlan plan)
    {
        int survivor = plan.Survivor;
        int removed = plan.Removed;
        state.Verts[survivor * 3] = plan.X;
        state.Verts[survivor * 3 + 1] = plan.Y;
        state.Verts[survivor * 3 + 2] = plan.Z;
        state.Kind[survivor] = plan.Kind;
        state.Chain[survivor] = plan.Chain;
        state.Param[survivor] = plan.Param;

        foreach (int t in adjacency.FacesOf(removed))
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

            adjacency.AddFace(survivor, t);
        }

        // Re-key feature edges that touched the removed vertex.
        state.FeatureEdges.Remove(plan.EdgeKey);
        foreach (int n in adjacency.NeighborsOf(removed))
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
    }

    /// <summary>The longest edge of a face that is not held, or -1 when all three are.</summary>
    private static long LongestUnheldEdgeKey(MeshState state, int v0, int v1, int v2, out double lengthSquared)
    {
        long best = -1;
        lengthSquared = 0.0;
        Span<int> ends = stackalloc int[] { v0, v1, v1, v2, v2, v0 };
        for (int k = 0; k < 3; k++)
        {
            int a = ends[k * 2], b = ends[(k * 2) + 1];
            long key = EdgeKey(a, b);
            if (state.HeldEdges.Contains(key))
                continue;
            double squared = DistanceSquared(state.Verts, a, b);
            if (squared > lengthSquared)
            {
                lengthSquared = squared;
                best = key;
            }
        }

        return best;
    }

        private static bool FaceHasHeldEdge(MeshState state, int face)
    {
        int v0 = state.Tris[face * 3], v1 = state.Tris[face * 3 + 1], v2 = state.Tris[face * 3 + 2];
        return state.HeldEdges.Contains(EdgeKey(v0, v1)) ||
               state.HeldEdges.Contains(EdgeKey(v1, v2)) ||
               state.HeldEdges.Contains(EdgeKey(v2, v0));
    }

    private static bool[]? HeldVertexMask(int[]? holdEdges, int vertexCount)
    {
        if (holdEdges is not { Length: > 1 })
            return null;

        var mask = new bool[vertexCount];
        foreach (int v in holdEdges)
        {
            if ((uint)v < (uint)vertexCount)
                mask[v] = true;
        }

        return mask;
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
    internal static int FlipForQuality(MeshState state)
    {
        return FlipForQuality(state, CancellationProbe.None);
    }

    internal static int FlipForQuality(MeshState state, CancellationProbe cancellation)
    {
        int totalFlips = 0;
        double[] vertices = state.Verts.ToArray(); // positions don't change during the flip phase

        // Flips rewrite faces but never add or remove them, so everything is sized once for the phase.
        // Edges are visited in the order the edge-incidence dictionary this replaced enumerated them
        // (FlipEdgeIndex explains why that order is each edge's first half-edge in face order), and every
        // test below is the one it made, in the same order. The output is flip-for-flip identical; that is
        // the contract IsotropicRemesherFlipEquivalenceTests holds it to.
        int faceCount = state.FaceCount;
        int halfEdgeCount = faceCount * 3;
        var index = new FlipEdgeIndex();
        var touched = new bool[faceCount];
        var createdEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);

        // A quad the geometry test rejected stays rejected while both of its faces are unchanged, because no
        // vertex moves in this phase: the verdict is a function of the four corners alone. Late sweeps flip a
        // few thousand edges out of millions, so re-deriving every angle of an unchanged mesh was most of their
        // cost. rejectedInSweep[h] is the sweep (1-based) in which the edge whose first half-edge is h was last
        // rejected on geometry; rewrittenInSweep[t] is the last sweep that rewrote face t. A face rewritten in
        // or after the rejecting sweep invalidates the verdict - and changes which edge h even denotes.
        var rejectedInSweep = new int[halfEdgeCount];
        var rewrittenInSweep = new int[faceCount];

        // The geometry verdict of every candidate is taken up front, in parallel, from the faces the sweep
        // starts with. The sequential pass below still decides every flip in order; it only reads the verdict
        // of a quad whose faces are untouched, which are then exactly the faces the verdict was taken from.
        // The field sampler of the retopo path is not known to be thread-safe, so that path stays serial.
        byte[]? verdicts = state.Field == null ? new byte[halfEdgeCount] : null;

        for (int sweep = 1; sweep <= MaxFlipSweeps; sweep++)
        {
            cancellation.ThrowIfCancelled();
            index.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(state.Tris), faceCount, state.VertexCount);
            createdEdges.Clear();
            Array.Clear(touched);
            if (verdicts != null)
                PrecomputeFlipVerdicts(state, vertices, index, rejectedInSweep, rewrittenInSweep, verdicts);

            int flips = 0;
            for (int h = 0; h < halfEdgeCount; h++)
            {
                int count = index.FirstIncidenceCount(h);
                if (count == 0)
                    continue; // not this edge's first half-edge, or a dead face
                cancellation.ThrowIfCancelledOften();
                if (count != 2)
                    continue;

                int h1 = index.SecondHalfEdge(h);
                int t0 = h / 3;
                int t1 = h1 / 3;

                // Every test up to the flip itself is a pure filter, so their order decides nothing; the cheap
                // array reads go first.
                if (touched[t0] || touched[t1])
                    continue;
                if (state.FaceFrozen[t0] || state.FaceFrozen[t1])
                    continue;
                int rejected = rejectedInSweep[h];
                if (rejected != 0 && rewrittenInSweep[t0] < rejected && rewrittenInSweep[t1] < rejected)
                    continue;

                // Neither face has been rewritten this sweep (touched), so the live faces still hold the
                // corners the sweep started with.
                int corner0 = h - (t0 * 3);
                int u = state.Tris[h];
                int v = state.Tris[(t0 * 3) + ((corner0 + 1) % 3)];
                int c = state.Tris[(t0 * 3) + ((corner0 + 2) % 3)];
                int d = state.Tris[(t1 * 3) + ((h1 - (t1 * 3) + 2) % 3)];
                int p = Math.Min(u, v);
                int q = Math.Max(u, v);
                if (state.FeatureEdges.ContainsKey(EdgeKey(p, q)))
                    continue;

                // The new diagonal must not duplicate a pre-existing edge NOR one another flip created
                // this sweep (two disjoint quads can propose the same diagonal — that would be a
                // non-manifold double edge).
                long newKey = EdgeKey(c, d);
                if (index.ContainsEdge(c, d) || createdEdges.Contains(newKey))
                    continue;
                bool improves = verdicts != null
                    ? verdicts[h] == FlipVerdictImproves
                    : FlipImprovesQuad(state, vertices, p, q, c, d);
                if (!improves)
                {
                    rejectedInSweep[h] = sweep;
                    continue;
                }

                WriteOrientedFaceToList(vertices, state.Tris, t0, p, c, d);
                WriteOrientedFaceToList(vertices, state.Tris, t1, c, q, d);
                rewrittenInSweep[t0] = sweep;
                rewrittenInSweep[t1] = sweep;
                createdEdges.Add(newKey);
                touched[t0] = true;
                touched[t1] = true;
                flips++;
            }

            totalFlips += flips;
            if (flips == 0)
                break;
        }

        return totalFlips;
    }

    private const byte FlipVerdictImproves = 1;
    private const byte FlipVerdictRejected = 2;

    /// <summary>
    /// Takes <see cref="FlipImprovesQuad"/> for every edge the coming sweep could reach its geometry test
    /// with — first half-edge of a two-face edge, neither face frozen, not a feature, no still-valid
    /// rejection — from the faces as they stand. Pure reads of state nothing writes during the sweep, so
    /// the result does not depend on scheduling.
    /// </summary>
    private static void PrecomputeFlipVerdicts(
        MeshState state,
        double[] vertices,
        FlipEdgeIndex index,
        int[] rejectedInSweep,
        int[] rewrittenInSweep,
        byte[] verdicts)
    {
        int halfEdgeCount = verdicts.Length;
        List<int> tris = state.Tris;
        List<bool> frozen = state.FaceFrozen;
        Dictionary<long, int> featureEdges = state.FeatureEdges;
        const int chunk = 16_384;
        int chunkCount = (halfEdgeCount + chunk - 1) / chunk;

        System.Threading.Tasks.Parallel.For(0, chunkCount, block =>
        {
            int end = Math.Min(halfEdgeCount, (block + 1) * chunk);
            for (int h = block * chunk; h < end; h++)
            {
                verdicts[h] = 0;
                if (index.FirstIncidenceCount(h) != 2)
                    continue;

                int h1 = index.SecondHalfEdge(h);
                int t0 = h / 3;
                int t1 = h1 / 3;
                if (frozen[t0] || frozen[t1])
                    continue;
                int rejected = rejectedInSweep[h];
                if (rejected != 0 && rewrittenInSweep[t0] < rejected && rewrittenInSweep[t1] < rejected)
                    continue;

                int corner0 = h - (t0 * 3);
                int u = tris[h];
                int v = tris[(t0 * 3) + ((corner0 + 1) % 3)];
                int c = tris[(t0 * 3) + ((corner0 + 2) % 3)];
                int d = tris[(t1 * 3) + ((h1 - (t1 * 3) + 2) % 3)];
                int p = Math.Min(u, v);
                int q = Math.Max(u, v);
                if (featureEdges.ContainsKey(EdgeKey(p, q)))
                    continue;

                verdicts[h] = FlipImprovesQuad(state, vertices, p, q, c, d) ? FlipVerdictImproves : FlipVerdictRejected;
            }
        });
    }

    /// <summary>
    /// The flip's geometry test for quad p-c-q-d with diagonal p–q: convex, and swapping to c–d improves it.
    /// A function of the four corners alone, which is what lets <see cref="FlipForQuality(MeshState, CancellationProbe)"/>
    /// remember a rejection.
    /// </summary>
    private static bool FlipImprovesQuad(MeshState state, double[] vertices, int p, int q, int c, int d)
    {
        if (!QuadIsConvexForFlip(vertices, p, q, c, d))
            return false;

        double minAfter = Math.Min(MinTriangleAngle(vertices, p, c, d), MinTriangleAngle(vertices, c, q, d));
        if (state.Field == null)
        {
            // Plain Remesh path: pure Lawson max-min-angle — flip only when it raises the
            // minimum angle. Unchanged from the original behaviour.
            double minBefore = Math.Min(MinTriangleAngle(vertices, p, q, c), MinTriangleAngle(vertices, p, q, d));
            return minAfter > minBefore + FlipAngleImproveEps;
        }

        // Retopo path: pick the diagonal that best serves as the ~45° hypotenuse of a
        // field-aligned quad, so tri-to-quad pairing (which removes the shared diagonal)
        // yields axis-aligned quads instead of 60/120° rhombi. Hard min-angle floor first
        // so a noisy/singular field can never carve a sliver.
        if (minAfter < FlipMinAngleFloorRad)
            return false;

        double cx = 0.25 * (vertices[p * 3]     + vertices[c * 3]     + vertices[q * 3]     + vertices[d * 3]);
        double cy = 0.25 * (vertices[p * 3 + 1] + vertices[c * 3 + 1] + vertices[q * 3 + 1] + vertices[d * 3 + 1]);
        double theta = state.Field.SampleTheta(cx, cy, double.NaN);
        if (double.IsNaN(theta))
        {
            // Singular field here → fall back to Lawson so we never do worse than isotropic.
            double minBefore = Math.Min(MinTriangleAngle(vertices, p, q, c), MinTriangleAngle(vertices, p, q, d));
            return minAfter > minBefore + FlipAngleImproveEps;
        }

        double sPQ = DiagonalFieldScore(vertices[q * 3] - vertices[p * 3], vertices[q * 3 + 1] - vertices[p * 3 + 1], theta); // current diagonal p-q
        double sCD = DiagonalFieldScore(vertices[d * 3] - vertices[c * 3], vertices[d * 3 + 1] - vertices[c * 3 + 1], theta); // flipped diagonal c-d
        return sCD > sPQ + FlipFieldImproveEps; // hysteresis: only flip toward a strictly better hypotenuse
    }

    private static void WriteOrientedFaceToList(double[] vertices, List<int> tris, int triangle, int p, int q, int r)
    {
        if (Cross2D(vertices, p, q, r) < 0.0)
            (q, r) = (r, q);
        tris[triangle * 3] = p;
        tris[triangle * 3 + 1] = q;
        tris[triangle * 3 + 2] = r;
    }

    /// <summary>
    /// Alignment of a diagonal (heading atan2(dy,dx)) to a 4-RoSy field of angle θ: 1.0 when the
    /// diagonal runs at θ±45° (the ideal hypotenuse of an axis-aligned quad), 0.0 when it is edge
    /// aligned to θ or θ+90°. The 2× folds it to mod-180° and sin² to mod-90°, so it is independent
    /// of both the field's 4-fold ambiguity and the diagonal's endpoint order.
    /// </summary>
    private static double DiagonalFieldScore(double dx, double dy, double theta)
    {
        double s = Math.Sin(2.0 * (Math.Atan2(dy, dx) - theta));
        return s * s;
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
    internal static int RelaxAndProject(MeshState state, double target, TerrainFaceGrid projection)
    {
        return RelaxAndProject(state, target, projection, CancellationProbe.None);
    }

    internal static int RelaxAndProject(MeshState state, double target, TerrainFaceGrid projection, CancellationProbe cancellation)
    {
        int faceCount = state.FaceCount;
        int vertexCount = state.VertexCount;
        MeshVertexAdjacency adjacency = MeshVertexAdjacency.Build(state.Tris, faceCount, vertexCount);

        double areaEps = Math.Max(1e-12, target * target * 1e-9);
        int moved = 0;
        for (int v = 0; v < vertexCount; v++)
        {
            cancellation.ThrowIfCancelledOften();
            if (!adjacency.HasNeighbors(v))
                continue; // orphaned by a collapse

            byte kind = state.Kind[v];
            if (kind == FeaturePolylineGraph.KindCorner || kind == FeaturePolylineGraph.KindFrozen)
                continue;

            if (kind == FeaturePolylineGraph.KindFree)
            {
                if (RelaxFreeVertex(state, projection, adjacency, v, areaEps))
                    moved++;
            }
            else if (RelaxFeatureVertex(state, adjacency, v, areaEps))
            {
                moved++;
            }
        }

        return moved;
    }

    private static bool RelaxFreeVertex(
        MeshState state,
        TerrainFaceGrid projection,
        MeshVertexAdjacency adjacency,
        int v,
        double areaEps)
    {
        ReadOnlySpan<int> ring = adjacency.NeighborsOf(v);
        double cx = 0, cy = 0;
        foreach (int n in ring)
        {
            cx += state.Verts[n * 3];
            cy += state.Verts[n * 3 + 1];
        }

        cx /= ring.Length;
        cy /= ring.Length;

        double px = state.Verts[v * 3];
        double py = state.Verts[v * 3 + 1];
        double dx = (cx - px) * RelaxLambda;
        double dy = (cy - py) * RelaxLambda;
        if ((dx * dx) + (dy * dy) < 1e-24)
            return false;

        if (state.Field != null)
        {
            // Field-aligned relaxation: damp the displacement component across the local quad
            // direction so vertices slide along field lines (edges straighten into the quad flow).
            double theta = state.Field.SampleTheta(px, py, fallback: double.NaN);
            if (!double.IsNaN(theta))
            {
                double e1x = Math.Cos(theta), e1y = Math.Sin(theta);
                double along = (dx * e1x) + (dy * e1y);
                double across = (dx * -e1y) + (dy * e1x);
                double a1 = Math.Abs(along), a2 = Math.Abs(across);
                const double damp = 0.3;
                if (a1 >= a2)
                    across *= damp;
                else
                    along *= damp;
                dx = (along * e1x) - (across * e1y);
                dy = (along * e1y) + (across * e1x);
            }
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            double newX = px + dx;
            double newY = py + dy;
            if (AllIncidentFacesValid(state, adjacency.FacesOf(v), v, newX, newY, areaEps) &&
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
        MeshVertexAdjacency adjacency,
        int v,
        double areaEps)
    {
        int chain = state.Chain[v];
        if (chain < 0)
            return false;

        // Chain neighbors = mesh neighbors joined to v by a feature edge of the same chain.
        int n0 = -1, n1 = -1;
        foreach (int n in adjacency.NeighborsOf(v))
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

        state.EvaluateFeature(chain, tNew, out double newX, out double newY, out double newZ);
        if (!AllIncidentFacesValid(state, adjacency.FacesOf(v), v, newX, newY, areaEps))
            return false;

        state.Verts[v * 3] = newX;
        state.Verts[v * 3 + 1] = newY;
        state.Verts[v * 3 + 2] = newZ;
        state.Param[v] = tNew;
        return true;
    }

    private static bool AllIncidentFacesValid(
        MeshState state,
        MeshVertexAdjacency.FaceEnumerable faces,
        int movedVertex,
        double newX,
        double newY,
        double areaEps)
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

    internal static void EmitRefinedTriangle(List<double> verts, List<int> outTris, int v0, int v1, int v2, int m0, int m1, int m2)
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

}
