using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using TopoTIN.Core.Engine;

namespace TopoTIN.Core.Grading;

/// <summary>
/// Splits a terrain mesh into separate meshes by closed boundary curves.
/// Re-triangulates with boundaries as constrained edges, then classifies
/// each face by which area its centroid falls inside.
/// </summary>
public static class MeshAreaSplitter
{
    /// <summary>
    /// Closed polygon boundary defining an area.
    /// </summary>
    public sealed class AreaBoundary
    {
        /// <summary>Flat XY polygon vertices: [x0,y0, x1,y1, …]</summary>
        public double[] XyVertices { get; }

        /// <summary>Number of polygon vertices.</summary>
        public int VertexCount { get; }

        public AreaBoundary(double[] xyVertices, int vertexCount)
        {
            XyVertices = xyVertices;
            VertexCount = vertexCount;
        }
    }

    /// <summary>
    /// Result of splitting a mesh by area boundaries.
    /// </summary>
    public sealed class SplitResult
    {
        /// <summary>Full mesh vertices (flat XYZ).</summary>
        public double[] Vertices { get; }

        /// <summary>Number of vertices.</summary>
        public int VertexCount { get; }

        /// <summary>Full mesh faces (triangle indices).</summary>
        public int[] Faces { get; }

        /// <summary>Number of faces.</summary>
        public int FaceCount { get; }

        /// <summary>Per-face area index: -1 = remainder, 0+ = area index.</summary>
        public int[] FaceAreaIndex { get; }

        /// <summary>Number of area boundaries.</summary>
        public int AreaCount { get; }

        public SplitResult(double[] vertices, int vertexCount,
                           int[] faces, int faceCount,
                           int[] faceAreaIndex, int areaCount)
        {
            Vertices = vertices;
            VertexCount = vertexCount;
            Faces = faces;
            FaceCount = faceCount;
            FaceAreaIndex = faceAreaIndex;
            AreaCount = areaCount;
        }
    }

    /// <summary>
    /// Split a mesh into areas defined by closed boundary curves.
    /// </summary>
    public static SplitResult? Split(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        AreaBoundary[] areas,
        double maxArea, double minAngle,
        out string? errorMessage)
    {
        errorMessage = null;
        const double dedupTol = 1e-6;

        if (areas.Length == 0)
        {
            errorMessage = "No area boundaries provided.";
            return null;
        }

        // ── Step 1: Build combined vertex + segment set ──
        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();

        for (int i = 0; i < vertexCount; i++)
        {
            xyList.Add(vertices[i * 3]);
            xyList.Add(vertices[i * 3 + 1]);
            zList.Add(vertices[i * 3 + 2]);
        }

        // Add area boundary vertices + constrained segments
        foreach (var area in areas)
        {
            var areaIndices = new int[area.VertexCount];
            for (int i = 0; i < area.VertexCount; i++)
            {
                double px = area.XyVertices[i * 2];
                double py = area.XyVertices[i * 2 + 1];

                int near = PadGrader.FindNearVertex(xyList, px, py, dedupTol);
                if (near >= 0)
                {
                    areaIndices[i] = near;
                }
                else
                {
                    areaIndices[i] = zList.Count;
                    xyList.Add(px);
                    xyList.Add(py);
                    zList.Add(PadGrader.InterpolateZ(vertices, faces, faceCount, px, py));
                }
            }

            // Closed polygon segments
            for (int i = 0; i < area.VertexCount; i++)
            {
                int a = areaIndices[i];
                int b = areaIndices[(i + 1) % area.VertexCount];
                if (a != b) segList.Add((a, b));
            }
        }

        // ── Step 2: Triangulate with fallback ──
        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            errorMessage = "Too few vertices for triangulation.";
            return null;
        }

        var triMesh = TriangulationHelper.Triangulate(
            xyList, totalVerts, segList,
            maxArea, minAngle,
            out string? triWarning);

        if (triMesh == null)
        {
            errorMessage = triWarning ?? "Triangulation failed.";
            return null;
        }

        if (triWarning != null)
            errorMessage = triWarning;

        // ── Step 3: Build output ──
        var outVerts = triMesh.Vertices.ToList();
        var outTris = triMesh.Triangles.ToList();
        int outVertCount = outVerts.Count;
        int outFaceCount = outTris.Count;

        var finalVerts = new double[outVertCount * 3];
        var idToIdx = new Dictionary<int, int>(outVertCount);

        for (int i = 0; i < outVertCount; i++)
        {
            var mv = outVerts[i];
            idToIdx[mv.ID] = i;
            finalVerts[i * 3] = mv.X;
            finalVerts[i * 3 + 1] = mv.Y;

            if (mv.ID >= 0 && mv.ID < totalVerts)
            {
                finalVerts[i * 3 + 2] = zList[mv.ID];
            }
            else
            {
                finalVerts[i * 3 + 2] = PadGrader.InterpolateZ(vertices, faces, faceCount, mv.X, mv.Y);
            }
        }

        var finalFaces = new int[outFaceCount * 3];
        var faceAreaIndex = new int[outFaceCount];

        for (int f = 0; f < outFaceCount; f++)
        {
            var tri = outTris[f];
            int i0 = idToIdx.GetValueOrDefault(tri.GetVertex(0).ID, 0);
            int i1 = idToIdx.GetValueOrDefault(tri.GetVertex(1).ID, 0);
            int i2 = idToIdx.GetValueOrDefault(tri.GetVertex(2).ID, 0);

            finalFaces[f * 3] = i0;
            finalFaces[f * 3 + 1] = i1;
            finalFaces[f * 3 + 2] = i2;

            // Classify by centroid
            double cx = (finalVerts[i0 * 3] + finalVerts[i1 * 3] + finalVerts[i2 * 3]) / 3.0;
            double cy = (finalVerts[i0 * 3 + 1] + finalVerts[i1 * 3 + 1] + finalVerts[i2 * 3 + 1]) / 3.0;

            faceAreaIndex[f] = -1;

            for (int a = 0; a < areas.Length; a++)
            {
                if (PadGrader.PointInPolygon(cx, cy, areas[a].XyVertices, areas[a].VertexCount))
                {
                    faceAreaIndex[f] = a;
                }
            }
        }

        return new SplitResult(
            finalVerts, outVertCount,
            finalFaces, outFaceCount,
            faceAreaIndex, areas.Length);
    }
}
