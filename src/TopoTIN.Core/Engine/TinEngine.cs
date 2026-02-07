using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace TopoTIN.Core.Engine;

/// <summary>
/// Main TIN triangulation orchestrator with XY-hash caching.
/// Persists between GH solves as a field on the component.
/// </summary>
public class TinEngine
{
    private InputSnapshot? _cachedSnapshot;
    private TinResult? _cachedResult;

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

        var snapshot = InputSnapshot.Create(xyCoords, zValues, segments, quality);

        if (_cachedSnapshot != null && _cachedResult != null &&
            snapshot.XyHash == _cachedSnapshot.XyHash &&
            zValues.Length == _cachedResult.VertexCount)
        {
            if (snapshot.ZHash == _cachedSnapshot.ZHash)
            {
                return _cachedResult;
            }

            _cachedSnapshot = snapshot;
            _cachedResult = _cachedResult.WithUpdatedZ(zValues);
            return _cachedResult;
        }

        var result = FullRebuild(xyCoords, zValues, segments, quality, out errorMessage);
        if (result != null)
        {
            _cachedSnapshot = snapshot;
            _cachedResult = result;
        }
        return result;
    }

    public void InvalidateCache()
    {
        _cachedSnapshot = null;
        _cachedResult = null;
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
                                           out string? errorMessage)
    {
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
            return BuildResult(result, xyCoords, zValues, segments);

        // Attempt 2: Non-conforming CDT with quality
        if (segCount > 0)
        {
            var ncResult = TryTriangulate(polygon, segCount, quality, conforming: false, out _);
            if (ncResult != null)
            {
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
        var meshVertices = mesh.Vertices.ToList();
        var triangles = mesh.Triangles.ToList();
        int outVertexCount = meshVertices.Count;
        var outVerts = new double[outVertexCount * 3];

        var idToOutIndex = new Dictionary<int, int>(outVertexCount);
        var steinerIndices = new List<int>();

        for (int i = 0; i < outVertexCount; i++)
        {
            var v = meshVertices[i];
            idToOutIndex[v.ID] = i;

            outVerts[i * 3] = v.X;
            outVerts[i * 3 + 1] = v.Y;

            if (v.ID >= 0 && v.ID < zValues.Length)
            {
                outVerts[i * 3 + 2] = zValues[v.ID];
            }
            else
            {
                outVerts[i * 3 + 2] = double.NaN;
                steinerIndices.Add(i);
            }
        }

        int faceCount = triangles.Count;
        var outFaces = new int[faceCount * 3];

        for (int i = 0; i < faceCount; i++)
        {
            var tri = triangles[i];
            outFaces[i * 3] = idToOutIndex.GetValueOrDefault(tri.GetVertex(0).ID, 0);
            outFaces[i * 3 + 1] = idToOutIndex.GetValueOrDefault(tri.GetVertex(1).ID, 0);
            outFaces[i * 3 + 2] = idToOutIndex.GetValueOrDefault(tri.GetVertex(2).ID, 0);
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

        // ── Priority 1: Check INPUT breakline segments ──
        // These always have both endpoints with known Z, so Steiner points
        // added ON a breakline get correct Z interpolation from the original curve.
        foreach (int si in steinerIndices)
        {
            double px = verts[si * 3];
            double py = verts[si * 3 + 1];

            for (int s = 0; s < inputSegCount; s++)
            {
                int a = inputSegments[s * 2];
                int b = inputSegments[s * 2 + 1];
                if (a < 0 || a >= inputVertCount || b < 0 || b >= inputVertCount) continue;

                double x0 = inputXy[a * 2], y0 = inputXy[a * 2 + 1];
                double x1 = inputXy[b * 2], y1 = inputXy[b * 2 + 1];

                double segLen = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                if (segLen < eps) continue;

                double t = ((px - x0) * (x1 - x0) + (py - y0) * (y1 - y0)) / (segLen * segLen);
                if (t < -0.001 || t > 1.001) continue;

                double projX = x0 + t * (x1 - x0);
                double projY = y0 + t * (y1 - y0);
                double dist = Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));

                // More generous tolerance for breakline matching
                if (dist < segLen * 1e-6 + 1e-8)
                {
                    t = Math.Max(0, Math.Min(1, t));
                    verts[si * 3 + 2] = inputZ[a] + t * (inputZ[b] - inputZ[a]);
                    break;
                }
            }
        }

        // ── Priority 2: Check output edges (for non-breakline Steiner points) ──
        foreach (int si in steinerIndices)
        {
            if (!double.IsNaN(verts[si * 3 + 2])) continue;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];

            for (int e = 0; e < edgeCount; e++)
            {
                int i0 = edges[e * 2];
                int i1 = edges[e * 2 + 1];

                double z0 = verts[i0 * 3 + 2], z1 = verts[i1 * 3 + 2];
                if (double.IsNaN(z0) || double.IsNaN(z1)) continue;

                double x0 = verts[i0 * 3], y0 = verts[i0 * 3 + 1];
                double x1 = verts[i1 * 3], y1 = verts[i1 * 3 + 1];

                double edgeLen = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                if (edgeLen < eps) continue;

                double t = ((px - x0) * (x1 - x0) + (py - y0) * (y1 - y0)) / (edgeLen * edgeLen);
                if (t < -eps || t > 1.0 + eps) continue;

                double projX = x0 + t * (x1 - x0);
                double projY = y0 + t * (y1 - y0);
                double dist = Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));

                if (dist < eps * edgeLen + eps)
                {
                    t = Math.Max(0, Math.Min(1, t));
                    verts[si * 3 + 2] = z0 + t * (z1 - z0);
                    break;
                }
            }
        }

        // ── Priority 3: Barycentric face interpolation ──
        foreach (int si in steinerIndices)
        {
            if (!double.IsNaN(verts[si * 3 + 2])) continue;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];

            for (int f = 0; f < faceCount; f++)
            {
                int i0 = faces[f * 3], i1 = faces[f * 3 + 1], i2 = faces[f * 3 + 2];
                double z0 = verts[i0 * 3 + 2], z1 = verts[i1 * 3 + 2], z2 = verts[i2 * 3 + 2];
                if (double.IsNaN(z0) || double.IsNaN(z1) || double.IsNaN(z2)) continue;

                double x0 = verts[i0 * 3], y0 = verts[i0 * 3 + 1];
                double x1 = verts[i1 * 3], y1 = verts[i1 * 3 + 1];
                double x2 = verts[i2 * 3], y2 = verts[i2 * 3 + 1];

                double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
                if (Math.Abs(denom) < eps) continue;

                double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
                double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
                double w2 = 1.0 - w0 - w1;

                if (w0 >= -1e-4 && w1 >= -1e-4 && w2 >= -1e-4)
                {
                    verts[si * 3 + 2] = w0 * z0 + w1 * z1 + w2 * z2;
                    break;
                }
            }
        }

        // ── Priority 4: Iterative resolution for chain dependencies ──
        int maxIter = steinerIndices.Count + 1;
        for (int iter = 0; iter < maxIter; iter++)
        {
            int resolved = 0;
            foreach (int si in steinerIndices)
            {
                if (!double.IsNaN(verts[si * 3 + 2])) continue;

                double px = verts[si * 3];
                double py = verts[si * 3 + 1];

                for (int e = 0; e < edgeCount; e++)
                {
                    int i0 = edges[e * 2], i1 = edges[e * 2 + 1];
                    double z0 = verts[i0 * 3 + 2], z1 = verts[i1 * 3 + 2];
                    if (double.IsNaN(z0) || double.IsNaN(z1)) continue;

                    double x0 = verts[i0 * 3], y0 = verts[i0 * 3 + 1];
                    double x1 = verts[i1 * 3], y1 = verts[i1 * 3 + 1];

                    double edgeLen = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                    if (edgeLen < eps) continue;

                    double t = ((px - x0) * (x1 - x0) + (py - y0) * (y1 - y0)) / (edgeLen * edgeLen);
                    if (t < -eps || t > 1.0 + eps) continue;

                    double projX = x0 + t * (x1 - x0);
                    double projY = y0 + t * (y1 - y0);
                    double dist = Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));

                    if (dist < eps * edgeLen + eps)
                    {
                        t = Math.Max(0, Math.Min(1, t));
                        verts[si * 3 + 2] = z0 + t * (z1 - z0);
                        resolved++;
                        break;
                    }
                }

                if (double.IsNaN(verts[si * 3 + 2]))
                {
                    for (int f = 0; f < faceCount; f++)
                    {
                        int i0 = faces[f * 3], i1 = faces[f * 3 + 1], i2 = faces[f * 3 + 2];
                        double z0 = verts[i0 * 3 + 2], z1 = verts[i1 * 3 + 2], z2 = verts[i2 * 3 + 2];
                        if (double.IsNaN(z0) || double.IsNaN(z1) || double.IsNaN(z2)) continue;

                        double x0 = verts[i0 * 3], y0 = verts[i0 * 3 + 1];
                        double x1 = verts[i1 * 3], y1 = verts[i1 * 3 + 1];
                        double x2 = verts[i2 * 3], y2 = verts[i2 * 3 + 1];

                        double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
                        if (Math.Abs(denom) < eps) continue;

                        double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
                        double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
                        double w2 = 1.0 - w0 - w1;

                        if (w0 >= -1e-4 && w1 >= -1e-4 && w2 >= -1e-4)
                        {
                            verts[si * 3 + 2] = w0 * z0 + w1 * z1 + w2 * z2;
                            resolved++;
                            break;
                        }
                    }
                }
            }
            if (resolved == 0) break;
        }

        // ── Priority 5: Nearest known vertex fallback ──
        foreach (int si in steinerIndices)
        {
            if (!double.IsNaN(verts[si * 3 + 2])) continue;

            double px = verts[si * 3];
            double py = verts[si * 3 + 1];
            double nearestZ = 0;
            double nearestDistSq = double.MaxValue;

            for (int i = 0; i < verts.Length / 3; i++)
            {
                double z = verts[i * 3 + 2];
                if (double.IsNaN(z)) continue;

                double dx = verts[i * 3] - px;
                double dy = verts[i * 3 + 1] - py;
                double distSq = dx * dx + dy * dy;

                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearestZ = z;
                }
            }

            verts[si * 3 + 2] = nearestZ;
        }
    }
}
