using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class RhinoGeometryConversions
{
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

        NormalizeMeshInPlace(mesh);
        return mesh;
    }

    public static bool TryExtractMeshData(Mesh mesh, out double[] vertices, out int[] faces, out string? errorMessage)
    {
        errorMessage = null;
        vertices = Array.Empty<double>();
        faces = Array.Empty<int>();

        var normalized = mesh.DuplicateMesh();
        NormalizeMeshInPlace(normalized);

        if (normalized.Faces.Count == 0)
        {
            errorMessage = "Mesh has no faces.";
            return false;
        }

        vertices = new double[normalized.Vertices.Count * 3];
        for (int i = 0; i < normalized.Vertices.Count; i++)
        {
            var pt = normalized.Vertices[i];
            vertices[i * 3] = pt.X;
            vertices[i * 3 + 1] = pt.Y;
            vertices[i * 3 + 2] = pt.Z;
        }

        faces = new int[normalized.Faces.Count * 3];
        for (int i = 0; i < normalized.Faces.Count; i++)
        {
            var face = normalized.Faces[i];
            if (face.IsQuad)
            {
                errorMessage = "Only triangle meshes are supported.";
                return false;
            }

            faces[i * 3] = face.A;
            faces[i * 3 + 1] = face.B;
            faces[i * 3 + 2] = face.C;
        }

        return true;
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
    }

}
