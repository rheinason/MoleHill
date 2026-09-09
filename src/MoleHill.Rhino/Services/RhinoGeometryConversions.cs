using System.Runtime.CompilerServices;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class ExtractedMeshData
{
    public required double[] Vertices { get; init; }

    public required int VertexCount { get; init; }

    public required int[] Faces { get; init; }

    public required int FaceCount { get; init; }
}

internal static class RhinoGeometryConversions
{
    private static readonly ConditionalWeakTable<Mesh, ExtractedMeshData> MeshDataCache = new();
    private static readonly ConditionalWeakTable<Mesh, NormalizedMeshMarker> NormalizedMeshes = new();

    public static Mesh ToRhinoMesh(TinResult result)
    {
        var mesh = new Mesh();
        mesh.Vertices.Capacity = result.VertexCount;
        mesh.Faces.Capacity = result.FaceCount;

        for (int i = 0; i < result.VertexCount; i++)
        {
            mesh.Vertices.Add(
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
        }

        for (int i = 0; i < result.FaceCount; i++)
        {
            mesh.Faces.AddFace(
                result.Faces[i * 3],
                result.Faces[i * 3 + 1],
                result.Faces[i * 3 + 2]);
        }

        FinalizeKnownTriangleMesh(mesh);
        return mesh;
    }

    public static bool TryGetMeshData(Mesh mesh, out ExtractedMeshData data, out string? errorMessage)
    {
        errorMessage = null;
        if (MeshDataCache.TryGetValue(mesh, out data!))
            return true;

        var normalized = mesh.DuplicateMesh();
        NormalizeMeshInPlace(normalized);

        if (normalized.Faces.Count == 0)
        {
            data = null!;
            errorMessage = "Mesh has no faces.";
            return false;
        }

        if (!TryBuildMeshData(normalized, out data, out errorMessage))
        {
            data = null!;
            return false;
        }

        CacheMeshData(mesh, data);
        return true;
    }

    /// <summary>
    /// Extracts flat arrays <b>together with the counts that describe them</b>.
    ///
    /// Prefer this over the array-only overload whenever a count is needed. Extraction normalizes a
    /// COPY of the mesh - <see cref="NormalizeMeshInPlace"/> converts quads to triangles, combines
    /// identical vertices, culls unused vertices and culls degenerate faces - so the returned arrays
    /// routinely describe a different number of vertices and faces than the Rhino mesh that was passed
    /// in. Pairing these arrays with <c>mesh.Vertices.Count</c>/<c>mesh.Faces.Count</c> reads past the
    /// end of them; on a large terrain with degenerate slivers that is an IndexOutOfRangeException,
    /// not a rounding error.
    /// </summary>
    public static bool TryExtractMeshData(
        Mesh mesh,
        out double[] vertices,
        out int vertexCount,
        out int[] faces,
        out int faceCount,
        out string? errorMessage)
    {
        vertices = Array.Empty<double>();
        faces = Array.Empty<int>();
        vertexCount = 0;
        faceCount = 0;
        if (!TryGetMeshData(mesh, out var data, out errorMessage))
            return false;

        vertices = data.Vertices;
        faces = data.Faces;
        vertexCount = data.VertexCount;
        faceCount = data.FaceCount;
        return true;
    }

    public static bool TryExtractMeshData(Mesh mesh, out double[] vertices, out int[] faces, out string? errorMessage)
    {
        vertices = Array.Empty<double>();
        faces = Array.Empty<int>();
        if (!TryGetMeshData(mesh, out var data, out errorMessage))
            return false;

        vertices = data.Vertices;
        faces = data.Faces;
        return true;
    }

    /// <summary>
    /// Extracts flat arrays from a mesh already normalized by the terrain stage-cache pipeline. Unlike
    /// <see cref="TryGetMeshData"/>, this avoids duplicating and normalizing a potentially large mesh on
    /// the Rhino UI thread. Callers must only pass cached terrain-stage meshes.
    /// </summary>
    public static ExtractedMeshData GetNormalizedMeshData(Mesh mesh)
    {
        if (MeshDataCache.TryGetValue(mesh, out ExtractedMeshData? data))
            return data;

        data = BuildMeshData(mesh);
        CacheMeshData(mesh, data);
        return data;
    }

    public static Mesh BuildMesh(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        var mesh = new Mesh();
        mesh.Vertices.Capacity = vertexCount;
        mesh.Faces.Capacity = faceCount;

        for (int i = 0; i < vertexCount; i++)
        {
            mesh.Vertices.Add(
                vertices[i * 3],
                vertices[i * 3 + 1],
                vertices[i * 3 + 2]);
        }

        for (int i = 0; i < faceCount; i++)
        {
            mesh.Faces.AddFace(
                faces[i * 3],
                faces[i * 3 + 1],
                faces[i * 3 + 2]);
        }

        NormalizeMeshInPlace(mesh);
        return mesh;
    }

    /// <summary>
    /// Builds a quad-dominant mesh from flat arrays, <b>preserving quads</b> (unlike
    /// <see cref="BuildMesh"/>, which triangulates via <see cref="NormalizeMeshInPlace"/>). Used by the
    /// Retopo quad output. <paramref name="quads"/> is 4 indices/face, <paramref name="tris"/> 3/face.
    /// </summary>
    public static Mesh BuildQuadDominantMesh(double[] vertices, int[] quads, int[] tris)
    {
        var mesh = new Mesh();
        int vertexCount = vertices.Length / 3;
        mesh.Vertices.Capacity = vertexCount;
        mesh.Faces.Capacity = (quads.Length / 4) + (tris.Length / 3);

        for (int i = 0; i < vertexCount; i++)
            mesh.Vertices.Add(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);

        for (int i = 0; i < quads.Length / 4; i++)
            mesh.Faces.AddFace(quads[i * 4], quads[i * 4 + 1], quads[i * 4 + 2], quads[i * 4 + 3]);

        for (int i = 0; i < tris.Length / 3; i++)
            mesh.Faces.AddFace(tris[i * 3], tris[i * 3 + 1], tris[i * 3 + 2]);

        mesh.Normals.ComputeNormals();
        mesh.UnifyNormals();
        mesh.Compact();
        CacheMeshData(mesh, BuildMeshData(mesh));
        // Retopo deliberately produces quad faces. Mark this mesh as finalized so the generic stage
        // cache normalization does not flatten those faces back into triangles.
        MarkNormalized(mesh);
        return mesh;
    }

    public static Mesh BuildSubMesh(MeshAreaSplitter.SplitResult result, int areaIndex)
    {
        var faceIndices = new List<int>();
        for (int f = 0; f < result.FaceCount; f++)
        {
            if (result.FaceAreaIndex[f] == areaIndex)
                faceIndices.Add(f);
        }

        var usedVertices = new HashSet<int>();
        foreach (int faceIndex in faceIndices)
        {
            usedVertices.Add(result.Faces[faceIndex * 3]);
            usedVertices.Add(result.Faces[faceIndex * 3 + 1]);
            usedVertices.Add(result.Faces[faceIndex * 3 + 2]);
        }

        var remap = new Dictionary<int, int>();
        var mesh = new Mesh();
        foreach (int vertexIndex in usedVertices)
        {
            remap[vertexIndex] = mesh.Vertices.Count;
            mesh.Vertices.Add(
                result.Vertices[vertexIndex * 3],
                result.Vertices[vertexIndex * 3 + 1],
                result.Vertices[vertexIndex * 3 + 2]);
        }

        foreach (int faceIndex in faceIndices)
        {
            mesh.Faces.AddFace(
                remap[result.Faces[faceIndex * 3]],
                remap[result.Faces[faceIndex * 3 + 1]],
                remap[result.Faces[faceIndex * 3 + 2]]);
        }

        NormalizeMeshInPlace(mesh);
        return mesh;
    }

    internal static void NormalizeMeshInPlace(Mesh mesh)
    {
        mesh.Faces.ConvertQuadsToTriangles();
        mesh.Vertices.CombineIdentical(true, true);
        mesh.Vertices.CullUnused();
        mesh.Faces.CullDegenerateFaces();
        mesh.Normals.ComputeNormals();
        mesh.UnifyNormals();
        mesh.Compact();
        CacheMeshData(mesh, BuildMeshData(mesh));
        MarkNormalized(mesh);
    }

    /// <summary>
    /// Finalizes a triangle-only mesh whose vertices and faces have already been validated by Core.
    /// Avoids repeating duplicate, unused-vertex, and degenerate-face scans over very large TINs.
    /// </summary>
    private static void FinalizeKnownTriangleMesh(Mesh mesh)
    {
        mesh.Normals.ComputeNormals();
        mesh.UnifyNormals();
        mesh.Compact();
        CacheMeshData(mesh, BuildMeshData(mesh));
        MarkNormalized(mesh);
    }

    internal static bool IsNormalizedMesh(Mesh mesh) => NormalizedMeshes.TryGetValue(mesh, out _);

    private static bool TryBuildMeshData(Mesh mesh, out ExtractedMeshData data, out string? errorMessage)
    {
        errorMessage = null;
        data = BuildMeshData(mesh);
        if (data.FaceCount == 0)
        {
            errorMessage = "Mesh has no faces.";
            return false;
        }

        return true;
    }

    private static ExtractedMeshData BuildMeshData(Mesh mesh)
    {
        var vertices = new double[mesh.Vertices.Count * 3];
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var pt = mesh.Vertices[i];
            vertices[i * 3] = pt.X;
            vertices[i * 3 + 1] = pt.Y;
            vertices[i * 3 + 2] = pt.Z;
        }

        // Quad-aware flattening: a quad face becomes two triangles (A,B,C)+(A,C,D). Triangle meshes are
        // unaffected; this keeps quad output (from Retopo) geometrically correct for any triangle consumer.
        int triangleCount = 0;
        for (int i = 0; i < mesh.Faces.Count; i++)
            triangleCount += mesh.Faces[i].IsQuad ? 2 : 1;

        var faces = new int[triangleCount * 3];
        int t = 0;
        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            var face = mesh.Faces[i];
            faces[t * 3] = face.A;
            faces[t * 3 + 1] = face.B;
            faces[t * 3 + 2] = face.C;
            t++;
            if (face.IsQuad)
            {
                faces[t * 3] = face.A;
                faces[t * 3 + 1] = face.C;
                faces[t * 3 + 2] = face.D;
                t++;
            }
        }

        return new ExtractedMeshData
        {
            Vertices = vertices,
            VertexCount = mesh.Vertices.Count,
            Faces = faces,
            FaceCount = triangleCount
        };
    }

    private static void CacheMeshData(Mesh mesh, ExtractedMeshData data)
    {
        MeshDataCache.Remove(mesh);
        MeshDataCache.Add(mesh, data);
    }

    private static void MarkNormalized(Mesh mesh)
    {
        NormalizedMeshes.Remove(mesh);
        NormalizedMeshes.Add(mesh, new NormalizedMeshMarker());
    }

    private sealed class NormalizedMeshMarker
    {
    }
}
