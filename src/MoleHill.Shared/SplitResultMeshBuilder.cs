using MoleHill.Core.Grading;
using Rhino.Geometry;

namespace MoleHill.Shared;

/// <summary>
/// Turns a run of <see cref="MeshAreaSplitter.SplitResult"/> faces into a Rhino mesh, for both hosts.
/// </summary>
/// <remarks>
/// Group the faces once with <see cref="FaceOwnerGroups"/> and reuse a single
/// <see cref="SubMeshVertexRemap"/> across areas: rescanning every result face per area is
/// O(areas x faces), and a fresh hash set plus dictionary per area allocates two whole-vertex-set
/// structures each time.
/// </remarks>
public static class SplitResultMeshBuilder
{
    /// <summary>
    /// The faces with their vertices in first-touch order, not yet oriented or compacted, for a caller
    /// that finishes the mesh its own way (the Rhino host normalizes it).
    /// </summary>
    public static Mesh CreateUnfinished(
        MeshAreaSplitter.SplitResult result,
        ReadOnlySpan<int> faceIndices,
        SubMeshVertexRemap remap)
    {
        var mesh = new Mesh();
        mesh.Faces.Capacity = faceIndices.Length;
        remap.Begin();

        foreach (int faceIndex in faceIndices)
        {
            int a = MapVertex(result, remap, mesh, result.Faces[faceIndex * 3]);
            int b = MapVertex(result, remap, mesh, result.Faces[faceIndex * 3 + 1]);
            int c = MapVertex(result, remap, mesh, result.Faces[faceIndex * 3 + 2]);
            mesh.Faces.AddFace(a, b, c);
        }

        return mesh;
    }

    /// <summary>The faces as an oriented, compacted mesh, vertices in first-touch order.</summary>
    public static Mesh Create(
        MeshAreaSplitter.SplitResult result,
        ReadOnlySpan<int> faceIndices,
        SubMeshVertexRemap remap)
    {
        Mesh mesh = CreateUnfinished(result, faceIndices, remap);
        MeshNormalOrientation.UnifyAndComputeNormals(mesh);
        mesh.Compact();
        return mesh;
    }

    private static int MapVertex(MeshAreaSplitter.SplitResult result, SubMeshVertexRemap remap, Mesh mesh, int vertexIndex)
    {
        if (remap.TryGet(vertexIndex, out int existing))
            return existing;

        int newIndex = mesh.Vertices.Count;
        mesh.Vertices.Add(
            result.Vertices[vertexIndex * 3],
            result.Vertices[vertexIndex * 3 + 1],
            result.Vertices[vertexIndex * 3 + 2]);
        remap.Set(vertexIndex, newIndex);
        return newIndex;
    }
}
