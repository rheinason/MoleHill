using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace MoleHill.Core.Engine;

/// <summary>
/// Main TIN triangulation orchestrator with XY-hash caching.
/// Persists between GH solves as a field on the component.
/// </summary>
public class TinEngine
{
    private const int MinSteinerPointCap = 25_000;
    private const int MaxSteinerPointCap = 250_000;
    private const int SteinerPointCapMultiplier = 12;
    private const int PlainFallbackVertexLimit = 100_000;
    private const int PlainFallbackSegmentLimit = 150_000;

    private InputSnapshot? _cachedSnapshot;
    private TinResult? _cachedResult;
    private Mesh? _cachedMesh;
    private IncrementalTopologyState? _topologyState;
    private readonly object _gate = new();

    private readonly record struct XyKey(long XBits, long YBits)
    {
        public static XyKey FromValues(double x, double y) =>
            new(BitConverter.DoubleToInt64Bits(x), BitConverter.DoubleToInt64Bits(y));
    }

    private sealed class IncrementalTopologyState
    {
        public int[] Segments { get; }
        public Dictionary<XyKey, int> VertexIdByKey { get; }
        public HashSet<XyKey> ProtectedKeys { get; }
        public bool QualityConstrained { get; }
        public bool UseConvexHull { get; }

        public IncrementalTopologyState(
            int[] segments,
            Dictionary<XyKey, int> vertexIdByKey,
            HashSet<XyKey> protectedKeys,
            bool qualityConstrained,
            bool useConvexHull)
        {
            Segments = segments;
            VertexIdByKey = vertexIdByKey;
            ProtectedKeys = protectedKeys;
            QualityConstrained = qualityConstrained;
            UseConvexHull = useConvexHull;
        }
    }

    private readonly struct SegmentInfo
    {
        public readonly int A;
        public readonly int B;
        public readonly double X0;
        public readonly double Y0;
        public readonly double Dx;
        public readonly double Dy;
        public readonly double LengthSquared;
        public readonly double ToleranceSquared;
        public readonly Bounds2D Bounds;

        public SegmentInfo(
            int a,
            int b,
            double x0,
            double y0,
            double dx,
            double dy,
            double lengthSquared,
            double toleranceSquared,
            Bounds2D bounds)
        {
            A = a;
            B = b;
            X0 = x0;
            Y0 = y0;
            Dx = dx;
            Dy = dy;
            LengthSquared = lengthSquared;
            ToleranceSquared = toleranceSquared;
            Bounds = bounds;
        }
    }

    private readonly struct FaceInfo
    {
        public readonly int I0;
        public readonly int I1;
        public readonly int I2;
        public readonly double X0;
        public readonly double Y0;
        public readonly double X1;
        public readonly double Y1;
        public readonly double X2;
        public readonly double Y2;
        public readonly double Denominator;
        public readonly Bounds2D Bounds;

        public FaceInfo(
            int i0,
            int i1,
            int i2,
            double x0,
            double y0,
            double x1,
            double y1,
            double x2,
            double y2,
            double denominator,
            Bounds2D bounds)
        {
            I0 = i0;
            I1 = i1;
            I2 = i2;
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
            Denominator = denominator;
            Bounds = bounds;
        }
    }

