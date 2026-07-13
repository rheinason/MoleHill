using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class RhinoGeometryConversionsTests
{
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
