using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class IndexedTriMeshTests
{
    [Fact]
    public void Constructor_CountsTheArraysCanHold_Succeeds()
    {
        // Over-allocated buffers are allowed: the counts are what is read.
        var mesh = new IndexedTriMesh(new double[12], 3, new int[6], 1);

        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(1, mesh.FaceCount);
        Assert.False(mesh.IsEmpty);
    }

    [Fact]
    public void Constructor_VertexCountFromAnotherMesh_ThrowsWithDiagnosis()
    {
        // The historical failure: arrays from a normalized copy, count from the original Rhino mesh.
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new IndexedTriMesh(new double[9], 4, new int[3], 1));

        Assert.Contains("same extraction", error.Message);
    }

    [Fact]
    public void Constructor_FaceCountBeyondArray_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexedTriMesh(new double[9], 3, new int[3], 2));
    }

    [Fact]
    public void FromArrays_TakesCountsFromLengths()
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) =
            IndexedTriMesh.FromArrays(new double[9], new int[3]);

        Assert.Equal(9, vertices.Length);
        Assert.Equal(3, vertexCount);
        Assert.Equal(3, faces.Length);
        Assert.Equal(1, faceCount);
    }

    [Fact]
    public void Empty_IsEmpty() => Assert.True(IndexedTriMesh.Empty.IsEmpty);
}
