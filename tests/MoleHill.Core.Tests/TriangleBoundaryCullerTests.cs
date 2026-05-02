using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class TriangleBoundaryCullerTests
{
    [Fact]
    public void Cull_DegenerateBoundaryTriangle_RemovesSliverAndCompactsVertices()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            0.0, 0.02, 0.0,
            100.0, 0.0001, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            1, 3, 2
        };

        var result = TriangleBoundaryCuller.Cull(
            vertices,
            4,
            faces,
            2,
            Array.Empty<double>(),
            Array.Empty<int>(),
            0.0);

        Assert.True(result.Changed);
        Assert.Equal(1, result.FaceCount);
        Assert.Equal(3, result.VertexCount);
        Assert.Equal(new[] { 0, 1, 2 }, result.Faces);
    }

    [Fact]
    public void Cull_BoundaryTriangleCrossingConstraint_RemovesCrossingFace()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            4.0, 0.0, 0.0,
            0.0, 4.0, 0.0,
            4.0, 4.0, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            1, 3, 2
        };
        double[] constraintXy =
        {
            2.0, -1.0,
            2.0, 1.0
        };
        int[] constraintSegments = { 0, 1 };

        var result = TriangleBoundaryCuller.Cull(
            vertices,
            4,
            faces,
            2,
            constraintXy,
            constraintSegments,
            0.0);

        Assert.True(result.Changed);
        Assert.Equal(1, result.FaceCount);
        Assert.Equal(3, result.VertexCount);
    }

    [Fact]
    public void Cull_NonDegenerateCoarseBoundaryTriangle_KeepsFaces()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            0.0, 10.0, 0.0,
            10.0, 10.0, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            1, 3, 2
        };

        var result = TriangleBoundaryCuller.Cull(
            vertices,
            4,
            faces,
            2,
            Array.Empty<double>(),
            Array.Empty<int>(),
            5.0);

        Assert.False(result.Changed);
        Assert.Equal(2, result.FaceCount);
    }

    [Fact]
    public void Cull_SteepBoundaryTriangle_RemovesOnlyAfterSlopeCriterion()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            100.0, 0.0001, 100.0
        };
        int[] faces =
        {
            0, 1, 2,
            1, 3, 2
        };

        var result = TriangleBoundaryCuller.Cull(
            vertices,
            4,
            faces,
            2,
            Array.Empty<double>(),
            Array.Empty<int>(),
            new BoundaryTrianglePeelSettings
            {
                MaxBoundaryEdgeLength = 1_000.0,
                MaxInteriorAngleDegrees = 180.0,
                MaxSlopeAngleDegrees = 45.0
            });

        Assert.True(result.Changed);
        Assert.Equal(1, result.FaceCount);
        Assert.Equal(3, result.VertexCount);
    }

    [Fact]
    public void Cull_ClosedMeshWithNoNakedEdges_DoesNotRemoveSteepFaces()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            0.0, 1.0, 100.0,
            0.2, 0.2, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 3, 1,
            1, 3, 2,
            2, 3, 0
        };

        var result = TriangleBoundaryCuller.Cull(
            vertices,
            4,
            faces,
            4,
            Array.Empty<double>(),
            Array.Empty<int>(),
            new BoundaryTrianglePeelSettings
            {
                MaxBoundaryEdgeLength = 0.0,
                MaxInteriorAngleDegrees = 170.0,
                MaxSlopeAngleDegrees = 1.0
            });

        Assert.False(result.Changed);
        Assert.Equal(4, result.FaceCount);
    }
}