    public TinResult? Build(double[] xyCoords, double[] zValues,
                            int[] segments, QualitySettings quality,
                            out string? errorMessage,
                            bool useConvexHull = true,
                            double maxBoundaryEdgeLength = 0,
                            Func<bool>? shouldCancel = null)
    {
        lock (_gate)
        {
            errorMessage = null;
            int vertexCount = xyCoords.Length / 2;
            if (vertexCount < 3)
            {
                errorMessage = $"Only {vertexCount} vertices provided (need at least 3).";
                return null;
            }

            if (ArePointsCollinear(xyCoords, vertexCount))
            {
                errorMessage = "All points are collinear (lie on a single line). A TIN requires non-collinear points.";
                return null;
            }

            ThrowIfCancellationRequested(shouldCancel);

            int xyHash = InputSnapshot.ComputeXyHash(xyCoords, segments, quality, useConvexHull, maxBoundaryEdgeLength);

            if (_cachedSnapshot != null && _cachedResult != null &&
                xyHash == _cachedSnapshot.XyHash &&
                zValues.Length == _cachedResult.VertexCount)
            {
                int zHash = InputSnapshot.ComputeZHash(zValues);
                if (zHash == _cachedSnapshot.ZHash)
                {
                    return _cachedResult;
                }

                _cachedSnapshot = new InputSnapshot(xyHash, zHash);
                _cachedResult = _cachedResult.WithUpdatedZ(zValues);
                return _cachedResult;
            }

            if (TryApplyIncrementalEdit(xyCoords, zValues, segments, quality, useConvexHull, maxBoundaryEdgeLength, out var incrementalResult))
            {
                int zHash = InputSnapshot.ComputeZHash(zValues);
                _cachedSnapshot = new InputSnapshot(xyHash, zHash);
                _cachedResult = incrementalResult;
                errorMessage = null;
                return incrementalResult;
            }

            ThrowIfCancellationRequested(shouldCancel);
            var result = FullRebuild(
                xyCoords, zValues, segments, quality,
                out errorMessage, out IMesh? builtMesh, useConvexHull, maxBoundaryEdgeLength, shouldCancel);
            if (result != null)
            {
                _cachedSnapshot = new InputSnapshot(xyHash, InputSnapshot.ComputeZHash(zValues));
                _cachedResult = result;
                _cachedMesh = builtMesh as Mesh;
                _topologyState = _cachedMesh != null
                    ? CreateTopologyState(_cachedMesh, xyCoords, segments, quality, useConvexHull)
                    : null;
            }
            return result;
        }
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cachedSnapshot = null;
            _cachedResult = null;
            _cachedMesh = null;
            _topologyState = null;
        }
    }

    private static Dictionary<XyKey, int> BuildInputIndexByKey(double[] xyCoords)
    {
        int count = xyCoords.Length / 2;
        var map = new Dictionary<XyKey, int>(count);
        for (int i = 0; i < count; i++)
        {
            var key = XyKey.FromValues(xyCoords[i * 2], xyCoords[i * 2 + 1]);
            map[key] = i;
        }
        return map;
    }

    private static bool AreSegmentsEqual(int[] a, int[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static IncrementalTopologyState? CreateTopologyState(
        Mesh mesh, double[] xyCoords, int[] segments, QualitySettings quality, bool useConvexHull)
    {
        var inputIndexByKey = BuildInputIndexByKey(xyCoords);
        if (inputIndexByKey.Count != xyCoords.Length / 2)
        {
            return null;
        }

        var meshIdByKey = new Dictionary<XyKey, int>(mesh.Vertices.Count);
        foreach (var v in mesh.Vertices)
        {
            meshIdByKey[XyKey.FromValues(v.X, v.Y)] = v.ID;
        }

        var vertexIdByKey = new Dictionary<XyKey, int>(inputIndexByKey.Count);
        foreach (var key in inputIndexByKey.Keys)
        {
            if (!meshIdByKey.TryGetValue(key, out int id))
            {
                return null;
            }
            vertexIdByKey[key] = id;
        }

        var protectedKeys = new HashSet<XyKey>();
        int inputVertexCount = xyCoords.Length / 2;
        int segCount = segments.Length / 2;
        for (int i = 0; i < segCount; i++)
        {
            int a = segments[i * 2];
            int b = segments[i * 2 + 1];
            if (a < 0 || b < 0 || a >= inputVertexCount || b >= inputVertexCount)
            {
                continue;
            }

            protectedKeys.Add(XyKey.FromValues(xyCoords[a * 2], xyCoords[a * 2 + 1]));
            protectedKeys.Add(XyKey.FromValues(xyCoords[b * 2], xyCoords[b * 2 + 1]));
        }

        return new IncrementalTopologyState(
            (int[])segments.Clone(),
            vertexIdByKey,
            protectedKeys,
            quality.HasConstraints,
            useConvexHull);
    }

    private bool TryApplyIncrementalEdit(
        double[] xyCoords, double[] zValues, int[] segments, QualitySettings quality, bool useConvexHull,
        double maxBoundaryEdgeLength,
        out TinResult? result)
    {
        result = null;

        if (_cachedMesh == null || _topologyState == null)
        {
            return false;
        }

        if (quality.HasConstraints || _topologyState.QualityConstrained)
        {
            return false;
        }

        if (_topologyState.UseConvexHull != useConvexHull)
        {
            return false;
        }

        if (!AreSegmentsEqual(_topologyState.Segments, segments))
        {
            return false;
        }

        var currentIndexByKey = BuildInputIndexByKey(xyCoords);
        var previousMap = _topologyState.VertexIdByKey;

        var removed = new List<XyKey>(1);
        foreach (var key in previousMap.Keys)
        {
            if (!currentIndexByKey.ContainsKey(key))
            {
                removed.Add(key);
                if (removed.Count > 1) return false;
            }
        }

        var added = new List<XyKey>(1);
        foreach (var key in currentIndexByKey.Keys)
        {
            if (!previousMap.ContainsKey(key))
            {
                added.Add(key);
                if (added.Count > 1) return false;
            }
        }

        if (added.Count == 0 && removed.Count == 0)
        {
            return false;
        }

        // Keep this path robust: only a single add or a single remove per solve.
        if (added.Count > 0 && removed.Count > 0)
        {
            return false;
        }

        if (removed.Count == 1)
        {
            var key = removed[0];
            if (_topologyState.ProtectedKeys.Contains(key))
            {
                return false;
            }

            if (!previousMap.TryGetValue(key, out int vertexId))
            {
                return false;
            }

            if (!_cachedMesh.CanDeletePoint(vertexId))
            {
                return false;
            }

            if (!_cachedMesh.TryDeletePoint(vertexId))
            {
                return false;
            }

            previousMap.Remove(key);
        }
        else if (added.Count == 1)
        {
            var key = added[0];
            double x = BitConverter.Int64BitsToDouble(key.XBits);
            double y = BitConverter.Int64BitsToDouble(key.YBits);

            if (!_cachedMesh.TryInsertPoint(x, y, out int vertexId))
            {
                return false;
            }

            previousMap[key] = vertexId;
        }

        result = BuildResult(_cachedMesh, xyCoords, zValues, segments, maxBoundaryEdgeLength);
        return true;
    }

    private static bool ArePointsCollinear(double[] xyCoords, int vertexCount)
    {
        if (vertexCount < 3) return true;

        double x0 = xyCoords[0], y0 = xyCoords[1];

        int secondIdx = -1;
        for (int i = 1; i < vertexCount; i++)
        {
            double dx = xyCoords[i * 2] - x0;
            double dy = xyCoords[i * 2 + 1] - y0;
            if (dx * dx + dy * dy > 1e-20)
            {
                secondIdx = i;
                break;
            }
        }
        if (secondIdx < 0) return true;

        double x1 = xyCoords[secondIdx * 2], y1 = xyCoords[secondIdx * 2 + 1];
        double baseLen = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

        for (int i = 1; i < vertexCount; i++)
        {
            if (i == secondIdx) continue;
            double xi = xyCoords[i * 2], yi = xyCoords[i * 2 + 1];
            double cross = Math.Abs((x1 - x0) * (yi - y0) - (y1 - y0) * (xi - x0));
            if (cross / baseLen > 1e-10) return false;
        }
        return true;
    }

    private static TinResult? FullRebuild(double[] xyCoords, double[] zValues,
                                           int[] segments, QualitySettings quality,
                                           out string? errorMessage, out IMesh? builtMesh,
                                           bool useConvexHull,
                                           double maxBoundaryEdgeLength,
                                           Func<bool>? shouldCancel = null)
    {
        builtMesh = null;
        errorMessage = null;
        int vertexCount = xyCoords.Length / 2;
        if (vertexCount < 3)
        {
            errorMessage = $"Only {vertexCount} vertices provided.";
            return null;
        }

        var polygon = new Polygon(vertexCount);
        var vertices = new Vertex[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            if ((i & 255) == 0)
                ThrowIfCancellationRequested(shouldCancel);

            var v = new Vertex(xyCoords[i * 2], xyCoords[i * 2 + 1]);
            v.ID = i;
            vertices[i] = v;
            polygon.Add(v);
        }

        int segCount = segments.Length / 2;
        for (int i = 0; i < segCount; i++)
        {
            if ((i & 255) == 0)
                ThrowIfCancellationRequested(shouldCancel);

            int a = segments[i * 2];
            int b = segments[i * 2 + 1];
            if (a >= 0 && a < vertexCount && b >= 0 && b < vertexCount && a != b)
            {
                polygon.Add(new Segment(vertices[a], vertices[b], 1), false);
            }
        }

        // Attempt 1: Conforming CDT with quality
        ThrowIfCancellationRequested(shouldCancel);
        var result = TryTriangulate(polygon, vertexCount, segCount, quality, conforming: true, out errorMessage, useConvexHull, shouldCancel);
        if (result != null)
        {
            builtMesh = result;
            return BuildResult(result, xyCoords, zValues, segments, maxBoundaryEdgeLength);
        }

        // Attempt 2: Non-conforming CDT with quality
        if (segCount > 0)
        {
            ThrowIfCancellationRequested(shouldCancel);
            var ncResult = TryTriangulate(polygon, vertexCount, segCount, quality, conforming: false, out _, useConvexHull, shouldCancel);
            if (ncResult != null)
            {
                builtMesh = ncResult;
                errorMessage = "Using non-conforming CDT for tightly spaced breaklines. Edges follow breaklines but mesh is not strictly Delaunay.";
                return BuildResult(ncResult, xyCoords, zValues, segments, maxBoundaryEdgeLength);
            }
        }

        // Attempt 3: Conforming CDT without quality
        if (quality.HasConstraints)
        {
            ThrowIfCancellationRequested(shouldCancel);
            var fallback = TryTriangulate(polygon, vertexCount, segCount, QualitySettings.None, conforming: true, out _, useConvexHull, shouldCancel);
            if (fallback != null)
            {
                builtMesh = fallback;
                errorMessage = "Quality constraints (max area / min angle) could not be applied. Using CDT without refinement.";
                return BuildResult(fallback, xyCoords, zValues, segments, maxBoundaryEdgeLength);
            }
        }

        // Attempt 4: Non-conforming CDT without quality
        if (segCount > 0 && quality.HasConstraints)
        {
            ThrowIfCancellationRequested(shouldCancel);
            var ncFallback = TryTriangulate(polygon, vertexCount, segCount, QualitySettings.None, conforming: false, out _, useConvexHull, shouldCancel);
            if (ncFallback != null)
            {
                builtMesh = ncFallback;
                errorMessage = "Using non-conforming CDT without quality constraints. Breakline edges preserved but no refinement.";
                return BuildResult(ncFallback, xyCoords, zValues, segments, maxBoundaryEdgeLength);
            }
        }

        // Attempt 5: Plain Delaunay (drops segments)
        if (segCount > 0)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (vertexCount > PlainFallbackVertexLimit || segCount > PlainFallbackSegmentLimit)
            {
                errorMessage = AppendBuildMessage(
                    errorMessage,
                    $"Skipped plain Delaunay fallback for large constrained input ({vertexCount:N0} verts, {segCount:N0} segments) to keep rebuilds responsive.");
                return null;
            }

            var plainOpts = new ConstraintOptions { ConformingDelaunay = false, Convex = false };
            var mesher = new GenericMesher();
            try
            {
                var plainPoly = new Polygon(vertexCount);
                for (int i = 0; i < vertexCount; i++)
                {
                    if ((i & 255) == 0)
                        ThrowIfCancellationRequested(shouldCancel);

                    plainPoly.Add(new Vertex(xyCoords[i * 2], xyCoords[i * 2 + 1]) { ID = i });
                }

                var mesh = mesher.Triangulate(plainPoly, plainOpts, null);
                if (mesh.Triangles.Count > 0)
                {
                    builtMesh = mesh;
                    errorMessage = segCount > 0
                        ? "Breakline constraints could not be enforced. Falling back to plain Delaunay."
                        : null;
                    return BuildResult(mesh, xyCoords, zValues, Array.Empty<int>(), maxBoundaryEdgeLength);
                }
            }
            catch { }
        }

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = xyCoords[i * 2], y = xyCoords[i * 2 + 1];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        double spanX = maxX - minX, spanY = maxY - minY;

        errorMessage = $"Triangulation failed with {vertexCount} vertices. "
            + $"XY extent: {spanX:G4} x {spanY:G4}. "
            + (spanX < 1e-6 || spanY < 1e-6
                ? "Points appear nearly collinear or coincident."
                : "Try increasing the deduplication tolerance.");
        return null;
    }

    private static IMesh? TryTriangulate(Polygon polygon, int vertexCount, int segCount,
                                          QualitySettings quality,
                                          bool conforming,
                                          out string? errorMessage,
                                          bool useConvexHull,
                                          Func<bool>? shouldCancel = null)
    {
        errorMessage = null;
        ThrowIfCancellationRequested(shouldCancel);

        var constraintOpts = new ConstraintOptions
        {
            ConformingDelaunay = conforming && segCount > 0,
            Convex = useConvexHull,
            SegmentSplitting = 0
        };

        TriangleNet.Meshing.QualityOptions? qualityOpts = null;
        if (quality.HasConstraints || (conforming && segCount > 0))
        {
            qualityOpts = new TriangleNet.Meshing.QualityOptions();
            if (quality.MaxArea > 0)
                qualityOpts.MaximumArea = quality.MaxArea;
            if (quality.MinAngle > 0)
                qualityOpts.MinimumAngle = quality.MinAngle;
            qualityOpts.SteinerPoints = ComputeSteinerPointCap(vertexCount, segCount, quality, conforming);
        }

        var mesher = new GenericMesher();
        IMesh mesh;
        try
        {
            mesh = mesher.Triangulate(polygon, constraintOpts, qualityOpts);
        }
        catch (Exception ex)
        {
            errorMessage = $"Triangle.NET: {ex.Message}";
            return null;
        }

        if (mesh.Triangles.Count == 0)
        {
            errorMessage = "Triangulation produced 0 triangles.";
            return null;
        }

        return mesh;
    }

    private static int ComputeSteinerPointCap(int vertexCount, int segCount, QualitySettings quality, bool conforming)
    {
        int complexity = Math.Max(vertexCount + Math.Max(segCount, 0), vertexCount);
        int multiplier = quality.HasConstraints ? SteinerPointCapMultiplier : Math.Max(4, SteinerPointCapMultiplier / 2);
        if (!conforming)
            multiplier = Math.Max(2, multiplier / 2);

        long cap = (long)complexity * multiplier;
        cap = Math.Max(cap, MinSteinerPointCap);
        cap = Math.Min(cap, MaxSteinerPointCap);
        return (int)cap;
    }

    private static string AppendBuildMessage(string? existing, string addition)
    {
        if (string.IsNullOrWhiteSpace(existing))
            return addition;
        if (string.IsNullOrWhiteSpace(addition))
            return existing;
        return $"{existing} {addition}";
    }

    private static void ThrowIfCancellationRequested(Func<bool>? shouldCancel)
    {
        if (shouldCancel?.Invoke() == true)
            throw new OperationCanceledException("Triangulation cancelled.");
    }

    private static TinResult BuildResult(IMesh mesh, double[] xyCoords, double[] zValues, int[] segments, double maxBoundaryEdgeLength)
    {
        var extracted = TriangleNetExtractor.Extract(mesh);
        int outVertexCount = extracted.VertexCount;
        var outVerts = new double[outVertexCount * 3];
        int inputVertexCount = xyCoords.Length / 2;

        var inputZByKey = new Dictionary<XyKey, double>(inputVertexCount);
        for (int i = 0; i < inputVertexCount; i++)
        {
            var key = XyKey.FromValues(xyCoords[i * 2], xyCoords[i * 2 + 1]);
            inputZByKey[key] = zValues[i];
        }

        var steinerIndices = new List<int>();
        for (int outIndex = 0; outIndex < outVertexCount; outIndex++)
        {
            double x = extracted.Xy[outIndex * 2];
            double y = extracted.Xy[outIndex * 2 + 1];

            outVerts[outIndex * 3] = x;
            outVerts[outIndex * 3 + 1] = y;

            if (inputZByKey.TryGetValue(XyKey.FromValues(x, y), out double z) && !double.IsNaN(z))
            {
                outVerts[outIndex * 3 + 2] = z;
            }
            else
            {
                outVerts[outIndex * 3 + 2] = double.NaN;
                steinerIndices.Add(outIndex);
            }
        }

        int faceCount = extracted.FaceCount;
        int[] outFaces = extracted.Faces;
        var topology = IndexedMeshTools.BuildEdgeTopology(outFaces, faceCount);
        int[] edgeList = topology.Edges;
        int edgeCount = topology.EdgeCount;

        if (steinerIndices.Count > 0)
        {
            InterpolateSteinerZ(outVerts, outVertexCount,
                                edgeList, edgeCount,
                                outFaces, faceCount,
                                steinerIndices,
                                xyCoords, zValues, segments);
        }

        var cullResult = TriangleBoundaryCuller.Cull(
            outVerts,
            outVertexCount,
            outFaces,
            faceCount,
            xyCoords,
            segments,
            maxBoundaryEdgeLength);

        if (cullResult.Changed)
        {
            outVerts = IndexedMeshTools.CompactDoubleData(outVerts, 3, cullResult.NewToOld, cullResult.VertexCount);
            outFaces = cullResult.Faces;
            outVertexCount = cullResult.VertexCount;
            faceCount = cullResult.FaceCount;
            topology = IndexedMeshTools.BuildEdgeTopology(outFaces, faceCount);
            edgeList = topology.Edges;
            edgeCount = topology.EdgeCount;
        }

        return new TinResult(
            outVerts, outVertexCount,
            outFaces, faceCount,
            edgeList, edgeCount,
            topology.NakedEdges, topology.NakedEdgeCount);
    }

    private static void InterpolateSteinerZ(double[] verts, int vertCount,
                                             int[] edges, int edgeCount,
                                             int[] faces, int faceCount,
                                             List<int> steinerIndices,
                                             double[] inputXy, double[] inputZ, int[] inputSegments)
    {
        const double eps = 1e-9;
        int inputVertCount = inputXy.Length / 2;
        int inputSegCount = inputSegments.Length / 2;

        if (steinerIndices.Count == 0)
            return;

        void GatherCandidatesOrAll(
            SpatialHashGrid2D index,
            in Bounds2D queryBounds,
            List<int> candidates,
            SpatialHashGrid2D.QueryScratch scratch,
            int itemCount)
        {
            index.GatherCandidates(queryBounds, candidates, scratch);
            if (candidates.Count == 0)
            {
                for (int i = 0; i < itemCount; i++)
                    candidates.Add(i);
            }
        }

        var inputSegmentInfos = new List<SegmentInfo>(inputSegCount);

        for (int s = 0; s < inputSegCount; s++)
        {
            int a = inputSegments[s * 2];
            int b = inputSegments[s * 2 + 1];

            if (a < 0 || a >= inputVertCount || b < 0 || b >= inputVertCount || a == b)
                continue;

            double x0 = inputXy[a * 2];
            double y0 = inputXy[a * 2 + 1];
            double x1 = inputXy[b * 2];
            double y1 = inputXy[b * 2 + 1];
            double dx = x1 - x0;
            double dy = y1 - y0;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < eps * eps)
                continue;

            double tol = Math.Sqrt(lenSq) * 1e-6 + 1e-8;
            inputSegmentInfos.Add(new SegmentInfo(
                a,
                b,
                x0,
                y0,
                dx,
                dy,
                lenSq,
                tol * tol,
                new Bounds2D(
                    Math.Min(x0, x1) - tol,
                    Math.Max(x0, x1) + tol,
                    Math.Min(y0, y1) - tol,
                    Math.Max(y0, y1) + tol)));
        }

        var edgeInfos = new List<SegmentInfo>(edgeCount);

        for (int e = 0; e < edgeCount; e++)
        {
            int i0 = edges[e * 2];
            int i1 = edges[e * 2 + 1];

            if (i0 < 0 || i0 >= vertCount || i1 < 0 || i1 >= vertCount || i0 == i1)
                continue;

            double x0 = verts[i0 * 3];
            double y0 = verts[i0 * 3 + 1];
            double x1 = verts[i1 * 3];
            double y1 = verts[i1 * 3 + 1];
            double dx = x1 - x0;
            double dy = y1 - y0;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < eps * eps)
                continue;

            double tol = eps * (Math.Sqrt(lenSq) + 1.0);
            edgeInfos.Add(new SegmentInfo(
                i0,
                i1,
                x0,
                y0,
                dx,
                dy,
                lenSq,
                tol * tol,
                new Bounds2D(
                    Math.Min(x0, x1) - tol,
                    Math.Max(x0, x1) + tol,
                    Math.Min(y0, y1) - tol,
                    Math.Max(y0, y1) + tol)));
        }

        var faceInfos = new List<FaceInfo>(faceCount);

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];

            if (i0 < 0 || i0 >= vertCount || i1 < 0 || i1 >= vertCount || i2 < 0 || i2 >= vertCount)
                continue;

            double x0 = verts[i0 * 3];
            double y0 = verts[i0 * 3 + 1];
            double x1 = verts[i1 * 3];
            double y1 = verts[i1 * 3 + 1];
            double x2 = verts[i2 * 3];
            double y2 = verts[i2 * 3 + 1];

            double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
            if (Math.Abs(denom) < eps)
                continue;

            faceInfos.Add(new FaceInfo(
                i0,
                i1,
                i2,
                x0,
                y0,
                x1,
                y1,
                x2,
                y2,
                denom,
                new Bounds2D(
                    Math.Min(x0, Math.Min(x1, x2)) - eps,
                    Math.Max(x0, Math.Max(x1, x2)) + eps,
                    Math.Min(y0, Math.Min(y1, y2)) - eps,
                    Math.Max(y0, Math.Max(y1, y2)) + eps)));
        }

        var inputSegBounds = new Bounds2D[inputSegmentInfos.Count];
        for (int i = 0; i < inputSegmentInfos.Count; i++)
            inputSegBounds[i] = inputSegmentInfos[i].Bounds;

        var edgeBounds = new Bounds2D[edgeInfos.Count];
        for (int i = 0; i < edgeInfos.Count; i++)
            edgeBounds[i] = edgeInfos[i].Bounds;

        var faceBounds = new Bounds2D[faceInfos.Count];
        for (int i = 0; i < faceInfos.Count; i++)
            faceBounds[i] = faceInfos[i].Bounds;

        var inputSegIndex = SpatialHashGrid2D.Build(inputSegBounds);
        var edgeIndex = SpatialHashGrid2D.Build(edgeBounds);
        var faceIndex = SpatialHashGrid2D.Build(faceBounds);
        var inputSegScratch = new SpatialHashGrid2D.QueryScratch(inputSegmentInfos.Count);
        var edgeScratch = new SpatialHashGrid2D.QueryScratch(edgeInfos.Count);
        var faceScratch = new SpatialHashGrid2D.QueryScratch(faceInfos.Count);

        var inputSegCandidates = new List<int>(32);
        var edgeCandidates = new List<int>(32);
        var faceCandidates = new List<int>(32);

        bool TryResolveFromInputSegments(int si)
        {
            if (inputSegmentInfos.Count == 0)
                return false;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];
            GatherCandidatesOrAll(
                inputSegIndex,
                Bounds2D.FromPoint(px, py),
                inputSegCandidates,
                inputSegScratch,
                inputSegmentInfos.Count);

            foreach (int s in inputSegCandidates)
            {
                SegmentInfo segment = inputSegmentInfos[s];

                double t = ((px - segment.X0) * segment.Dx + (py - segment.Y0) * segment.Dy) / segment.LengthSquared;
                if (t < -0.001 || t > 1.001) continue;

                double projX = segment.X0 + t * segment.Dx;
                double projY = segment.Y0 + t * segment.Dy;
                double ddx = px - projX;
                double ddy = py - projY;
                double distSq = ddx * ddx + ddy * ddy;
                if (distSq >= segment.ToleranceSquared) continue;

                t = Math.Max(0, Math.Min(1, t));
                int a = segment.A;
                int b = segment.B;
                verts[si * 3 + 2] = inputZ[a] + t * (inputZ[b] - inputZ[a]);
                return true;
            }

            return false;
        }

        bool TryResolveFromEdges(int si)
        {
            if (edgeInfos.Count == 0)
                return false;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];
            GatherCandidatesOrAll(
                edgeIndex,
                Bounds2D.FromPoint(px, py),
                edgeCandidates,
                edgeScratch,
                edgeInfos.Count);

            foreach (int e in edgeCandidates)
            {
                SegmentInfo segment = edgeInfos[e];

                int i0 = segment.A;
                int i1 = segment.B;
                double z0 = verts[i0 * 3 + 2];
                double z1 = verts[i1 * 3 + 2];
                if (double.IsNaN(z0) || double.IsNaN(z1)) continue;

                double t = ((px - segment.X0) * segment.Dx + (py - segment.Y0) * segment.Dy) / segment.LengthSquared;
                if (t < -eps || t > 1.0 + eps) continue;

                double projX = segment.X0 + t * segment.Dx;
                double projY = segment.Y0 + t * segment.Dy;
                double ddx = px - projX;
                double ddy = py - projY;
                double distSq = ddx * ddx + ddy * ddy;
                if (distSq >= segment.ToleranceSquared) continue;

                t = Math.Max(0, Math.Min(1, t));
                verts[si * 3 + 2] = z0 + t * (z1 - z0);
                return true;
            }

            return false;
        }

        bool TryResolveFromFaces(int si)
        {
            if (faceInfos.Count == 0)
                return false;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];
            GatherCandidatesOrAll(
                faceIndex,
                Bounds2D.FromPoint(px, py),
                faceCandidates,
                faceScratch,
                faceInfos.Count);

            foreach (int f in faceCandidates)
            {
                FaceInfo face = faceInfos[f];

                int i0 = face.I0;
                int i1 = face.I1;
                int i2 = face.I2;
                double z0 = verts[i0 * 3 + 2];
                double z1 = verts[i1 * 3 + 2];
                double z2 = verts[i2 * 3 + 2];
                if (double.IsNaN(z0) || double.IsNaN(z1) || double.IsNaN(z2)) continue;

                double denom = face.Denominator;
                double w0 = ((face.Y1 - face.Y2) * (px - face.X2) + (face.X2 - face.X1) * (py - face.Y2)) / denom;
                double w1 = ((face.Y2 - face.Y0) * (px - face.X2) + (face.X0 - face.X2) * (py - face.Y2)) / denom;
                double w2 = 1.0 - w0 - w1;

                if (w0 < -1e-4 || w1 < -1e-4 || w2 < -1e-4) continue;

                verts[si * 3 + 2] = w0 * z0 + w1 * z1 + w2 * z2;
                return true;
            }

            return false;
        }

        // Priority 1: interpolate from input breakline segments.
        foreach (int si in steinerIndices)
        {
            if (!double.IsNaN(verts[si * 3 + 2])) continue;
            TryResolveFromInputSegments(si);
        }

        // Priority 2: interpolate from output edges.
        foreach (int si in steinerIndices)
        {
            if (!double.IsNaN(verts[si * 3 + 2])) continue;
            TryResolveFromEdges(si);
        }

        // Priority 3: interpolate from containing face.
        foreach (int si in steinerIndices)
        {
            if (!double.IsNaN(verts[si * 3 + 2])) continue;
            TryResolveFromFaces(si);
        }

        // Priority 4: iterative propagation for chain dependencies.
        int maxIter = steinerIndices.Count + 1;
        for (int iter = 0; iter < maxIter; iter++)
        {
            int resolved = 0;
            foreach (int si in steinerIndices)
            {
                if (!double.IsNaN(verts[si * 3 + 2])) continue;

                if (TryResolveFromEdges(si) || TryResolveFromFaces(si))
                    resolved++;
            }

            if (resolved == 0) break;
        }

        // Priority 5: nearest known vertex fallback.
        var knownVertexIndices = new List<int>(vertCount);
        for (int i = 0; i < vertCount; i++)
        {
            if (!double.IsNaN(verts[i * 3 + 2]))
                knownVertexIndices.Add(i);
        }

        foreach (int si in steinerIndices)
        {
            if (!double.IsNaN(verts[si * 3 + 2])) continue;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];
            double nearestZ = 0;
            double nearestDistSq = double.MaxValue;

            foreach (int i in knownVertexIndices)
            {
                double dx = verts[i * 3] - px;
                double dy = verts[i * 3 + 1] - py;
                double distSq = dx * dx + dy * dy;

                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearestZ = verts[i * 3 + 2];
                }
            }

            verts[si * 3 + 2] = nearestZ;
        }
    }
}
