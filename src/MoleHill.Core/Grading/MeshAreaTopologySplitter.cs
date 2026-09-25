using System.Diagnostics;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Conforms area loops into existing terrain faces while preserving shared-edge subdivision.
/// </summary>
internal static class MeshAreaTopologySplitter
{
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

    private readonly record struct Point2D(double X, double Y);
    private readonly record struct BoundarySegment(Point2D Start, Point2D End);
    private readonly record struct SegmentPiece(Point2D Start, Point2D End);
    private readonly record struct EdgePoint(int EdgeIndex, Point2D Point);

    // Constructed on demand, without retaining a heap object for every terrain face.
    // Passed by `in` to avoid copying the geometry at helper call sites.
    private readonly struct FaceData
    {
        public required int I0 { get; init; }
        public required int I1 { get; init; }
        public required int I2 { get; init; }
        public required Point2D A { get; init; }
        public required Point2D B { get; init; }
        public required Point2D C { get; init; }
        public required double Az { get; init; }
        public required double Bz { get; init; }
        public required double Cz { get; init; }
        public required Bounds2D Bounds { get; init; }

        public Point2D GetVertex(int index) => index switch
        {
            0 => A,
            1 => B,
            _ => C
        };

        public Point2D GetEdgeStart(int edgeIndex) => edgeIndex switch
        {
            0 => A,
            1 => B,
            _ => C
        };

        public Point2D GetEdgeEnd(int edgeIndex) => edgeIndex switch
        {
            0 => B,
            1 => C,
            _ => A
        };

        public bool IsNearVertex(Point2D point, double tolerance)
        {
            double tolSq = tolerance * tolerance;
            return DistanceSquared(point, A) <= tolSq ||
                   DistanceSquared(point, B) <= tolSq ||
                   DistanceSquared(point, C) <= tolSq;
        }

        public bool ContainsPoint(Point2D point, double tolerance)
        {
            double x0 = A.X;
            double y0 = A.Y;
            double x1 = B.X;
            double y1 = B.Y;
            double x2 = C.X;
            double y2 = C.Y;
            double denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
            if (Math.Abs(denom) <= 1e-16)
                return false;

            double w0 = (((y1 - y2) * (point.X - x2)) + ((x2 - x1) * (point.Y - y2))) / denom;
            double w1 = (((y2 - y0) * (point.X - x2)) + ((x0 - x2) * (point.Y - y2))) / denom;
            double w2 = 1.0 - w0 - w1;
            const double barycentricTolerance = 1e-8;
            return w0 >= -barycentricTolerance &&
                   w1 >= -barycentricTolerance &&
                   w2 >= -barycentricTolerance;
        }

        public double InterpolateZ(Point2D point)
        {
            double x0 = A.X;
            double y0 = A.Y;
            double x1 = B.X;
            double y1 = B.Y;
            double x2 = C.X;
            double y2 = C.Y;
            double denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
            if (Math.Abs(denom) <= 1e-16)
                return Az;

            double w0 = (((y1 - y2) * (point.X - x2)) + ((x2 - x1) * (point.Y - y2))) / denom;
            double w1 = (((y2 - y0) * (point.X - x2)) + ((x0 - x2) * (point.Y - y2))) / denom;
            double w2 = 1.0 - w0 - w1;
            return (w0 * Az) + (w1 * Bz) + (w2 * Cz);
        }

        public int GetEdgeIndex(Point2D point, double tolerance)
        {
            for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
            {
                if (PointOnSegment(point, GetEdgeStart(edgeIndex), GetEdgeEnd(edgeIndex), tolerance))
                    return edgeIndex;
            }

            return -1;
        }
    }

    private sealed class FaceCutData
    {
        public List<SegmentPiece> InternalSegments { get; } = new();
        public List<EdgePoint> EdgePoints { get; } = new();

        public bool HasData => InternalSegments.Count > 0 || EdgePoints.Count > 0;
    }

    private sealed class LocalPointBuilder
    {
        private readonly double _toleranceSquared;

        public LocalPointBuilder(double tolerance)
        {
            _toleranceSquared = tolerance * tolerance;
        }

        public List<double> Xy { get; } = new();
        public List<double> Z { get; } = new();

        public int Count => Z.Count;

        public int Add(Point2D point, double z)
        {
            for (int i = 0; i < Z.Count; i++)
            {
                double dx = Xy[i * 2] - point.X;
                double dy = Xy[i * 2 + 1] - point.Y;
                if ((dx * dx) + (dy * dy) <= _toleranceSquared)
                    return i;
            }

            int index = Z.Count;
            Xy.Add(point.X);
            Xy.Add(point.Y);
            Z.Add(z);
            return index;
        }

        public Point2D GetPoint(int index) => new(Xy[index * 2], Xy[index * 2 + 1]);

        public double GetZ(int index) => Z[index];

