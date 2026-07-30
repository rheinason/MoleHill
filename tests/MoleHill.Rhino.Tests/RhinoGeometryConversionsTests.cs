using MoleHill.Core.Engine;
using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class RhinoGeometryConversionsTests
{
    [RhinoNativeFact]
    public void ToRhinoMesh_ValidatedTin_PreservesTopologyAndCachesExtractedData()
    {
        var result = new TinResult(
            new[]
            {
                0.0, 0.0, 0.0,
                1.0, 0.0, 0.0,
                1.0, 1.0, 1.0,
                0.0, 1.0, 1.0
            },
            vertexCount: 4,
            new[] { 0, 1, 2, 0, 2, 3 },
            faceCount: 2,
            Array.Empty<int>(),
            edgeCount: 0,
            Array.Empty<int>(),
            nakedEdgeCount: 0,
            new[] { 0, 1, 2, 3 });

        using Mesh mesh = RhinoGeometryConversions.ToRhinoMesh(result);
        ExtractedMeshData data = RhinoGeometryConversions.GetNormalizedMeshData(mesh);

        Assert.True(RhinoGeometryConversions.IsNormalizedMesh(mesh));
        Assert.Equal(4, mesh.Vertices.Count);
        Assert.Equal(2, mesh.Faces.Count);
        Assert.Equal(4, data.VertexCount);
        Assert.Equal(2, data.FaceCount);
        Assert.Equal(result.Faces, data.Faces);
    }

    [RhinoNativeFact]
    public void BuildMesh_NormalizedMeshIsMarkedButDuplicateIsNot()
    {
        using Mesh mesh = RhinoGeometryConversions.BuildMesh(
            new[]
            {
                0.0, 0.0, 0.0,
                1.0, 0.0, 0.0,
                0.0, 1.0, 0.0
            },
            3,
            new[] { 0, 1, 2 },
            1);
        using Mesh duplicate = mesh.DuplicateMesh();

        Assert.True(RhinoGeometryConversions.IsNormalizedMesh(mesh));
        Assert.False(RhinoGeometryConversions.IsNormalizedMesh(duplicate));

        RhinoGeometryConversions.NormalizeMeshInPlace(duplicate);

        Assert.True(RhinoGeometryConversions.IsNormalizedMesh(duplicate));
    }
}
