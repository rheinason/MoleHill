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
    private InputSnapshot? _cachedSnapshot;
    private TinResult? _cachedResult;
    private Mesh? _cachedMesh;
    private IncrementalTopologyState? _topologyState;

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

        public IncrementalTopologyState(
            int[] segments,
            Dictionary<XyKey, int> vertexIdByKey,
            HashSet<XyKey> protectedKeys,
            bool qualityConstrained)
        {
            Segments = segments;
            VertexIdByKey = vertexIdByKey;
            ProtectedKeys = protectedKeys;
            QualityConstrained = qualityConstrained;
        }
    }

    public TinResult? Build(double[] xyCoords, double[] zValues,
                            int[] segments, QualitySettings quality,
                            out string? errorMessage)
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

        int xyHash = InputSnapshot.ComputeXyHash(xyCoords, segments, quality);

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

        if (TryApplyIncrementalEdit(xyCoords, zValues, segments, quality, out var incrementalResult))
        {
            int zHash = InputSnapshot.ComputeZHash(zValues);
            _cachedSnapshot = new InputSnapshot(xyHash, zHash);
            _cachedResult = incrementalResult;
            errorMessage = null;
            return incrementalResult;
        }

        var result = FullRebuild(
            xyCoords, zValues, segments, quality,
            out errorMessage, out IMesh? builtMesh);
        if (result != null)
        {
            _cachedSnapshot = new InputSnapshot(xyHash, InputSnapshot.ComputeZHash(zValues));
            _cachedResult = result;
            _cachedMesh = builtMesh as Mesh;
            _topologyState = _cachedMesh != null
                ? CreateTopologyState(_cachedMesh, xyCoords, segments, quality)
                : null;
        }
        return result;
    }

    public void InvalidateCache()
    {
        _cachedSnapshot = null;
        _cachedResult = null;
        _cachedMesh = null;
        _topologyState = null;
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
        Mesh mesh, double[] xyCoords, int[] segments, QualitySettings quality)
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
            quality.HasConstraints);
    }

    private bool TryApplyIncrementalEdit(
        double[] xyCoords, double[] zValues, int[] segments, QualitySettings quality,
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

        result = BuildResult(_cachedMesh, xyCoords, zValues, segments);
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
                                           out string? errorMessage, out IMesh? builtMesh)
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
            var v = new Vertex(xyCoords[i * 2], xyCoords[i * 2 + 1]);
            v.ID = i;
            vertices[i] = v;
            polygon.Add(v);
        }

        int segCount = segments.Length / 2;
        for (int i = 0; i < segCount; i++)
        {
            int a = segments[i * 2];
            int b = segments[i * 2 + 1];
            if (a >= 0 && a < vertexCount && b >= 0 && b < vertexCount && a != b)
            {
                polygon.Add(new Segment(vertices[a], vertices[b], 1), false);
            }
        }

        // Attempt 1: Conforming CDT with quality
        var result = TryTriangulate(polygon, segCount, quality, conforming: true, out errorMessage);
        if (result != null)
        {
            builtMesh = result;
            return BuildResult(result, xyCoords, zValues, segments);
        }

        // Attempt 2: Non-conforming CDT with quality
        if (segCount > 0)
        {
            var ncResult = TryTriangulate(polygon, segCount, quality, conforming: false, out _);
            if (ncResult != null)
            {
                builtMesh = ncResult;
                errorMessage = "Using non-conforming CDT for tightly spaced breaklines. Edges follow breaklines but mesh is not strictly Delaunay.";
                return BuildResult(ncResult, xyCoords, zValues, segments);
            }
        }

        // Attempt 3: Conforming CDT without quality
        if (quality.HasConstraints)
        {
            var fallback = TryTriangulate(polygon, segCount, QualitySettings.None, conforming: true, out _);
            if (fallback != null)
            {
                builtMesh = fallback;
                errorMessage = "Quality constraints (max area / min angle) could not be applied. Using CDT without refinement.";
                return BuildResult(fallback, xyCoords, zValues, segments);
            }
        }

        // Attempt 4: Non-conforming CDT without quality
        if (segCount > 0)
        {
            var ncFallback = TryTriangulate(polygon, segCount, QualitySettings.None, conforming: false, out _);
            if (ncFallback != null)
            {
                builtMesh = ncFallback;
                errorMessage = "Using non-conforming CDT without quality constraints. Breakline edges preserved but no refinement.";
                return BuildResult(ncFallback, xyCoords, zValues, segments);
            }
        }

        // Attempt 5: Plain Delaunay (drops segments)
        {
            var plainOpts = new ConstraintOptions { ConformingDelaunay = false, Convex = false };
            var mesher = new GenericMesher();
            try
            {
                var plainPoly = new Polygon(vertexCount);
                for (int i = 0; i < vertexCount; i++)
                    plainPoly.Add(new Vertex(xyCoords[i * 2], xyCoords[i * 2 + 1]) { ID = i });

                var mesh = mesher.Triangulate(plainPoly, plainOpts, null);
                if (mesh.Triangles.Count > 0)
                {
                    builtMesh = mesh;
                    errorMessage = segCount > 0
                        ? "Breakline constraints could not be enforced. Falling back to plain Delaunay."
                        : null;
                    return BuildResult(mesh, xyCoords, zValues, Array.Empty<int>());
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

    private static IMesh? TryTriangulate(Polygon polygon, int segCount,
                                          QualitySettings quality,
                                          bool conforming,
                                          out string? errorMessage)
    {
        errorMessage = null;

        var constraintOpts = new ConstraintOptions
        {
            ConformingDelaunay = conforming && segCount > 0,
            Convex = true
        };

        TriangleNet.Meshing.QualityOptions? qualityOpts = null;
        if (quality.HasConstraints)
        {
            qualityOpts = new TriangleNet.Meshing.QualityOptions();
            if (quality.MaxArea > 0)
                qualityOpts.MaximumArea = quality.MaxArea;
            if (quality.MinAngle > 0)
                qualityOpts.MinimumAngle = quality.MinAngle;
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

    private static TinResult BuildResult(IMesh mesh, double[] xyCoords, double[] zValues, int[] segments)
    {
        var meshVertices = mesh.Vertices;
        var triangles = mesh.Triangles;
        int outVertexCount = meshVertices.Count;
        var outVerts = new double[outVertexCount * 3];
        int inputVertexCount = xyCoords.Length / 2;

        var inputZByKey = new Dictionary<XyKey, double>(inputVertexCount);
        for (int i = 0; i < inputVertexCount; i++)
        {
            var key = XyKey.FromValues(xyCoords[i * 2], xyCoords[i * 2 + 1]);
            inputZByKey[key] = zValues[i];
        }

        var idToOutIndex = new Dictionary<int, int>(outVertexCount);
        var steinerIndices = new List<int>();

        int outIndex = 0;
        foreach (var v in meshVertices)
        {
            idToOutIndex[v.ID] = outIndex;

            outVerts[outIndex * 3] = v.X;
            outVerts[outIndex * 3 + 1] = v.Y;

            if (inputZByKey.TryGetValue(XyKey.FromValues(v.X, v.Y), out double z))
            {
                outVerts[outIndex * 3 + 2] = z;
            }
            else
            {
                outVerts[outIndex * 3 + 2] = double.NaN;
                steinerIndices.Add(outIndex);
            }

            outIndex++;
        }

        int faceCount = triangles.Count;
        var outFaces = new int[faceCount * 3];

        int faceIndex = 0;
        foreach (var tri in triangles)
        {
            outFaces[faceIndex * 3] = idToOutIndex.GetValueOrDefault(tri.GetVertex(0).ID, 0);
            outFaces[faceIndex * 3 + 1] = idToOutIndex.GetValueOrDefault(tri.GetVertex(1).ID, 0);
            outFaces[faceIndex * 3 + 2] = idToOutIndex.GetValueOrDefault(tri.GetVertex(2).ID, 0);
            faceIndex++;
        }

        var edges = mesh.Edges.ToList();
        var edgeList = new int[edges.Count * 2];
        for (int i = 0; i < edges.Count; i++)
        {
            edgeList[i * 2] = idToOutIndex.GetValueOrDefault(edges[i].P0, 0);
            edgeList[i * 2 + 1] = idToOutIndex.GetValueOrDefault(edges[i].P1, 0);
        }

        if (steinerIndices.Count > 0)
        {
            InterpolateSteinerZ(outVerts, outVertexCount,
                                edgeList, edges.Count,
                                outFaces, faceCount,
                                steinerIndices,
                                xyCoords, zValues, segments);
        }

        // Build naked edge list
        var nakedEdgeList = new List<int>();
        var edgeTriCount = new Dictionary<(int, int), int>();
        foreach (var tri in triangles)
        {
            for (int e = 0; e < 3; e++)
            {
                var va = tri.GetVertex(e);
                var vb = tri.GetVertex((e + 1) % 3);
                var key = va.ID < vb.ID ? (va.ID, vb.ID) : (vb.ID, va.ID);
                edgeTriCount[key] = edgeTriCount.GetValueOrDefault(key, 0) + 1;
            }
        }

        for (int i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            var key = edge.P0 < edge.P1 ? (edge.P0, edge.P1) : (edge.P1, edge.P0);
            if (edgeTriCount.GetValueOrDefault(key, 0) < 2)
            {
                nakedEdgeList.Add(edgeList[i * 2]);
                nakedEdgeList.Add(edgeList[i * 2 + 1]);
            }
        }

        return new TinResult(
            outVerts, outVertexCount,
            outFaces, faceCount,
            edgeList, edges.Count,
            nakedEdgeList.ToArray(), nakedEdgeList.Count / 2);
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

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < vertCount; i++)
        {
            double x = verts[i * 3];
            double y = verts[i * 3 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double ChooseCellSize(int candidateCount)
        {
            if (candidateCount <= 0) return 1.0;

            double spanX = maxX - minX;
            double spanY = maxY - minY;
            double span = Math.Max(spanX, spanY);
            if (span <= 0 || double.IsNaN(span) || double.IsInfinity(span))
                return 1.0;

            return Math.Max(span / Math.Max(8.0, Math.Sqrt(candidateCount)), 1e-9);
        }

        Dictionary<(long, long), List<int>> BuildSpatialIndex(
            double[] minXs, double[] maxXs, double[] minYs, double[] maxYs,
            bool[] valid, int count, double invCell)
        {
            var index = new Dictionary<(long, long), List<int>>(Math.Max(16, count));
            for (int i = 0; i < count; i++)
            {
                if (!valid[i]) continue;

                long cminX = (long)Math.Floor((minXs[i] - minX) * invCell);
                long cmaxX = (long)Math.Floor((maxXs[i] - minX) * invCell);
                long cminY = (long)Math.Floor((minYs[i] - minY) * invCell);
                long cmaxY = (long)Math.Floor((maxYs[i] - minY) * invCell);

                for (long cx = cminX; cx <= cmaxX; cx++)
                {
                    for (long cy = cminY; cy <= cmaxY; cy++)
                    {
                        var key = (cx, cy);
                        if (!index.TryGetValue(key, out var list))
                        {
                            list = new List<int>(4);
                            index[key] = list;
                        }
                        list.Add(i);
                    }
                }
            }
            return index;
        }

        void FillCandidates(
            Dictionary<(long, long), List<int>> index,
            double x, double y, double invCell,
            int[] marks, ref int stamp, int totalCount,
            List<int> candidates)
        {
            candidates.Clear();
            if (totalCount == 0)
                return;

            if (index.Count == 0)
            {
                for (int i = 0; i < totalCount; i++) candidates.Add(i);
                return;
            }

            if (stamp == int.MaxValue)
            {
                Array.Clear(marks, 0, marks.Length);
                stamp = 1;
            }
            else
            {
                stamp++;
            }

            long cx = (long)Math.Floor((x - minX) * invCell);
            long cy = (long)Math.Floor((y - minY) * invCell);
            int currentStamp = stamp;

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!index.TryGetValue((cx + dx, cy + dy), out var list))
                        continue;

                    foreach (int candidate in list)
                    {
                        if (marks[candidate] == currentStamp) continue;
                        marks[candidate] = currentStamp;
                        candidates.Add(candidate);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                for (int i = 0; i < totalCount; i++) candidates.Add(i);
            }
        }

        var inputSegValid = new bool[inputSegCount];
        var inputSegA = new int[inputSegCount];
        var inputSegB = new int[inputSegCount];
        var inputSegX0 = new double[inputSegCount];
        var inputSegY0 = new double[inputSegCount];
        var inputSegDx = new double[inputSegCount];
        var inputSegDy = new double[inputSegCount];
        var inputSegLenSq = new double[inputSegCount];
        var inputSegTolSq = new double[inputSegCount];
        var inputSegMinX = new double[inputSegCount];
        var inputSegMaxX = new double[inputSegCount];
        var inputSegMinY = new double[inputSegCount];
        var inputSegMaxY = new double[inputSegCount];

        for (int s = 0; s < inputSegCount; s++)
        {
            int a = inputSegments[s * 2];
            int b = inputSegments[s * 2 + 1];
            inputSegA[s] = a;
            inputSegB[s] = b;

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

            inputSegX0[s] = x0;
            inputSegY0[s] = y0;
            inputSegDx[s] = dx;
            inputSegDy[s] = dy;
            inputSegLenSq[s] = lenSq;

            double tol = Math.Sqrt(lenSq) * 1e-6 + 1e-8;
            double tolSq = tol * tol;
            inputSegTolSq[s] = tolSq;

            inputSegMinX[s] = Math.Min(x0, x1) - tol;
            inputSegMaxX[s] = Math.Max(x0, x1) + tol;
            inputSegMinY[s] = Math.Min(y0, y1) - tol;
            inputSegMaxY[s] = Math.Max(y0, y1) + tol;
            inputSegValid[s] = true;
        }

        var edgeValid = new bool[edgeCount];
        var edgeI0 = new int[edgeCount];
        var edgeI1 = new int[edgeCount];
        var edgeX0 = new double[edgeCount];
        var edgeY0 = new double[edgeCount];
        var edgeDx = new double[edgeCount];
        var edgeDy = new double[edgeCount];
        var edgeLenSq = new double[edgeCount];
        var edgeTolSq = new double[edgeCount];
        var edgeMinX = new double[edgeCount];
        var edgeMaxX = new double[edgeCount];
        var edgeMinY = new double[edgeCount];
        var edgeMaxY = new double[edgeCount];

        for (int e = 0; e < edgeCount; e++)
        {
            int i0 = edges[e * 2];
            int i1 = edges[e * 2 + 1];
            edgeI0[e] = i0;
            edgeI1[e] = i1;

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

            edgeX0[e] = x0;
            edgeY0[e] = y0;
            edgeDx[e] = dx;
            edgeDy[e] = dy;
            edgeLenSq[e] = lenSq;

            double tol = eps * (Math.Sqrt(lenSq) + 1.0);
            double tolSq = tol * tol;
            edgeTolSq[e] = tolSq;

            edgeMinX[e] = Math.Min(x0, x1) - tol;
            edgeMaxX[e] = Math.Max(x0, x1) + tol;
            edgeMinY[e] = Math.Min(y0, y1) - tol;
            edgeMaxY[e] = Math.Max(y0, y1) + tol;
            edgeValid[e] = true;
        }

        var faceValid = new bool[faceCount];
        var faceI0 = new int[faceCount];
        var faceI1 = new int[faceCount];
        var faceI2 = new int[faceCount];
        var faceX0 = new double[faceCount];
        var faceY0 = new double[faceCount];
        var faceX1 = new double[faceCount];
        var faceY1 = new double[faceCount];
        var faceX2 = new double[faceCount];
        var faceY2 = new double[faceCount];
        var faceDenom = new double[faceCount];
        var faceMinX = new double[faceCount];
        var faceMaxX = new double[faceCount];
        var faceMinY = new double[faceCount];
        var faceMaxY = new double[faceCount];

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            faceI0[f] = i0;
            faceI1[f] = i1;
            faceI2[f] = i2;

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

            faceX0[f] = x0;
            faceY0[f] = y0;
            faceX1[f] = x1;
            faceY1[f] = y1;
            faceX2[f] = x2;
            faceY2[f] = y2;
            faceDenom[f] = denom;

            faceMinX[f] = Math.Min(x0, Math.Min(x1, x2)) - eps;
            faceMaxX[f] = Math.Max(x0, Math.Max(x1, x2)) + eps;
            faceMinY[f] = Math.Min(y0, Math.Min(y1, y2)) - eps;
            faceMaxY[f] = Math.Max(y0, Math.Max(y1, y2)) + eps;
            faceValid[f] = true;
        }

        double inputSegInvCell = 1.0 / ChooseCellSize(inputSegCount);
        double edgeInvCell = 1.0 / ChooseCellSize(edgeCount);
        double faceInvCell = 1.0 / ChooseCellSize(faceCount);

        var inputSegIndex = BuildSpatialIndex(
            inputSegMinX, inputSegMaxX, inputSegMinY, inputSegMaxY,
            inputSegValid, inputSegCount, inputSegInvCell);
        var edgeIndex = BuildSpatialIndex(
            edgeMinX, edgeMaxX, edgeMinY, edgeMaxY,
            edgeValid, edgeCount, edgeInvCell);
        var faceIndex = BuildSpatialIndex(
            faceMinX, faceMaxX, faceMinY, faceMaxY,
            faceValid, faceCount, faceInvCell);

        var inputSegMarks = new int[inputSegCount];
        var edgeMarks = new int[edgeCount];
        var faceMarks = new int[faceCount];
        int inputSegStamp = 0, edgeStamp = 0, faceStamp = 0;

        var inputSegCandidates = new List<int>(32);
        var edgeCandidates = new List<int>(32);
        var faceCandidates = new List<int>(32);

        bool TryResolveFromInputSegments(int si)
        {
            if (inputSegCount == 0)
                return false;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];
            FillCandidates(
                inputSegIndex, px, py, inputSegInvCell,
                inputSegMarks, ref inputSegStamp, inputSegCount,
                inputSegCandidates);

            foreach (int s in inputSegCandidates)
            {
                if (!inputSegValid[s]) continue;

                double t = ((px - inputSegX0[s]) * inputSegDx[s] + (py - inputSegY0[s]) * inputSegDy[s]) / inputSegLenSq[s];
                if (t < -0.001 || t > 1.001) continue;

                double projX = inputSegX0[s] + t * inputSegDx[s];
                double projY = inputSegY0[s] + t * inputSegDy[s];
                double ddx = px - projX;
                double ddy = py - projY;
                double distSq = ddx * ddx + ddy * ddy;
                if (distSq >= inputSegTolSq[s]) continue;

                t = Math.Max(0, Math.Min(1, t));
                int a = inputSegA[s];
                int b = inputSegB[s];
                verts[si * 3 + 2] = inputZ[a] + t * (inputZ[b] - inputZ[a]);
                return true;
            }

            return false;
        }

        bool TryResolveFromEdges(int si)
        {
            if (edgeCount == 0)
                return false;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];
            FillCandidates(
                edgeIndex, px, py, edgeInvCell,
                edgeMarks, ref edgeStamp, edgeCount,
                edgeCandidates);

            foreach (int e in edgeCandidates)
            {
                if (!edgeValid[e]) continue;

                int i0 = edgeI0[e];
                int i1 = edgeI1[e];
                double z0 = verts[i0 * 3 + 2];
                double z1 = verts[i1 * 3 + 2];
                if (double.IsNaN(z0) || double.IsNaN(z1)) continue;

                double t = ((px - edgeX0[e]) * edgeDx[e] + (py - edgeY0[e]) * edgeDy[e]) / edgeLenSq[e];
                if (t < -eps || t > 1.0 + eps) continue;

                double projX = edgeX0[e] + t * edgeDx[e];
                double projY = edgeY0[e] + t * edgeDy[e];
                double ddx = px - projX;
                double ddy = py - projY;
                double distSq = ddx * ddx + ddy * ddy;
                if (distSq >= edgeTolSq[e]) continue;

                t = Math.Max(0, Math.Min(1, t));
                verts[si * 3 + 2] = z0 + t * (z1 - z0);
                return true;
            }

            return false;
        }

        bool TryResolveFromFaces(int si)
        {
            if (faceCount == 0)
                return false;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];
            FillCandidates(
                faceIndex, px, py, faceInvCell,
                faceMarks, ref faceStamp, faceCount,
                faceCandidates);

            foreach (int f in faceCandidates)
            {
                if (!faceValid[f]) continue;

                int i0 = faceI0[f];
                int i1 = faceI1[f];
                int i2 = faceI2[f];
                double z0 = verts[i0 * 3 + 2];
                double z1 = verts[i1 * 3 + 2];
                double z2 = verts[i2 * 3 + 2];
                if (double.IsNaN(z0) || double.IsNaN(z1) || double.IsNaN(z2)) continue;

                double denom = faceDenom[f];
                double w0 = ((faceY1[f] - faceY2[f]) * (px - faceX2[f]) + (faceX2[f] - faceX1[f]) * (py - faceY2[f])) / denom;
                double w1 = ((faceY2[f] - faceY0[f]) * (px - faceX2[f]) + (faceX0[f] - faceX2[f]) * (py - faceY2[f])) / denom;
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