        public int Find(Point2D point)
        {
            for (int i = 0; i < Z.Count; i++)
            {
                double dx = Xy[i * 2] - point.X;
                double dy = Xy[i * 2 + 1] - point.Y;
                if ((dx * dx) + (dy * dy) <= _toleranceSquared)
                    return i;
            }

            return -1;
        }
    }

    private sealed class GlobalPointLookup
    {
        private readonly List<double> _vertices;
        private readonly double _toleranceSquared;
        private readonly double _inverseCellSize;
        private readonly Dictionary<long, (int Head, int Tail)> _cells = new();
        private readonly List<int> _next;

        public GlobalPointLookup(List<double> vertices, double tolerance)
        {
            _vertices = vertices;
            double resolvedTolerance = Math.Max(tolerance, 1e-9);
            _toleranceSquared = resolvedTolerance * resolvedTolerance;
            _inverseCellSize = 1.0 / resolvedTolerance;

            int vertexCount = vertices.Count / 3;
            _next = new List<int>(vertexCount);
            for (int i = 0; i < vertexCount; i++)
                Register(i, vertices[i * 3], vertices[i * 3 + 1]);
        }

        public int Resolve(Point2D point, double z)
        {
            if (TryFind(point, out int existing))
                return existing;

            int index = _vertices.Count / 3;
            _vertices.Add(point.X);
            _vertices.Add(point.Y);
            _vertices.Add(z);
            Register(index, point.X, point.Y);
            return index;
        }

        private bool TryFind(Point2D point, out int index)
        {
            long cellX = ToCell(point.X);
            long cellY = ToCell(point.Y);
            double bestDistanceSquared = double.MaxValue;
            int bestIndex = -1;

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!_cells.TryGetValue(PackKey(cellX + dx, cellY + dy), out var cell))
                        continue;

