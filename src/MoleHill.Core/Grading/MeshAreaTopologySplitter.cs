using System.Diagnostics;
using MoleHill.Core.Engine;
using static MoleHill.Core.Grading.FaceCutGeometry;

namespace MoleHill.Core.Grading;

/// <summary>
/// Conforms area loops into existing terrain faces while preserving shared-edge subdivision.
/// </summary>
internal static class MeshAreaTopologySplitter
{
    /// <summary>
    /// How far, in multiples of the model tolerance, a conform snaps a cut point onto a nearby terrain edge
    /// or corner. A line conformed through the splitter can therefore sit this far off the line as drawn,
    /// which is why a later stage looking for that line in the mesh must search at least this far
    /// (the Retaining Wall's graded-rail trace does).
    /// </summary>
    internal const double ConformSnapToleranceFactor = 8.0;

    /// <summary>A face whose middle corner lies within this distance of its long edge is treated as a cap:
    /// far below any terrain detail, and above the few micrometres float rounding leaves.</summary>
    internal const double ThinCapApexDistance = 1e-5;

    internal sealed class PerformanceTimings
    {
        public double FaceDataMilliseconds { get; internal set; }

        public long FaceDataAllocatedBytes { get; internal set; }

        public double BoundarySegmentsMilliseconds { get; internal set; }

        public long BoundarySegmentsAllocatedBytes { get; internal set; }

        public double FaceMappingMilliseconds { get; internal set; }

        public long FaceMappingAllocatedBytes { get; internal set; }

        public double SharedEdgeRegistryMilliseconds { get; internal set; }

        public long SharedEdgeRegistryAllocatedBytes { get; internal set; }

        public double OutputSetupMilliseconds { get; internal set; }

        public long OutputSetupAllocatedBytes { get; internal set; }

        public double TouchedFaceTriangulationMilliseconds { get; internal set; }

        public long TouchedFaceTriangulationAllocatedBytes { get; internal set; }

        public int TouchedFaceCount { get; internal set; }

        public int RegistryOnlyFaceCount { get; internal set; }

        public int ZeroInternalSegmentFaceCount { get; internal set; }

        public int OneInternalSegmentFaceCount { get; internal set; }

        public int MultipleInternalSegmentFaceCount { get; internal set; }

        public double ClassificationMilliseconds { get; internal set; }

        public long ClassificationAllocatedBytes { get; internal set; }
    }

    /// <summary>
    /// Test seam: faces (by input index) this predicate accepts are treated as if Triangle.NET had
    /// failed to re-triangulate them. Triangle.NET is robust enough that no small fixture reliably
    /// fails, yet the degraded-face path is what keeps a GIS-scale split alive. Thread-static because
    /// the per-face triangulation loop runs on the calling thread, so parallel tests cannot see it.
    /// </summary>
    [ThreadStatic]
    internal static Predicate<int>? ForceRetriangulationFailureForTesting;

    // Output grows only as faces are emitted, without doubling a terrain-sized backing array.
    // Chunks are flattened once into the exact-sized array required by SplitResult.
    private sealed class FaceBuffer
    {
        private const int ChunkSize = 16384;
        private readonly List<int[]> _chunks = new();

        public int Count { get; private set; }

        public void Add(int index)
        {
            int offset = Count % ChunkSize;
            if (offset == 0)
                _chunks.Add(new int[ChunkSize]);
            _chunks[^1][offset] = index;
            Count++;
        }

        public int[] ToArray()
        {
            var result = new int[Count];
            int offset = 0;
            foreach (int[] chunk in _chunks)
            {
                int length = Math.Min(ChunkSize, Count - offset);
                Array.Copy(chunk, 0, result, offset, length);
                offset += length;
            }
            return result;
        }
    }

    public static MeshAreaSplitter.SplitResult? Split(
        IndexedTriMesh mesh,
        MeshAreaSplitter.AreaBoundary[] areas,
        double boundaryTolerance,
        out string? errorMessage,
        Func<bool>? shouldCancel = null)
    {
        return Split(
            mesh,
            areas,
            boundaryTolerance,
            out errorMessage,
            performanceTimings: null,
            shouldCancel);
    }

    internal static MeshAreaSplitter.SplitResult? Split(
        IndexedTriMesh mesh,
        MeshAreaSplitter.AreaBoundary[] areas,
        double boundaryTolerance,
        out string? errorMessage,
        PerformanceTimings? performanceTimings,
        Func<bool>? shouldCancel = null)
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = mesh;

        errorMessage = null;

        // A superseded build must stop here rather than finish mapping and re-triangulating millions of
        // faces while holding their buffers. Cancelling throws, so a partial split can never be handed
        // back and cached as a result.
        CancellationProbe cancellation = CancellationProbe.For(shouldCancel);
        cancellation.ThrowIfCancelled();

