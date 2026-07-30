using MoleHill.Core.Engine;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using Xunit;

namespace MoleHill.Core.Tests;

public class TriangleBoundaryCullerTests
{
    [Fact]
    public void CullUsingNativeAdjacency_MatchesDictionaryPathAndExactThreshold()
    {
        double[] inputXy =
        {
            0.0, 0.0,
            1.0, 0.0,
            0.0, 0.02,
            100.0, 0.0001
        };
        var polygon = new Polygon(inputXy.Length / 2);
        for (int index = 0; index < inputXy.Length / 2; index++)
        {
            polygon.Add(new Vertex(inputXy[index * 2], inputXy[index * 2 + 1])
            {
                ID = index
            });
        }

        var mesh = new GenericMesher().Triangulate(
            polygon,
            new ConstraintOptions
            {
                ConformingDelaunay = false,
                Convex = true
            });
        TriangleNetExtractor.Result extracted =
            TriangleNetExtractor.Extract(mesh, includeNativeAdjacency: true);
        Assert.NotNull(extracted.Adjacency);
        Assert.True(extracted.Adjacency!.IsValid);

        var vertices = new double[extracted.VertexCount * 3];
        for (int index = 0; index < extracted.VertexCount; index++)
        {
            vertices[index * 3] = extracted.Xy[index * 2];
            vertices[index * 3 + 1] = extracted.Xy[index * 2 + 1];
        }

        IndexedMeshTools.EdgeTopology topology =
            IndexedMeshTools.BuildEdgeTopology(extracted.Faces, extracted.FaceCount);
        double genericThreshold = TriangleBoundaryCuller.ComputeAutoThreshold(vertices, topology);
        double nativeThreshold = TriangleBoundaryCuller.ComputeAutoThreshold(
            vertices,
            extracted.Faces,
            extracted.FaceCount,
            extracted.Adjacency);
        Assert.Equal(genericThreshold, nativeThreshold);

        var settings = new BoundaryTrianglePeelSettings
        {
            Enabled = true,
            MaxBoundaryEdgeLength = nativeThreshold,
            MaxInteriorAngleDegrees = BoundaryTrianglePeelSettings.Default.MaxInteriorAngleDegrees,
            MaxSlopeAngleDegrees = BoundaryTrianglePeelSettings.Default.MaxSlopeAngleDegrees
        };
        TriangleBoundaryCuller.Result generic = TriangleBoundaryCuller.Cull(
            vertices,
            extracted.VertexCount,
            extracted.Faces,
            extracted.FaceCount,
            inputXy,
            Array.Empty<int>(),
            settings);
        TriangleBoundaryCuller.Result native = TriangleBoundaryCuller.CullUsingNativeAdjacency(
            vertices,
            extracted.VertexCount,
            extracted.Faces,
            extracted.FaceCount,
            inputXy,
            Array.Empty<int>(),
            settings,
            extracted.Adjacency);

        Assert.Equal(generic.Changed, native.Changed);
        Assert.Equal(generic.VertexCount, native.VertexCount);
        Assert.Equal(generic.FaceCount, native.FaceCount);
        Assert.Equal(generic.Faces, native.Faces);
        Assert.Equal(generic.NewToOld, native.NewToOld);
    }

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