                    for (int candidate = cell.Head; candidate >= 0; candidate = _next[candidate])
                    {
                        double vx = _vertices[candidate * 3];
                        double vy = _vertices[candidate * 3 + 1];
                        double deltaX = vx - point.X;
                        double deltaY = vy - point.Y;
                        double distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
                        if (distanceSquared > _toleranceSquared || distanceSquared >= bestDistanceSquared)
                            continue;

                        bestDistanceSquared = distanceSquared;
                        bestIndex = candidate;
                    }
                }
            }

            index = bestIndex;
            return bestIndex >= 0;
        }

        private void Register(int index, double x, double y)
        {
            long key = PackKey(ToCell(x), ToCell(y));
            // Append in original vertex order: nearest-point ties must resolve exactly as
            // they did with per-cell lists, including duplicate XY vertices at different Z.
            _next.Add(-1);
            if (_cells.TryGetValue(key, out var cell))
            {
                _next[cell.Tail] = index;
                _cells[key] = (cell.Head, index);
            }
            else
                _cells[key] = (index, index);
        }

        private long ToCell(double value) => (long)Math.Floor(value * _inverseCellSize);
    }

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

    private enum SegmentIntersectionKind
    {
        None,
        Point,
        Overlap
    }

    private readonly struct SegmentIntersection
    {
        public required SegmentIntersectionKind Kind { get; init; }
        public required double T0 { get; init; }
        public required double T1 { get; init; }
        public required Point2D P0 { get; init; }
        public required Point2D P1 { get; init; }
    }

    public static MeshAreaSplitter.SplitResult? Split(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        MeshAreaSplitter.AreaBoundary[] areas,
        double boundaryTolerance,
        out string? errorMessage,
        Func<bool>? shouldCancel = null)
    {
        return Split(
            vertices,
            vertexCount,
            faces,
            faceCount,
            areas,
            boundaryTolerance,
            out errorMessage,
            performanceTimings: null,
            shouldCancel);
    }

    internal static MeshAreaSplitter.SplitResult? Split(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        MeshAreaSplitter.AreaBoundary[] areas,
        double boundaryTolerance,
        out string? errorMessage,
        PerformanceTimings? performanceTimings,
        Func<bool>? shouldCancel = null)
    {
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

        // The counts must actually describe the arrays. Rhino mesh extraction normalizes a COPY of the
        // mesh (quads split, identical vertices combined, degenerate faces culled), so a caller that
        // pairs the extracted arrays with the original mesh's Vertices.Count/Faces.Count overruns them.
        // Report that as a diagnosis rather than letting it surface as an IndexOutOfRangeException on a
        // worker thread, where the stack says nothing about which caller mismatched.
        if ((long)faceCount * 3 > faces.Length || (long)vertexCount * 3 > vertices.Length)
        {
            errorMessage =
                $"Mesh data is inconsistent: caller reported {faceCount:N0} faces and {vertexCount:N0} " +
                $"vertices, but the arrays hold {faces.Length / 3:N0} faces and {vertices.Length / 3:N0} " +
                "vertices. The counts must come from the same extraction as the arrays.";
            return null;
        }

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
            return MeshAreaSplitter.Classify(vertices, vertexCount, faces, faceCount, areas, 0.0, out errorMessage);
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
            return MeshAreaSplitter.Classify(vertices, vertexCount, faces, faceCount, areas, 0.0, out errorMessage);

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
            if (!TriangulateTouchedFace(face, cuts, sharedEdgePoints, pointLookup, globalFaces, tolerance, forceFailure, out string? faceError))
            {
                // One face that cannot be re-triangulated must not discard the split for the whole
                // terrain. On a multi-million-face GIS mesh a handful of faces are degenerate or carry
                // constraints Triangle.NET will not honour; failing hard there returned NO zones at all
                // for the entire model. Emit this face unchanged (nothing was appended for it yet) and
                // carry on, reporting how many were degraded.
                globalFaces.Add(face.I0);
                globalFaces.Add(face.I1);
                globalFaces.Add(face.I2);
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
                $"{degradedFaceCount:N0} of {faceCount:N0} terrain faces kept their original topology " +
                $"because they could not be re-triangulated against the zone boundaries. " +
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

        cancellation.ThrowIfCancelled();
        MeshAreaSplitter.SplitResult? result = MeshAreaSplitter.Classify(
            globalVertices.ToArray(),
            globalVertices.Count / 3,
            globalFaces.ToArray(),
            globalFaces.Count / 3,
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
                // Endpoints that coincide with a triangle vertex never subdivide the edge.
                if (face.IsNearVertex(edgePoint.Point, tolerance))
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

        public FaceData Get(int faceIndex)
        {
            int i0 = _faces[faceIndex * 3];
            int i1 = _faces[faceIndex * 3 + 1];
            int i2 = _faces[faceIndex * 3 + 2];
            var a = new Point2D(_vertices[i0 * 3], _vertices[i0 * 3 + 1]);
            var b = new Point2D(_vertices[i1 * 3], _vertices[i1 * 3 + 1]);
            var c = new Point2D(_vertices[i2 * 3], _vertices[i2 * 3 + 1]);

            return new FaceData
            {
                I0 = i0,
                I1 = i1,
                I2 = i2,
                A = a,
                B = b,
                C = c,
                Az = _vertices[i0 * 3 + 2],
                Bz = _vertices[i1 * 3 + 2],
                Cz = _vertices[i2 * 3 + 2],
                Bounds = new Bounds2D(
                    Math.Min(a.X, Math.Min(b.X, c.X)),
                    Math.Max(a.X, Math.Max(b.X, c.X)),
                    Math.Min(a.Y, Math.Min(b.Y, c.Y)),
                    Math.Max(a.Y, Math.Max(b.Y, c.Y)))
            };
        }
    }

    private static List<BoundarySegment> BuildBoundarySegments(
        MeshAreaSplitter.AreaBoundary[] areas,
        double tolerance,
        CancellationProbe? cancellation = null)
    {
        CancellationProbe probe = cancellation ?? CancellationProbe.None;
        var sourceSegments = new List<BoundarySegment>();
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

                sourceSegments.Add(new BoundarySegment(start, end));
            }
        }

        if (sourceSegments.Count == 0)
            return sourceSegments;

        var splitParameters = new List<double>[sourceSegments.Count];
        var segmentBounds = new Bounds2D[sourceSegments.Count];
        for (int i = 0; i < sourceSegments.Count; i++)
        {
            splitParameters[i] = new List<double>(4) { 0.0, 1.0 };
            var segment = sourceSegments[i];
            segmentBounds[i] = new Bounds2D(
                Math.Min(segment.Start.X, segment.End.X),
                Math.Max(segment.Start.X, segment.End.X),
                Math.Min(segment.Start.Y, segment.End.Y),
                Math.Max(segment.Start.Y, segment.End.Y));
        }

        // Boundary-vs-boundary intersections are found through a spatial index, not an all-pairs sweep.
        // Zone boundaries come straight from GIS/CAD polygons and routinely carry tens of thousands of
        // vertices; the n^2 sweep this replaces did ~1.6e9 bbox tests on a 56k-vertex cadastral layer.
        // Every other hot loop in this file is already indexed this way.
        var pairGrid = SpatialHashGrid2D.Build(segmentBounds, valid: null, probe);
        var pairScratch = new SpatialHashGrid2D.QueryScratch(sourceSegments.Count);
        var pairCandidates = new List<int>(16);

        for (int i = 0; i < sourceSegments.Count; i++)
        {
            probe.ThrowIfCancelledOften();
            pairGrid.GatherCandidates(segmentBounds[i], pairCandidates, pairScratch);
            // Sorted so each unordered pair is still visited exactly once, in the same ascending order
            // the all-pairs sweep used - split parameters accumulate identically.
            pairCandidates.Sort();

            foreach (int j in pairCandidates)
            {
                if (j <= i)
                    continue;

                if (!segmentBounds[i].Intersects(segmentBounds[j]))
                    continue;

                var intersection = IntersectSegments(sourceSegments[i].Start, sourceSegments[i].End, sourceSegments[j].Start, sourceSegments[j].End, tolerance);
                if (intersection.Kind == SegmentIntersectionKind.None)
                    continue;

                AddSplitParameter(splitParameters[i], intersection.T0);
                AddSplitParameter(splitParameters[i], intersection.T1);

                var reverse = IntersectSegments(sourceSegments[j].Start, sourceSegments[j].End, sourceSegments[i].Start, sourceSegments[i].End, tolerance);
                AddSplitParameter(splitParameters[j], reverse.T0);
                AddSplitParameter(splitParameters[j], reverse.T1);
            }
        }

        var dedupedSegments = new List<BoundarySegment>();
        double inverseTolerance = 1.0 / tolerance;
        var seen = new HashSet<(long, long, long, long)>();

        for (int segmentIndex = 0; segmentIndex < sourceSegments.Count; segmentIndex++)
        {
            var parameters = splitParameters[segmentIndex];
            parameters.Sort();

            int uniqueCount = 0;
            for (int i = 0; i < parameters.Count; i++)
            {
                double value = Math.Clamp(parameters[i], 0.0, 1.0);
                if (uniqueCount > 0 && Math.Abs(value - parameters[uniqueCount - 1]) <= 1e-9)
                    continue;

                parameters[uniqueCount++] = value;
            }

            var source = sourceSegments[segmentIndex];
            for (int i = 0; i < uniqueCount - 1; i++)
            {
                double t0 = parameters[i];
                double t1 = parameters[i + 1];
                if (t1 - t0 <= 1e-9)
                    continue;

                Point2D start = Lerp(source.Start, source.End, t0);
                Point2D end = Lerp(source.Start, source.End, t1);
                if (DistanceSquared(start, end) <= tolerance * tolerance)
                    continue;

                var key = CreateSegmentKey(start, end, inverseTolerance);
                if (!seen.Add(key))
                    continue;

                dedupedSegments.Add(new BoundarySegment(start, end));
            }
        }

        return dedupedSegments;
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
        List<BoundarySegment> boundarySegments,
        double tolerance,
        CancellationProbe? cancellation = null)
    {
        CancellationProbe probe = cancellation ?? CancellationProbe.None;
        probe.ThrowIfCancelled();
        var segmentBounds = new Bounds2D[boundarySegments.Count];
        for (int i = 0; i < boundarySegments.Count; i++)
        {
            probe.ThrowIfCancelledOften();
            BoundarySegment segment = boundarySegments[i];
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
        double edgeSnapToleranceSquared = (tolerance * 8.0) * (tolerance * 8.0);

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

                BoundarySegment segment = boundarySegments[segmentIndex];
                AnalyzeSegmentAgainstFace(
                    face,
                    segment,
                    tolerance,
                    state.EdgePointBuffer,
                    out int edgePointCount,
                    state.ParameterBuffer,
                    state.ClippedPieceBuffer,
                    out int clippedPieceCount);
                if (edgePointCount == 0 && clippedPieceCount == 0)
                    continue;

                FaceCutData cuts = result[faceIndex] ??= new FaceCutData();
                for (int edgePointIndex = 0; edgePointIndex < edgePointCount; edgePointIndex++)
                    AddUniqueEdgePoint(cuts.EdgePoints, state.EdgePointBuffer[edgePointIndex], face, tolerance);

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
                        AddUniqueEdgePoint(cuts.EdgePoints, new EdgePoint(edgeIndex, clippedPiece.Start), face, tolerance);
                        AddUniqueEdgePoint(cuts.EdgePoints, new EdgePoint(edgeIndex, clippedPiece.End), face, tolerance);
                        continue;
                    }

                    AddUniqueSegment(cuts.InternalSegments, clippedPiece, tolerance);
                    AddPieceEndpointEdgePoints(cuts.EdgePoints, face, clippedPiece, tolerance);
                }
            }

            return state;
        }, _ => { });
        }
        catch (AggregateException aggregate) when (
            aggregate.Flatten().InnerExceptions.Any(inner => inner is OperationCanceledException))
        {
            // Parallel.For wraps a worker's exception. Cancellation must reach the host as an
            // OperationCanceledException, not as an aggregated build failure that reads like a bug.
            throw new OperationCanceledException("Cancelled.");
        }

        return result;
    }

    private static bool TriangulateTouchedFace(
        in FaceData face,
        FaceCutData? cutData,
        Dictionary<(int, int), List<Point2D>> sharedEdgePoints,
        GlobalPointLookup pointLookup,
        FaceBuffer globalFaces,
        double tolerance,
        bool forceFailure,
        out string? errorMessage)
    {
        errorMessage = null;

        var localPoints = new LocalPointBuilder(tolerance);
        int a = localPoints.Add(face.A, face.Az);
        int b = localPoints.Add(face.B, face.Bz);
        int c = localPoints.Add(face.C, face.Cz);

        // Snap a cut point that lands very close to a triangle CORNER onto that corner. A daylight-loop
        // segment that grazes near an existing terrain vertex otherwise leaves a cut point a hair off
        // the corner (just beyond the model tolerance), and each adjacent face places its own slightly
        // different near-corner point — producing two overlapping sliver triangles, i.e. a non-manifold
        // edge in the conformed terrain. Snapping to the shared corner is consistent across both faces
        // by construction (same global vertex), so the slivers collapse to degenerate and drop out. The
        // snap radius is several times the model tolerance but far below terrain detail, so the carve
        // boundary moves negligibly.
        double cornerSnapTolSq = (tolerance * 8.0) * (tolerance * 8.0);

        var edgePointLists = new List<(double Parameter, int LocalIndex)>[3];
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
            edgePointLists[edgeIndex] = new List<(double Parameter, int LocalIndex)>(4);

        edgePointLists[0].Add((0.0, a));
        edgePointLists[0].Add((1.0, b));
        edgePointLists[1].Add((0.0, b));
        edgePointLists[1].Add((1.0, c));
        edgePointLists[2].Add((0.0, c));
        edgePointLists[2].Add((1.0, a));

        // Seed edge subdivision points from the shared-edge registry rather than this face's own
        // detected points. The registry is the union of both adjacent faces' cut points on each
        // edge, so both faces subdivide their shared edge identically — no T-junction.
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            if (!sharedEdgePoints.TryGetValue(EdgeKey(face, edgeIndex), out var points))
                continue;

            foreach (Point2D rawPoint in points)
            {
                Point2D point = SnapToCorner(face, rawPoint, cornerSnapTolSq);

                // A point that snapped to (or already coincides with) a corner does not subdivide the
                // edge — the corner is already a triangle vertex.
                if (face.IsNearVertex(point, tolerance))
                    continue;

                int localIndex = localPoints.Add(point, face.InterpolateZ(point));
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
                Point2D pieceStart = SnapToCorner(face, piece.Start, cornerSnapTolSq);
                Point2D pieceEnd = SnapToCorner(face, piece.End, cornerSnapTolSq);
                int start = localPoints.Add(pieceStart, face.InterpolateZ(pieceStart));
                int end = localPoints.Add(pieceEnd, face.InterpolateZ(pieceEnd));
                if (start == end)
                    continue;

                MeshConstraintTools.TryAddSegment(segments, segmentKeys, start, end);
            }
        }

        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            var points = edgePointLists[edgeIndex];
            points.Sort((left, right) => left.Parameter.CompareTo(right.Parameter));

            int writeIndex = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var current = points[i];
                if (writeIndex > 0)
                {
                    var previous = points[writeIndex - 1];
                    if (current.LocalIndex == previous.LocalIndex || Math.Abs(current.Parameter - previous.Parameter) <= 1e-9)
                        continue;
                }

                points[writeIndex++] = current;
            }

            for (int i = 0; i < writeIndex - 1; i++)
            {
                int start = points[i].LocalIndex;
                int end = points[i + 1].LocalIndex;
                if (start == end)
                    continue;

                MeshConstraintTools.TryAddSegment(segments, segmentKeys, start, end);
            }
        }

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
            return false;
        }

        var extracted = TriangleNetExtractor.Extract(outcome.Mesh);
        var extractedToGlobal = new int[extracted.VertexCount];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            var point = new Point2D(extracted.Xy[i * 2], extracted.Xy[i * 2 + 1]);
            extractedToGlobal[i] = pointLookup.Resolve(point, face.InterpolateZ(point));
        }

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
            globalFaces.Add(g1);
            globalFaces.Add(g2);
        }

        return true;
    }

    private static void AnalyzeSegmentAgainstFace(
        in FaceData face,
        BoundarySegment segment,
        double tolerance,
        EdgePoint[] edgePoints,
        out int edgePointCount,
        double[] parameters,
        SegmentPiece[] clippedPieces,
        out int clippedPieceCount)
    {
        edgePointCount = 0;
        clippedPieceCount = 0;
        int parameterCount = 0;
        bool startInside = face.ContainsPoint(segment.Start, tolerance);
        bool endInside = face.ContainsPoint(segment.End, tolerance);

        if (startInside)
            parameters[parameterCount++] = 0.0;
        if (endInside)
            parameters[parameterCount++] = 1.0;

        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            SegmentIntersection intersection = IntersectSegments(
                segment.Start,
                segment.End,
                face.GetEdgeStart(edgeIndex),
                face.GetEdgeEnd(edgeIndex),
                tolerance);
            if (intersection.Kind == SegmentIntersectionKind.None)
                continue;

            AddScratchEdgeTouchPoint(
                edgePoints,
                ref edgePointCount,
                face,
                edgeIndex,
                intersection.P0,
                tolerance);
            if (intersection.Kind == SegmentIntersectionKind.Overlap)
            {
                AddScratchEdgeTouchPoint(
                    edgePoints,
                    ref edgePointCount,
                    face,
                    edgeIndex,
                    intersection.P1,
                    tolerance);
            }

            parameters[parameterCount++] = intersection.T0;
            parameters[parameterCount++] = intersection.T1;
        }

        if (startInside)
        {
            int edgeIndex = face.GetEdgeIndex(segment.Start, tolerance);
            if (edgeIndex >= 0)
            {
                AddScratchEdgeTouchPoint(
                    edgePoints,
                    ref edgePointCount,
                    face,
                    edgeIndex,
                    segment.Start,
                    tolerance);
            }
        }

        if (endInside)
        {
            int edgeIndex = face.GetEdgeIndex(segment.End, tolerance);
            if (edgeIndex >= 0)
            {
                AddScratchEdgeTouchPoint(
                    edgePoints,
                    ref edgePointCount,
                    face,
                    edgeIndex,
                    segment.End,
                    tolerance);
            }
        }

        if (parameterCount == 0)
            return;

        for (int index = 1; index < parameterCount; index++)
        {
            double value = parameters[index];
            int writeIndex = index;
            while (writeIndex > 0 && parameters[writeIndex - 1] > value)
            {
                parameters[writeIndex] = parameters[writeIndex - 1];
                writeIndex--;
            }

            parameters[writeIndex] = value;
        }

        int uniqueCount = 0;
        for (int index = 0; index < parameterCount; index++)
        {
            double value = Math.Clamp(parameters[index], 0.0, 1.0);
            if (uniqueCount > 0 && Math.Abs(value - parameters[uniqueCount - 1]) <= 1e-9)
                continue;

            parameters[uniqueCount++] = value;
        }

        for (int index = 0; index < uniqueCount - 1; index++)
        {
            double t0 = parameters[index];
            double t1 = parameters[index + 1];
            if (t1 - t0 <= 1e-9)
                continue;

            double midpointT = (t0 + t1) * 0.5;
            var midpoint = Lerp(segment.Start, segment.End, midpointT);
            if (!face.ContainsPoint(midpoint, tolerance))
                continue;

            var start = SnapPointToTriangle(face, Lerp(segment.Start, segment.End, t0), tolerance);
            var end = SnapPointToTriangle(face, Lerp(segment.Start, segment.End, t1), tolerance);
            if (DistanceSquared(start, end) <= tolerance * tolerance)
                continue;

            clippedPieces[clippedPieceCount++] = new SegmentPiece(start, end);
        }
    }

    private static void AddScratchEdgeTouchPoint(
        EdgePoint[] destination,
        ref int count,
        in FaceData face,
        int edgeIndex,
        Point2D point,
        double tolerance)
    {
        Point2D snapped = SnapPointToEdge(
            face.GetEdgeStart(edgeIndex),
            face.GetEdgeEnd(edgeIndex),
            point);
        if (face.IsNearVertex(snapped, tolerance))
            return;

        var candidate = new EdgePoint(edgeIndex, snapped);
        double toleranceSquared = tolerance * tolerance;
        double vertexToleranceSquared = 4.0 * toleranceSquared;
        for (int index = 0; index < count; index++)
        {
            EdgePoint existing = destination[index];
            if (DistanceSquared(existing.Point, candidate.Point) > toleranceSquared)
                continue;

            if (existing.EdgeIndex == candidate.EdgeIndex)
                return;

            for (int vertexIndex = 0; vertexIndex < 3; vertexIndex++)
            {
                Point2D vertex = face.GetVertex(vertexIndex);
                if (DistanceSquared(existing.Point, vertex) <= vertexToleranceSquared &&
                    DistanceSquared(candidate.Point, vertex) <= vertexToleranceSquared)
                {
                    return;
                }
            }
        }

        destination[count++] = candidate;
    }

    private static int GetPieceEdgeIndex(in FaceData face, SegmentPiece piece, double tolerance)
    {
        int startEdge = face.GetEdgeIndex(piece.Start, tolerance);
        int endEdge = face.GetEdgeIndex(piece.End, tolerance);
        if (startEdge < 0 || startEdge != endEdge)
            return -1;

        var midpoint = new Point2D((piece.Start.X + piece.End.X) * 0.5, (piece.Start.Y + piece.End.Y) * 0.5);
        return face.GetEdgeIndex(midpoint, tolerance) == startEdge
            ? startEdge
            : -1;
    }

    private static void AddPieceEndpointEdgePoints(List<EdgePoint> edgePoints, FaceData face, SegmentPiece piece, double tolerance)
    {
        int startEdge = face.GetEdgeIndex(piece.Start, tolerance);
        if (startEdge >= 0)
            AddUniqueEdgePoint(edgePoints, new EdgePoint(startEdge, piece.Start), face, tolerance);

        int endEdge = face.GetEdgeIndex(piece.End, tolerance);
        if (endEdge >= 0)
            AddUniqueEdgePoint(edgePoints, new EdgePoint(endEdge, piece.End), face, tolerance);
    }

    private static void AddUniqueEdgePoint(List<EdgePoint> destination, EdgePoint candidate, FaceData face, double tolerance)
    {
        double toleranceSquared = tolerance * tolerance;
        double vertexToleranceSquared = 4.0 * toleranceSquared;
        for (int i = 0; i < destination.Count; i++)
        {
            var existing = destination[i];
            if (DistanceSquared(existing.Point, candidate.Point) > toleranceSquared)
                continue;

            if (existing.EdgeIndex == candidate.EdgeIndex)
                return;

            for (int vertexIndex = 0; vertexIndex < 3; vertexIndex++)
            {
                var vertex = face.GetVertex(vertexIndex);
                if (DistanceSquared(existing.Point, vertex) <= vertexToleranceSquared &&
                    DistanceSquared(candidate.Point, vertex) <= vertexToleranceSquared)
                {
                    return;
                }
            }
        }

        destination.Add(candidate);
    }

    private static void AddUniqueSegment(List<SegmentPiece> destination, SegmentPiece candidate, double tolerance)
    {
        double toleranceSquared = tolerance * tolerance;
        for (int i = 0; i < destination.Count; i++)
        {
            var existing = destination[i];
            bool sameDirection = DistanceSquared(existing.Start, candidate.Start) <= toleranceSquared &&
                                 DistanceSquared(existing.End, candidate.End) <= toleranceSquared;
            bool reverseDirection = DistanceSquared(existing.Start, candidate.End) <= toleranceSquared &&
                                    DistanceSquared(existing.End, candidate.Start) <= toleranceSquared;
            if (sameDirection || reverseDirection)
                return;
        }

        destination.Add(candidate);
    }

    private static SegmentIntersection IntersectSegments(Point2D a0, Point2D a1, Point2D b0, Point2D b1, double tolerance)
    {
        double rx = a1.X - a0.X;
        double ry = a1.Y - a0.Y;
        double sx = b1.X - b0.X;
        double sy = b1.Y - b0.Y;
        double rxs = Cross(rx, ry, sx, sy);
        double qpx = b0.X - a0.X;
        double qpy = b0.Y - a0.Y;
        double qpxr = Cross(qpx, qpy, rx, ry);

        if (Math.Abs(rxs) <= tolerance && Math.Abs(qpxr) <= tolerance)
        {
            double t0 = ParameterOnSegment(a0, a1, b0);
            double t1 = ParameterOnSegment(a0, a1, b1);
            double minT = Math.Max(0.0, Math.Min(t0, t1));
            double maxT = Math.Min(1.0, Math.Max(t0, t1));
            if (maxT < 0.0 || minT > 1.0)
            {
                return new SegmentIntersection
                {
                    Kind = SegmentIntersectionKind.None,
                    T0 = 0.0,
                    T1 = 0.0,
                    P0 = a0,
                    P1 = a0
                };
            }

            var start = Lerp(a0, a1, minT);
            var end = Lerp(a0, a1, maxT);
            if (DistanceSquared(start, end) <= tolerance * tolerance)
            {
                return new SegmentIntersection
                {
                    Kind = SegmentIntersectionKind.Point,
                    T0 = minT,
                    T1 = minT,
                    P0 = start,
                    P1 = start
                };
            }

            return new SegmentIntersection
            {
                Kind = SegmentIntersectionKind.Overlap,
                T0 = minT,
                T1 = maxT,
                P0 = start,
                P1 = end
            };
        }

        if (Math.Abs(rxs) <= tolerance)
        {
            return new SegmentIntersection
            {
                Kind = SegmentIntersectionKind.None,
                T0 = 0.0,
                T1 = 0.0,
                P0 = a0,
                P1 = a0
            };
        }

        double t = Cross(qpx, qpy, sx, sy) / rxs;
        double u = Cross(qpx, qpy, rx, ry) / rxs;
        if (t < -1e-9 || t > 1.0 + 1e-9 || u < -1e-9 || u > 1.0 + 1e-9)
        {
            return new SegmentIntersection
            {
                Kind = SegmentIntersectionKind.None,
                T0 = 0.0,
                T1 = 0.0,
                P0 = a0,
                P1 = a0
            };
        }

        var intersectionPoint = Lerp(a0, a1, Math.Clamp(t, 0.0, 1.0));
        return new SegmentIntersection
        {
            Kind = SegmentIntersectionKind.Point,
            T0 = Math.Clamp(t, 0.0, 1.0),
            T1 = Math.Clamp(t, 0.0, 1.0),
            P0 = intersectionPoint,
            P1 = intersectionPoint
        };
    }

    private static void AddSplitParameter(List<double> parameters, double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        for (int i = 0; i < parameters.Count; i++)
        {
            if (Math.Abs(parameters[i] - value) <= 1e-9)
                return;
        }

        parameters.Add(value);
    }

    private static (long, long, long, long) CreateSegmentKey(Point2D start, Point2D end, double inverseTolerance)
    {
        long x0 = (long)Math.Round(start.X * inverseTolerance);
        long y0 = (long)Math.Round(start.Y * inverseTolerance);
        long x1 = (long)Math.Round(end.X * inverseTolerance);
        long y1 = (long)Math.Round(end.Y * inverseTolerance);
        bool keepOrder = x0 < x1 || (x0 == x1 && y0 <= y1);
        return keepOrder
            ? (x0, y0, x1, y1)
            : (x1, y1, x0, y0);
    }

    private static Point2D SnapPointToTriangle(in FaceData face, Point2D point, double tolerance)
    {
        if (DistanceSquared(point, face.A) <= tolerance * tolerance)
            return face.A;
        if (DistanceSquared(point, face.B) <= tolerance * tolerance)
            return face.B;
        if (DistanceSquared(point, face.C) <= tolerance * tolerance)
            return face.C;

        int edgeIndex = face.GetEdgeIndex(point, tolerance);
        return edgeIndex < 0
            ? point
            : SnapPointToEdge(face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), point);
    }

    private static Point2D SnapPointToEdge(Point2D edgeStart, Point2D edgeEnd, Point2D point)
    {
        double t = ParameterOnEdge(edgeStart, edgeEnd, point);
        return Lerp(edgeStart, edgeEnd, t);
    }

    /// <summary>
    /// True when the local point set spans a real area, i.e. some three points form a triangle above
    /// the degeneracy epsilon. Runs in O(n): the point farthest from the first one fixes the dominant
    /// direction, so if any point lies off that line the set is not collinear.
    /// </summary>
    private static bool HasTriangulableArea(LocalPointBuilder points, double tolerance)
    {
        if (points.Count < 3)
            return false;

        Point2D origin = points.GetPoint(0);
        int farthest = -1;
        double farthestDistance = 0.0;
        for (int i = 1; i < points.Count; i++)
        {
            double distance = DistanceSquared(origin, points.GetPoint(i));
            if (distance > farthestDistance)
            {
                farthestDistance = distance;
                farthest = i;
            }
        }

        if (farthest < 0 || farthestDistance <= 0.0)
            return false;

        Point2D axis = points.GetPoint(farthest);
        double areaEpsilon = tolerance * tolerance * 1e-3;
        for (int i = 1; i < points.Count; i++)
        {
            if (i == farthest)
                continue;

            Point2D candidate = points.GetPoint(i);
            double cross = Math.Abs(
                ((axis.X - origin.X) * (candidate.Y - origin.Y)) -
                ((axis.Y - origin.Y) * (candidate.X - origin.X)));
            if (cross > areaEpsilon)
                return true;
        }

        return false;
    }

    /// <summary>Snaps a cut point that lands very close to a triangle CORNER onto that corner, so both
    /// faces sharing the corner place it at the identical global vertex instead of two hair-apart
    /// points that would emit overlapping slivers (a non-manifold edge).</summary>
    private static Point2D SnapToCorner(in FaceData face, Point2D p, double cornerSnapTolSq)
    {
        double da = DistanceSquared(p, face.A);
        double db = DistanceSquared(p, face.B);
        double dc = DistanceSquared(p, face.C);
        double best = Math.Min(da, Math.Min(db, dc));
        if (best > cornerSnapTolSq)
            return p;

        return best == da ? face.A : (best == db ? face.B : face.C);
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

    private static double ParameterOnSegment(Point2D start, Point2D end, Point2D point)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
    }

    private static double ParameterOnEdge(Point2D start, Point2D end, Point2D point)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return Math.Clamp((((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared, 0.0, 1.0);
    }

    private static bool PointOnSegment(Point2D point, Point2D start, Point2D end, double tolerance)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return DistanceSquared(point, start) <= tolerance * tolerance;

        double t = (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
        if (t < -tolerance || t > 1.0 + tolerance)
            return false;

        var projected = new Point2D(start.X + (dx * Math.Clamp(t, 0.0, 1.0)), start.Y + (dy * Math.Clamp(t, 0.0, 1.0)));
        return DistanceSquared(projected, point) <= tolerance * tolerance;
    }

    private static Point2D Lerp(Point2D start, Point2D end, double t)
    {
        return new Point2D(
            start.X + ((end.X - start.X) * t),
            start.Y + ((end.Y - start.Y) * t));
    }

    private static double Cross(double ax, double ay, double bx, double by) => (ax * by) - (ay * bx);

    private static double DistanceSquared(Point2D a, Point2D b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static long PackKey(long cellX, long cellY) => (cellX * 0x100000001L) ^ (cellY * 0x27d4eb2dL);
}
