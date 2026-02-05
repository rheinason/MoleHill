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

    /// <summary>
    /// Build a TIN from preprocessed input. Uses caching to avoid unnecessary rebuilds.
    /// </summary>
    /// <param name="xyCoords">Flat [x0,y0, x1,y1, …] of all merged vertices.</param>
    /// <param name="zValues">Z value per vertex, same order as xyCoords pairs.</param>
    /// <param name="segments">Flat [a0,b0, a1,b1, …] index pairs for constrained edges.</param>
    /// <param name="quality">Refinement settings.</param>
    /// <returns>The triangulation result, or null if input is invalid.</returns>
    public TinResult? Build(double[] xyCoords, double[] zValues,
                            int[] segments, QualitySettings quality)
    {
        int vertexCount = xyCoords.Length / 2;
        if (vertexCount < 3)
            return null;

        var snapshot = InputSnapshot.Create(xyCoords, zValues, segments, quality);

        // Tier 1: Check XY hash
        if (_cachedSnapshot != null && _cachedResult != null &&
            snapshot.XyHash == _cachedSnapshot.XyHash)
        {
            if (snapshot.ZHash == _cachedSnapshot.ZHash)
            {
                // Exact match — return cached result as-is
                return _cachedResult;
            }

            // Z-only change — update Z values without retriangulation
            _cachedSnapshot = snapshot;
            _cachedResult = _cachedResult.WithUpdatedZ(zValues);
            return _cachedResult;
        }

        // Tier 2: Full rebuild
        var result = FullRebuild(xyCoords, zValues, segments, quality);
        if (result != null)
        {
            _cachedSnapshot = snapshot;
            _cachedResult = result;
        }
        return result;
    }

    /// <summary>
    /// Invalidate the cache, forcing a full rebuild on next Build call.
    /// </summary>
    public void InvalidateCache()
    {
        _cachedSnapshot = null;
        _cachedResult = null;
    }

    /// <summary>
    /// Perform a full Delaunay (or constrained Delaunay) triangulation.
    /// </summary>
    private static TinResult? FullRebuild(double[] xyCoords, double[] zValues,
                                           int[] segments, QualitySettings quality)
    {
        int vertexCount = xyCoords.Length / 2;
        if (vertexCount < 3)
            return null;

        // Build Triangle.NET polygon
        var polygon = new Polygon(vertexCount);
        var vertices = new Vertex[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            var v = new Vertex(xyCoords[i * 2], xyCoords[i * 2 + 1]);
            v.ID = i;
            vertices[i] = v;
            polygon.Add(v);
        }

        // Add constrained segments
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

        // Triangulate
        var constraintOpts = new ConstraintOptions
        {
            ConformingDelaunay = segCount > 0
        };

        TriangleNet.Meshing.QualityOptions? qualityOpts = null;
        if (quality.HasConstraints)
        {
            qualityOpts = new TriangleNet.Meshing.QualityOptions();
            if (quality.MaxArea > 0) qualityOpts.MaximumArea = quality.MaxArea;
            if (quality.MinAngle > 0) qualityOpts.MinimumAngle = quality.MinAngle;
        }

        var mesher = new GenericMesher();
        IMesh mesh;
        try
        {
            mesh = mesher.Triangulate(polygon, constraintOpts, qualityOpts);
        }
        catch
        {
            return null;
        }

        // Build ID → index map (Triangle.NET may renumber vertices or add Steiner points)
        var meshVertices = mesh.Vertices;
        int outVertexCount = meshVertices.Count;
        var outVerts = new double[outVertexCount * 3];
        var idToIndex = new Dictionary<int, int>(outVertexCount);

        int idx = 0;
        foreach (var v in meshVertices)
        {
            idToIndex[v.ID] = idx;
            outVerts[idx * 3] = v.X;
            outVerts[idx * 3 + 1] = v.Y;
            // Z: use original Z if vertex ID maps to input, else interpolate later (0 for Steiner points)
            outVerts[idx * 3 + 2] = v.ID < zValues.Length ? zValues[v.ID] : 0.0;
            idx++;
        }

        // Triangles
        var triangles = mesh.Triangles;
        int faceCount = triangles.Count;
        var outFaces = new int[faceCount * 3];
        int fi = 0;
        foreach (var tri in triangles)
        {
            int v0id = tri.GetVertexID(0);
            int v1id = tri.GetVertexID(1);
            int v2id = tri.GetVertexID(2);

            outFaces[fi++] = idToIndex.GetValueOrDefault(v0id, 0);
            outFaces[fi++] = idToIndex.GetValueOrDefault(v1id, 0);
            outFaces[fi++] = idToIndex.GetValueOrDefault(v2id, 0);
        }

        // Edges
        var edges = mesh.Edges;
        var edgeList = new List<int>();
        var nakedEdgeList = new List<int>();

        // Track edge usage to find boundary edges
        var edgeTriCount = new Dictionary<(int, int), int>();
        foreach (var tri in triangles)
        {
            for (int e = 0; e < 3; e++)
            {
                int va = tri.GetVertexID(e);
                int vb = tri.GetVertexID((e + 1) % 3);
                var key = va < vb ? (va, vb) : (vb, va);
                edgeTriCount[key] = edgeTriCount.GetValueOrDefault(key, 0) + 1;
            }
        }

        foreach (var edge in edges)
        {
            int a = idToIndex.GetValueOrDefault(edge.P0, 0);
            int b = idToIndex.GetValueOrDefault(edge.P1, 0);
            edgeList.Add(a);
            edgeList.Add(b);

            var key = edge.P0 < edge.P1 ? (edge.P0, edge.P1) : (edge.P1, edge.P0);
            if (edgeTriCount.GetValueOrDefault(key, 0) < 2)
            {
                nakedEdgeList.Add(a);
                nakedEdgeList.Add(b);
            }
        }

        return new TinResult(
            outVerts, outVertexCount,
            outFaces, faceCount,
            edgeList.ToArray(), edgeList.Count / 2,
            nakedEdgeList.ToArray(), nakedEdgeList.Count / 2);
    }
}