        if (areas.Length == 0)
        {
            errorMessage = "No area boundaries provided.";
            return null;
        }

        if (vertexCount == 0 || faceCount == 0)
        {
            errorMessage = "Input mesh has no usable triangles.";
            return null;
        }

        // A cap - a face whose middle corner lies on the edge between the other two, as float-rounded
        // terrain carries by the hundred - has no area to re-triangulate, so it was emitted whole while its
        // neighbour split the shared edge: a zero-area slit in the zone mesh. Split the caps across their
        // long edges first; the surface moves by at most ThinCapApexDistance.
        faces = MeshArrayNormalizer.SplitCollinearCaps(vertices, faces, faceCount, out faceCount, out _, ThinCapApexDistance);

        double tolerance = Math.Max(boundaryTolerance, 1e-9);
        Stopwatch? phaseTimer = performanceTimings != null ? Stopwatch.StartNew() : null;
        long phaseAllocatedBefore = performanceTimings != null
            ? GC.GetTotalAllocatedBytes(precise: true)
            : 0;
        var faceData = new FaceSource(vertices, faces, faceCount);
        if (performanceTimings != null)
        {
            performanceTimings.FaceDataMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.FaceDataAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        cancellation.ThrowIfCancelled();
        var boundarySegments = BuildBoundarySegments(areas, tolerance, cancellation);
        if (performanceTimings != null)
        {
            performanceTimings.BoundarySegmentsMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.BoundarySegmentsAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        if (boundarySegments.Count == 0)
        {
            // Exact topology split should classify strictly by area ownership rather than
            // inflating the inside region by boundary tolerance, which can steal outside
            // seam-adjacent faces and leave the extracted outside mesh open.
            return MeshAreaSplitter.Classify(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), areas, 0.0, out errorMessage);
        }

        cancellation.ThrowIfCancelled();
        var faceCuts = MapBoundarySegmentsToFaces(faceData, boundarySegments, tolerance, cancellation);
        if (performanceTimings != null)
        {
            performanceTimings.FaceMappingMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.FaceMappingAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        bool hasTopologyEdits = false;
        for (int i = 0; i < faceCuts.Length; i++)
        {
            if (faceCuts[i]?.HasData == true)
            {
                hasTopologyEdits = true;
                break;
            }
        }

        if (!hasTopologyEdits)
            return MeshAreaSplitter.Classify(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), areas, 0.0, out errorMessage);

        // Conforming guarantee: every cut point that lands on a terrain edge is registered against
        // that edge (keyed by its two global vertex ids, shared by the two adjacent faces). Both
        // faces then subdivide the shared edge at the SAME points, so independent per-face
        // re-triangulation cannot leave a T-junction / crack. This is what keeps dense and nested
        // boundary loops manifold instead of producing naked edges along shared terrain edges.
        cancellation.ThrowIfCancelled();
        var sharedEdgePoints = BuildSharedEdgeRegistry(faceData, faceCuts, tolerance, cancellation);
        if (performanceTimings != null)
        {
            performanceTimings.SharedEdgeRegistryMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.SharedEdgeRegistryAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        // The output buffers copy the whole input mesh; check before reserving them rather than after.
        cancellation.ThrowIfCancelled();
        var globalVertices = new List<double>(vertices);
        var globalFaces = new FaceBuffer();
        var pointLookup = new GlobalPointLookup(globalVertices, tolerance);
        var edgeSplits = new MeshEdgeSplitRegistry(globalVertices, pointLookup.Append, tolerance);
        if (performanceTimings != null)
        {
            performanceTimings.OutputSetupMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.OutputSetupAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        int degradedFaceCount = 0;
        string? firstFaceError = null;

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            cancellation.ThrowIfCancelledOften();
            var cuts = faceCuts[faceIndex];
            FaceData face = faceData.Get(faceIndex);

            // A face must be re-triangulated when it carries its own cut data OR when a neighbour
            // subdivided one of its edges (registry hit): emitting it unchanged would leave the
            // neighbour's edge point dangling as a T-junction.
            bool hasOwnCuts = cuts != null && cuts.HasData;
            if (!hasOwnCuts && !HasRegistryEdgePoints(face, sharedEdgePoints))
            {
                globalFaces.Add(faces[faceIndex * 3]);
                globalFaces.Add(faces[faceIndex * 3 + 1]);
                globalFaces.Add(faces[faceIndex * 3 + 2]);
                continue;
            }

            if (performanceTimings != null)
            {
                performanceTimings.TouchedFaceCount++;
                if (!hasOwnCuts)
                    performanceTimings.RegistryOnlyFaceCount++;

                int internalSegmentCount = cuts?.InternalSegments.Count ?? 0;
                if (internalSegmentCount == 0)
                    performanceTimings.ZeroInternalSegmentFaceCount++;
                else if (internalSegmentCount == 1)
                    performanceTimings.OneInternalSegmentFaceCount++;
                else
                    performanceTimings.MultipleInternalSegmentFaceCount++;
            }

            bool forceFailure = ForceRetriangulationFailureForTesting?.Invoke(faceIndex) == true;
            if (!TriangulateTouchedFace(face, cuts, sharedEdgePoints, pointLookup, edgeSplits, globalFaces, tolerance, forceFailure, out string? faceError))
            {
                // One face that cannot be re-triangulated must not discard the split for the whole
                // terrain. On a multi-million-face GIS mesh a handful of faces are degenerate or carry
                // constraints Triangle.NET will not honour; failing hard there returned NO zones at all
                // for the entire model. TriangulateTouchedFace has already emitted the face as a fan
                // over its subdivided edges (see EmitBoundaryFan); carry on, reporting how many were
                // degraded.
                degradedFaceCount++;
                firstFaceError ??= faceError;
            }
        }

        // Held apart from errorMessage until classification has run: Classify writes its own out
        // parameter (null on success), which used to discard this warning on every split.
        string? degradedWarning = null;
        if (degradedFaceCount > 0)
        {
            degradedWarning =
                $"{degradedFaceCount:N0} of {faceCount:N0} terrain faces could not be re-triangulated " +
                $"against the zone boundaries; they were split only along their edges, so the zone " +
                $"boundary follows them approximately. " +
                $"First cause: {firstFaceError ?? "unknown"}";
        }

        if (performanceTimings != null)
        {
            performanceTimings.TouchedFaceTriangulationMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.TouchedFaceTriangulationAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        // Splits are resolved per edge, so the faces agree, but that leaves the near-twin vertices the old
        // per-face position merge used to fuse. Fuse them now, as edges of the finished mesh.
        cancellation.ThrowIfCancelled();
        var outputFaces = new List<int>(globalFaces.ToArray());
        NearVertexCollapser.Collapse(globalVertices, outputFaces, vertices.Length / 3, tolerance);

        cancellation.ThrowIfCancelled();
        MeshAreaSplitter.SplitResult? result = MeshAreaSplitter.Classify(
            new IndexedTriMesh(globalVertices.ToArray(), globalVertices.Count / 3, outputFaces.ToArray(), outputFaces.Count / 3),
            areas,
            0.0,
            out string? classifyMessage);
        errorMessage = degradedWarning == null
            ? classifyMessage
            : string.IsNullOrWhiteSpace(classifyMessage) ? degradedWarning : $"{degradedWarning} {classifyMessage}";
        if (performanceTimings != null)
        {
            performanceTimings.ClassificationMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.ClassificationAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
        }

        return result;
    }

    /// <summary>
    /// Canonical key for the terrain edge at <paramref name="edgeIndex"/> of a face: the unordered
    /// pair of its two global vertex ids. Two faces sharing that edge produce the same key, so cut
    /// points registered against it are visible to both.
    /// </summary>
    private static (int, int) EdgeKey(in FaceData face, int edgeIndex)
    {
        int start = edgeIndex switch { 0 => face.I0, 1 => face.I1, _ => face.I2 };
        int end = edgeIndex switch { 0 => face.I1, 1 => face.I2, _ => face.I0 };
        return start < end ? (start, end) : (end, start);
    }

    /// <summary>
    /// Collects every cut point that lies on a terrain edge into a per-edge registry keyed by the
    /// edge's two global vertex ids. The result is the union of subdivision points contributed by
    /// either adjacent face, so both faces can conform to it identically.
    /// </summary>
    private static Dictionary<(int, int), List<Point2D>> BuildSharedEdgeRegistry(
        FaceSource faceData,
        FaceCutData?[] faceCuts,
        double tolerance,
        CancellationProbe? cancellation = null)
    {
        CancellationProbe probe = cancellation ?? CancellationProbe.None;
        var registry = new Dictionary<(int, int), List<Point2D>>();
        double toleranceSquared = tolerance * tolerance;

        for (int faceIndex = 0; faceIndex < faceData.Count; faceIndex++)
        {
            probe.ThrowIfCancelledOften();
            var cuts = faceCuts[faceIndex];
            if (cuts == null || cuts.EdgePoints.Count == 0)
                continue;

            FaceData face = faceData.Get(faceIndex);
            foreach (var edgePoint in cuts.EdgePoints)
            {
                // A point at one of the edge's own ends never subdivides it. Only those: a sliver's far
                // corner can lie within tolerance of the point too, but the face across the edge does not
                // have that corner and would split the edge anyway.
                if (DistanceSquared(edgePoint.Point, face.GetEdgeStart(edgePoint.EdgeIndex)) <= toleranceSquared ||
                    DistanceSquared(edgePoint.Point, face.GetEdgeEnd(edgePoint.EdgeIndex)) <= toleranceSquared)
                    continue;

                (int, int) key = EdgeKey(face, edgePoint.EdgeIndex);
                if (!registry.TryGetValue(key, out var list))
                {
                    list = new List<Point2D>(4);
                    registry[key] = list;
                }

                bool duplicate = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (DistanceSquared(list[i], edgePoint.Point) <= toleranceSquared)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    list.Add(edgePoint.Point);
            }
        }

        return registry;
    }

    private static bool HasRegistryEdgePoints(in FaceData face, Dictionary<(int, int), List<Point2D>> registry)
    {
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            if (registry.TryGetValue(EdgeKey(face, edgeIndex), out var list) && list.Count > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Supplies <see cref="FaceData"/> on demand from the flat vertex/face arrays instead of
    /// materializing one per terrain face up front. Only this geometry setup has constant storage;
    /// cut slots and output buffers still scale with mesh size. Rebuilding one is a few array reads.
    /// </summary>
    private readonly struct FaceSource
    {
        private readonly double[] _vertices;
        private readonly int[] _faces;

        public FaceSource(double[] vertices, int[] faces, int faceCount)
        {
            _vertices = vertices;
            _faces = faces;
            Count = faceCount;
        }

        public int Count { get; }

        public FaceData Get(int faceIndex) => new(_vertices, _faces, faceIndex);
    }

    private static List<CutSegment> BuildBoundarySegments(
        MeshAreaSplitter.AreaBoundary[] areas,
        double tolerance,
        CancellationProbe? cancellation = null)
    {
        CancellationProbe probe = cancellation ?? CancellationProbe.None;
        var sourceSegments = new List<CutSegment>();
        for (int areaIndex = 0; areaIndex < areas.Length; areaIndex++)
        {
            probe.ThrowIfCancelledOften();
            var area = areas[areaIndex];
            for (int vertexIndex = 0; vertexIndex < area.VertexCount; vertexIndex++)
            {
                int next = (vertexIndex + 1) % area.VertexCount;
                var start = new Point2D(area.XyVertices[vertexIndex * 2], area.XyVertices[vertexIndex * 2 + 1]);
                var end = new Point2D(area.XyVertices[next * 2], area.XyVertices[next * 2 + 1]);
                if (DistanceSquared(start, end) <= tolerance * tolerance)
                    continue;

                sourceSegments.Add(new CutSegment(start, end));
            }
        }

        return SplitAtMutualIntersections(sourceSegments, tolerance, probe);
    }

    /// <summary>
    /// Maps every boundary segment onto the terrain faces it cuts.
    ///
    /// Iteration is per FACE, not per segment, for two reasons. It parallelizes: each face owns its own
    /// <see cref="FaceCutData"/> slot, so workers never contend and no locking is needed (the per-segment
    /// form had many segments writing the same face). And it keeps the spatial query on the small side -
    /// the index is built over the boundary segments (thousands) rather than the terrain faces (millions).
    /// Candidates are sorted so each face still accumulates its cuts in ascending segment order, making
    /// the dedup in AddUniqueEdgePoint/AddUniqueSegment order-identical to the serial version.
    /// </summary>
    private static FaceCutData[] MapBoundarySegmentsToFaces(
        FaceSource faceData,
        List<CutSegment> boundarySegments,
        double tolerance,
        CancellationProbe? cancellation = null)
    {
        CancellationProbe probe = cancellation ?? CancellationProbe.None;
        probe.ThrowIfCancelled();
        var segmentBounds = new Bounds2D[boundarySegments.Count];
        for (int i = 0; i < boundarySegments.Count; i++)
        {
            probe.ThrowIfCancelledOften();
            CutSegment segment = boundarySegments[i];
            segmentBounds[i] = new Bounds2D(
                Math.Min(segment.Start.X, segment.End.X) - tolerance,
                Math.Max(segment.Start.X, segment.End.X) + tolerance,
                Math.Min(segment.Start.Y, segment.End.Y) - tolerance,
                Math.Max(segment.Start.Y, segment.End.Y) + tolerance);
        }

        var grid = SpatialHashGrid2D.Build(segmentBounds, valid: null, probe);
        probe.ThrowIfCancelled();
        var result = new FaceCutData[faceData.Count];

        // Cut endpoints within this distance of a terrain edge are projected onto it so they conform
        // (see the snap rationale in the clipped-piece loop). Several times the model tolerance - large
        // enough to absorb near-edge cut points, far below terrain detail.
        double edgeSnapToleranceSquared = (tolerance * ConformSnapToleranceFactor) * (tolerance * ConformSnapToleranceFactor);

        // Each worker gets its own probe: a shared countdown is decremented by all of them at once,
        // which would consult the callback far more often than once per interval per worker.
        try
        {
            System.Threading.Tasks.Parallel.For(
            0,
            faceData.Count,
            () => (
                Scratch: new SpatialHashGrid2D.QueryScratch(boundarySegments.Count),
                Candidates: new List<int>(16),
                EdgePointBuffer: new EdgePoint[8],
                ParameterBuffer: new double[8],
                ClippedPieceBuffer: new SegmentPiece[7],
                Cancellation: probe.Fork(CancellationProbe.ParallelWorkerInterval)),
            (faceIndex, _, state) =>
        {
            state.Cancellation.ThrowIfCancelledOften();
            FaceData face = faceData.Get(faceIndex);
            Bounds2D faceBounds = face.Bounds;

            grid.GatherCandidates(faceBounds, state.Candidates, state.Scratch);
            if (state.Candidates.Count == 0)
                return state;

            state.Candidates.Sort();

            foreach (int segmentIndex in state.Candidates)
            {
                Bounds2D queryBounds = segmentBounds[segmentIndex];
                if (!faceBounds.Intersects(queryBounds))
                    continue;

                CutSegment segment = boundarySegments[segmentIndex];
                AnalyzeSegmentAgainstFace(
                    face,
                    segment,
                    tolerance,
                    EdgePointMerge.SameEdgeOrSharedCorner,
                    state.EdgePointBuffer,
                    out int edgePointCount,
                    state.ParameterBuffer,
                    state.ClippedPieceBuffer,
                    out int clippedPieceCount);
                if (edgePointCount == 0 && clippedPieceCount == 0)
                    continue;

                FaceCutData cuts = result[faceIndex] ??= new FaceCutData();
                for (int edgePointIndex = 0; edgePointIndex < edgePointCount; edgePointIndex++)
                    AddUniqueEdgePoint(cuts.EdgePoints, state.EdgePointBuffer[edgePointIndex], face, tolerance, EdgePointMerge.SameEdgeOrSharedCorner);

                for (int clippedPieceIndex = 0; clippedPieceIndex < clippedPieceCount; clippedPieceIndex++)
                {
                    SegmentPiece rawPiece = state.ClippedPieceBuffer[clippedPieceIndex];
                    // Conform a cut endpoint that lands NEAR (but not within the model tolerance of) a
                    // terrain edge onto that edge. Such a point is otherwise kept interior, and because
                    // each adjacent face detects it at a slightly different spot, they emit overlapping
                    // sliver triangles along the shared edge - one non-manifold edge in the conformed
                    // terrain. Projecting onto the edge makes the point register in the shared-edge
                    // registry, so BOTH faces subdivide the edge identically (no sliver). Projection onto
                    // a shared edge is position-identical from either side, so it is consistent by
                    // construction. The snap radius is several times the model tolerance but far below
                    // terrain detail, so the carve boundary moves negligibly.
                    var clippedPiece = new SegmentPiece(
                        SnapPointToNearEdge(face, rawPiece.Start, edgeSnapToleranceSquared),
                        SnapPointToNearEdge(face, rawPiece.End, edgeSnapToleranceSquared));

                    int edgeIndex = GetPieceEdgeIndex(face, clippedPiece, tolerance);
                    if (edgeIndex >= 0)
                    {
                        AddUniqueEdgePoint(cuts.EdgePoints, new EdgePoint(edgeIndex, clippedPiece.Start), face, tolerance, EdgePointMerge.SameEdgeOrSharedCorner);
                        AddUniqueEdgePoint(cuts.EdgePoints, new EdgePoint(edgeIndex, clippedPiece.End), face, tolerance, EdgePointMerge.SameEdgeOrSharedCorner);
                        continue;
                    }

                    AddUniqueSegment(cuts.InternalSegments, clippedPiece, tolerance);
                    AddPieceEndpointEdgePoints(cuts.EdgePoints, face, clippedPiece, tolerance, EdgePointMerge.SameEdgeOrSharedCorner);
                }
            }

            return state;
        }, _ => { });
        }
        catch (AggregateException aggregate) when (
            aggregate.Flatten().InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            // Parallel.For wraps a worker's exception. Cancellation must reach the host as an
            // OperationCanceledException, not as an aggregated build failure that reads like a bug.
            // Only when EVERY worker was cancelled, though: a genuine failure in one worker that
            // coincides with another worker's cancellation must surface as the failure it is.
            throw new OperationCanceledException("Cancelled.");
        }

        return result;
    }

    private static bool TriangulateTouchedFace(
        in FaceData face,
        FaceCutData? cutData,
        Dictionary<(int, int), List<Point2D>> sharedEdgePoints,
        GlobalPointLookup pointLookup,
        MeshEdgeSplitRegistry edgeSplits,
        FaceBuffer globalFaces,
        double tolerance,
        bool forceFailure,
        out string? errorMessage)
    {
        errorMessage = null;

        FaceData self = face; // the corner/edge resolver below cannot capture an in parameter
        var localPoints = new LocalPointBuilder(tolerance, protectCorners: true);
        var identities = new LocalPointIdentities();
        // Edge 0 runs A-B, edge 1 B-C, edge 2 C-A.
        int a = localPoints.Add(face.A, face.Az, 0b101);
        identities.SetCorner(a, face.I0);
        int b = localPoints.Add(face.B, face.Bz, 0b011);
        identities.SetCorner(b, face.I1);
        int c = localPoints.Add(face.C, face.Cz, 0b110);
        identities.SetCorner(c, face.I2);

        // Snap a cut point that lands very close to a triangle CORNER onto that corner. A daylight-loop
        // segment that grazes near an existing terrain vertex otherwise leaves a cut point a hair off
        // the corner (just beyond the model tolerance), and each adjacent face places its own slightly
        // different near-corner point — producing two overlapping sliver triangles, i.e. a non-manifold
        // edge in the conformed terrain. Snapping to the shared corner is consistent across both faces
        // by construction (same global vertex), so the slivers collapse to degenerate and drop out. The
        // snap radius is several times the model tolerance but far below terrain detail, so the carve
        // boundary moves negligibly.
        double cornerSnapTolSq = (tolerance * ConformSnapToleranceFactor) * (tolerance * ConformSnapToleranceFactor);

        List<(double Parameter, int LocalIndex)>[] edgePointLists = CreateEdgeChains(a, b, c);

        // Seed edge subdivision points from the shared-edge registry rather than this face's own
        // detected points. The registry is the union of both adjacent faces' cut points on each
        // edge, so both faces subdivide their shared edge identically — no T-junction.
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            if (!sharedEdgePoints.TryGetValue(EdgeKey(face, edgeIndex), out var points))
                continue;

            Point2D edgeStart = face.GetEdgeStart(edgeIndex), edgeEnd = face.GetEdgeEnd(edgeIndex);
            foreach (Point2D rawPoint in points)
            {
                // Snap only to this edge's own ends: the face across the edge shares those, so both land on
                // the same vertex. A sliver's far corner is this face's alone - snapping to it moved the split
                // off the shared edge on one side only, folding the two faces over each other.
                Point2D point = SnapToEdgeEnd(edgeStart, edgeEnd, rawPoint, cornerSnapTolSq);
                if (DistanceSquared(point, edgeStart) <= tolerance * tolerance || DistanceSquared(point, edgeEnd) <= tolerance * tolerance)
                    continue;

                // Triangulate the split where it will land - exactly on the edge. Left a hair off it, the thin
                // triangle between it and the unsplit hull edge has area here and none once resolved: a cap.
                point = SnapPointToEdge(edgeStart, edgeEnd, point);
                int localIndex = localPoints.Add(point, face.InterpolateZ(point), 1 << edgeIndex);
                identities.SetEdge(localIndex, edgeIndex);
                double parameter = ParameterOnEdge(face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), point);
                edgePointLists[edgeIndex].Add((parameter, localIndex));
            }
        }

        var segments = new List<(int a, int b)>();
        var segmentKeys = IndexedMeshTools.CreateEdgeKeySet();

        if (cutData != null)
        {
            foreach (var piece in cutData.InternalSegments)
            {
                Point2D pieceStart = SnapToCorner(face, piece.Start, cornerSnapTolSq, tolerance);
                Point2D pieceEnd = SnapToCorner(face, piece.End, cornerSnapTolSq, tolerance);
                int start = localPoints.Add(pieceStart, face.InterpolateZ(pieceStart));
                int end = localPoints.Add(pieceEnd, face.InterpolateZ(pieceEnd));
                if (start == end)
                    continue;

                MeshConstraintTools.TryAddSegment(segments, segmentKeys, start, end);
            }
        }

        int[] edgePointCounts = AddEdgeChainSegments(edgePointLists, segments, segmentKeys);

        // A face whose local point set cannot form a triangle - a sliver whose corners merge inside the
        // merge tolerance, or a set that is entirely collinear - has no meaningful subdivision to
        // compute. Triangle.NET yields 0 triangles for every constrained tier on such input and throws
        // inside the plain-Delaunay fallback, which used to fail the ENTIRE split (and with it every
        // zone in the terrain) over a single degenerate face. Emit the face unchanged instead: it is
        // degenerate to within tolerance, so any T-junction left behind is below tolerance too.
        if (!HasTriangulableArea(localPoints, tolerance))
        {
            globalFaces.Add(face.I0);
            globalFaces.Add(face.I1);
            globalFaces.Add(face.I2);
            return true;
        }

        var outcome = forceFailure
            ? new TriangulationOutcome { WarningMessage = "failure forced for testing." }
            : TriangulationHelper.Triangulate(localPoints.Xy, localPoints.Count, segments, 0, 0, convex: true, segmentSplitting: 0);
        if (outcome.Mesh == null || MeshConstraintTools.ConstraintsWereDropped(outcome.Flags))
        {
            errorMessage =
                $"face at ({face.A.X:0.###}, {face.A.Y:0.###}) with {localPoints.Count} local points and " +
                $"{segments.Count} constraint segments - " +
                (outcome.WarningMessage ?? "no triangles produced.");
            EmitBoundaryFan(face, edgePointLists, edgePointCounts, localPoints, ResolveLocal, pointLookup, globalFaces);
            return false;
        }

        var extracted = TriangleNetExtractor.Extract(outcome.Mesh);
        var extractedToGlobal = new int[extracted.VertexCount];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            var point = new Point2D(extracted.Xy[i * 2], extracted.Xy[i * 2 + 1]);
            int local = extracted.SourceIds[i];
            bool isLocal = local >= 0 && local < localPoints.Count &&
                           localPoints.Xy[local * 2] == point.X && localPoints.Xy[(local * 2) + 1] == point.Y;
            extractedToGlobal[i] = isLocal ? ResolveLocal(local) : pointLookup.Resolve(point, face.InterpolateZ(point));
        }

        // Triangle.NET emits counter-clockwise triangles. A face that runs clockwise in plan (a sliver leaning
        // past vertical beside a wall) must keep its own winding, or its pieces run against the neighbours'
        // and every shared edge is traversed twice in one direction: 65 such edges on a 566k-face terrain,
        // which the hand-off normalization then "fixed" by flipping overlapping faces.
        bool clockwise = face.IsClockwise;

        for (int faceIndex = 0; faceIndex < extracted.FaceCount; faceIndex++)
        {
            int vertex0 = extracted.Faces[faceIndex * 3];
            int vertex1 = extracted.Faces[faceIndex * 3 + 1];
            int vertex2 = extracted.Faces[faceIndex * 3 + 2];
            var p0 = new Point2D(extracted.Xy[vertex0 * 2], extracted.Xy[vertex0 * 2 + 1]);
            var p1 = new Point2D(extracted.Xy[vertex1 * 2], extracted.Xy[vertex1 * 2 + 1]);
            var p2 = new Point2D(extracted.Xy[vertex2 * 2], extracted.Xy[vertex2 * 2 + 1]);
            var centroid = new Point2D((p0.X + p1.X + p2.X) / 3.0, (p0.Y + p1.Y + p2.Y) / 3.0);
            if (!face.ContainsPoint(centroid, tolerance * 8.0))
                continue;

            int g0 = extractedToGlobal[vertex0];
            int g1 = extractedToGlobal[vertex1];
            int g2 = extractedToGlobal[vertex2];
            if (g0 == g1 || g1 == g2 || g2 == g0)
                continue;

            double cross = Math.Abs((p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X));
            if (cross < tolerance * tolerance * 1e-3)
                continue;

            globalFaces.Add(g0);
            globalFaces.Add(clockwise ? g2 : g1);
            globalFaces.Add(clockwise ? g1 : g2);
        }

        return true;

        // A corner is this face's own vertex and a split of a terrain edge resolves against that edge alone,
        // so the two faces sharing an edge land on one vertex lying on it. Anything else resolves by position.
        int ResolveLocal(int local)
        {
            if (identities.TryGetCorner(local, out int corner))
                return corner;

            Point2D point = localPoints.GetPoint(local);
            if (identities.TryGetEdge(local, out int edgeIndex))
            {
                (int start, int end) = edgeIndex switch
                {
                    0 => (self.I0, self.I1),
                    1 => (self.I1, self.I2),
                    _ => (self.I2, self.I0)
                };
                return edgeSplits.Resolve(start, end, point.X, point.Y);
            }

            return pointLookup.Resolve(point, self.InterpolateZ(point));
        }
    }

    /// <summary>
    /// Fallback for a face that cannot be re-triangulated against its interior cut segments. Its
    /// neighbours still subdivide the shared edges at the registry points, so emitting the face whole
    /// would leave T-junctions (single-use edges) along them. Instead it is fanned from its centroid
    /// over the ring of corners and edge points: every ring point lies on the boundary of the convex
    /// face and the centroid is strictly inside it, so each fan triangle is valid, and the edges match
    /// the neighbours' subdivision exactly. The interior constraints are dropped, so classification
    /// assigns each fan triangle to a zone on its own and the boundary crosses this face approximately.
    /// </summary>
    private static void EmitBoundaryFan(
        in FaceData face,
        List<(double Parameter, int LocalIndex)>[] edgePointLists,
        int[] edgePointCounts,
        LocalPointBuilder localPoints,
        Func<int, int> resolveLocal,
        GlobalPointLookup pointLookup,
        FaceBuffer globalFaces)
    {
        // Resolve every ring point (corners included) exactly as the successful path resolves its
        // output vertices, so a neighbour that split the same edge lands on the same global vertices.
        var ring = new List<int>(edgePointCounts[0] + edgePointCounts[1] + edgePointCounts[2]);
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            var points = edgePointLists[edgeIndex];
            for (int i = 0; i < edgePointCounts[edgeIndex]; i++)
            {
                int global = resolveLocal(points[i].LocalIndex);

                // Each edge ends on the corner the next one starts from.
                if (ring.Count == 0 || ring[^1] != global)
                    ring.Add(global);
            }
        }

        if (ring.Count > 1 && ring[^1] == ring[0])
            ring.RemoveAt(ring.Count - 1);

        var center = new Point2D((face.A.X + face.B.X + face.C.X) / 3.0, (face.A.Y + face.B.Y + face.C.Y) / 3.0);
        int centerIndex = pointLookup.Resolve(center, face.InterpolateZ(center));
        for (int i = 0; i < ring.Count; i++)
        {
            int start = ring[i];
            int end = ring[(i + 1) % ring.Count];
            if (start == end || start == centerIndex || end == centerIndex)
                continue;

            // Ring order follows the face's A -> B -> C winding, so the fan keeps its orientation.
            globalFaces.Add(centerIndex);
            globalFaces.Add(start);
            globalFaces.Add(end);
        }
    }

