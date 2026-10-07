using MoleHill.Core.Engine;
using MoleHill.Shared;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

/// <summary>
/// The Grasshopper components extract terrain meshes through <see cref="RhinoGeometryConversions"/>, the
/// extraction the Rhino panel uses, so a quad mesh is accepted and a mesh with duplicate vertices is welded
/// instead of being rejected or graded as it came.
/// </summary>
public sealed class GhMeshExtractionTests
{
    [RhinoNativeFact]
    public void TryExtractMesh_QuadMesh_ConvertsToTriangles()
    {
        using var mesh = new Mesh();
        mesh.Vertices.Add(0.0, 0.0, 0.0);
        mesh.Vertices.Add(10.0, 0.0, 1.0);
        mesh.Vertices.Add(10.0, 10.0, 2.0);
        mesh.Vertices.Add(0.0, 10.0, 1.0);
        mesh.Faces.AddFace(0, 1, 2, 3);

        bool ok = RhinoGeometryConversions.TryExtractMesh(mesh, out IndexedTriMesh extracted, out string? error);

        Assert.True(ok, error);
        Assert.Equal(4, extracted.VertexCount);
        Assert.Equal(2, extracted.FaceCount);
        Assert.Single(mesh.Faces);
    }

    [RhinoNativeFact]
    public void TryExtractMesh_DuplicateVertices_AreCombinedAndCountsMatchTheArrays()
    {
        using var mesh = new Mesh();
        mesh.Vertices.Add(0.0, 0.0, 0.0);
        mesh.Vertices.Add(10.0, 0.0, 0.0);
        mesh.Vertices.Add(0.0, 10.0, 0.0);
        mesh.Vertices.Add(10.0, 0.0, 0.0);
        mesh.Vertices.Add(10.0, 10.0, 0.0);
        mesh.Vertices.Add(0.0, 10.0, 0.0);
        mesh.Faces.AddFace(0, 1, 2);
        mesh.Faces.AddFace(3, 4, 5);

        bool ok = RhinoGeometryConversions.TryExtractMesh(mesh, out IndexedTriMesh extracted, out string? error);

        Assert.True(ok, error);
        Assert.Equal(4, extracted.VertexCount);
        Assert.Equal(2, extracted.FaceCount);
        Assert.Equal(extracted.VertexCount * 3, extracted.Vertices.Length);
        Assert.Equal(extracted.FaceCount * 3, extracted.Faces.Length);
    }
}