    /// <summary>Snaps a cut point that lands very close to a triangle CORNER onto that corner, so both
    /// faces sharing the corner place it at the identical global vertex instead of two hair-apart
    /// points that would emit overlapping slivers (a non-manifold edge).</summary>
    private static Point2D SnapToCorner(in FaceData face, Point2D p, double cornerSnapTolSq, double tolerance)
    {
        // A point on an edge snaps only to that edge's own ends (see SnapToEdgeEnd); a point inside the face
        // may snap to any corner, since no neighbour sees it.
        bool onAB = PointOnSegment(p, face.A, face.B, tolerance);
        bool onBC = PointOnSegment(p, face.B, face.C, tolerance);
        bool onCA = PointOnSegment(p, face.C, face.A, tolerance);
        bool anyEdge = onAB || onBC || onCA;
        double da = !anyEdge || onAB || onCA ? DistanceSquared(p, face.A) : double.MaxValue;
        double db = !anyEdge || onAB || onBC ? DistanceSquared(p, face.B) : double.MaxValue;
        double dc = !anyEdge || onBC || onCA ? DistanceSquared(p, face.C) : double.MaxValue;
        double best = Math.Min(da, Math.Min(db, dc));
        if (best > cornerSnapTolSq)
            return p;

        return best == da ? face.A : (best == db ? face.B : face.C);
    }

    private static Point2D SnapToEdgeEnd(Point2D start, Point2D end, Point2D p, double snapTolSq)
    {
        double ds = DistanceSquared(p, start), de = DistanceSquared(p, end);
        if (Math.Min(ds, de) > snapTolSq)
            return p;
        return ds <= de ? start : end;
    }

    /// <summary>
    /// Projects <paramref name="point"/> onto the nearest of the face's three edges if it lies within
    /// the (squared) snap radius; otherwise returns it unchanged. Used to conform near-edge cut points
    /// onto the terrain edge so adjacent faces subdivide it identically.
    /// </summary>
    private static Point2D SnapPointToNearEdge(in FaceData face, Point2D point, double snapToleranceSquared)
    {
        double bestDistanceSquared = snapToleranceSquared;
        Point2D best = point;
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            Point2D projected = SnapPointToEdge(face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), point);
            double distanceSquared = DistanceSquared(point, projected);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = projected;
            }
        }

        return best;
    }
}
