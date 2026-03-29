using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshAreaSplitterTests
{
    [Fact]
    public void Split_SingleInnerArea_LeavesOutsideFacesUnassigned()
    {
        var result = MeshAreaSplitter.Split(
            CreatePlanarMeshVertices(),
            4,
            CreateMeshFaces(),
            2,
            new[]
            {
                new MeshAreaSplitter.AreaBoundary(
                    new[] { 1.0, 1.0, 3.0, 1.0, 3.0, 3.0, 1.0, 3.0 },
                    4)
            },
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            0.001,
            0,
            0,
            out _);

        Assert.NotNull(result);
        Assert.Contains(result!.FaceAreaIndex, area => area == 0);
        Assert.Contains(result.FaceAreaIndex, area => area == -1);
    }

    [Fact]
    public void Split_OverlappingAreas_LaterAreaWinsWithinOverlap()
    {
        var outer = new MeshAreaSplitter.AreaBoundary(
            new[] { 0.5, 0.5, 3.5, 0.5, 3.5, 3.5, 0.5, 3.5 },
            4);
        var inner = new MeshAreaSplitter.AreaBoundary(
            new[] { 1.5, 1.5, 2.5, 1.5, 2.5, 2.5, 1.5, 2.5 },
            4);

        var result = MeshAreaSplitter.Split(
            CreatePlanarMeshVertices(),
            4,
            CreateMeshFaces(),
            2,
            new[] { outer, inner },
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            0.001,
            0,
            0,
            out _);

        Assert.NotNull(result);

        int innerFaceCount = 0;
        int outerOnlyFaceCount = 0;
        for (int faceIndex = 0; faceIndex < result!.FaceCount; faceIndex++)
        {
            var (cx, cy) = GetFaceCentroid(result, faceIndex);
            if (PadGrader.PointInPolygon(cx, cy, inner.XyVertices, inner.VertexCount))
            {
                Assert.Equal(1, result.FaceAreaIndex[faceIndex]);
                innerFaceCount++;
            }
            else if (PadGrader.PointInPolygon(cx, cy, outer.XyVertices, outer.VertexCount))
            {
                Assert.Equal(0, result.FaceAreaIndex[faceIndex]);
                outerOnlyFaceCount++;
            }
        }

        Assert.True(innerFaceCount > 0);
        Assert.True(outerOnlyFaceCount > 0);
    }

    [Fact]
    public void Split_InsertedBoundaryVertices_UseInterpolatedPlaneElevation()
    {
        var vertices = new[]
        {
            0.0, 0.0, 0.0,
            4.0, 0.0, 4.0,
            4.0, 4.0, 8.0,
            0.0, 4.0, 4.0
        };

        var result = MeshAreaSplitter.Split(
            vertices,
            4,
            CreateMeshFaces(),
            2,
            new[]
            {
                new MeshAreaSplitter.AreaBoundary(
                    new[] { 1.0, 1.0, 3.0, 1.0, 3.0, 3.0, 1.0, 3.0 },
                    4)
            },
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            0.001,
            0,
            0,
            out _);

        Assert.NotNull(result);

        AssertVertexZ(result!, 1.0, 1.0, 2.0);
        AssertVertexZ(result, 3.0, 1.0, 4.0);
        AssertVertexZ(result, 3.0, 3.0, 6.0);
        AssertVertexZ(result, 1.0, 3.0, 4.0);
    }

    [Fact]
    public void Split_PersistentHardConstraint_PreservesConstraintElevation()
    {
        var hardConstraint = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                0.0, 2.0, 3.0,
                4.0, 2.0, 3.0
            },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);

        var result = MeshAreaSplitter.Split(
            CreatePlanarMeshVertices(),
            4,
            CreateMeshFaces(),
            2,
            new[]
            {
                new MeshAreaSplitter.AreaBoundary(
                    new[] { 1.0, 1.0, 3.0, 1.0, 3.0, 3.0, 1.0, 3.0 },
                    4)
            },
            new[] { hardConstraint },
            0.001,
            0,
            0,
            out _);

        Assert.NotNull(result);

        var constraintVertices = Enumerable.Range(0, result!.VertexCount)
            .Where(index => Math.Abs(result.Vertices[index * 3 + 1] - 2.0) < 1e-6)
            .ToList();

        Assert.NotEmpty(constraintVertices);
        Assert.All(constraintVertices, index => Assert.Equal(3.0, result.Vertices[index * 3 + 2], 6));
    }

    [Fact]
    public void Classify_CentroidNearBoundaryWithinTolerance_TreatsFaceAsInside()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 2, 3
        };
        var boundary = new MeshAreaSplitter.AreaBoundary(
            new[] { 0.34, 0.0, 1.0, 0.0, 1.0, 1.0, 0.34, 1.0 },
            4);

        var strict = MeshAreaSplitter.Classify(
            vertices,
            4,
            faces,
            2,
            new[] { boundary },
            0.0,
            out _);
        var tolerant = MeshAreaSplitter.Classify(
            vertices,
            4,
            faces,
            2,
            new[] { boundary },
            0.01,
            out _);

        Assert.NotNull(strict);
        Assert.NotNull(tolerant);
        Assert.Equal(-1, strict!.FaceAreaIndex[1]);
        Assert.Equal(0, tolerant!.FaceAreaIndex[1]);
    }

    private static double[] CreatePlanarMeshVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            4.0, 0.0, 0.0,
            4.0, 4.0, 0.0,
            0.0, 4.0, 0.0
        };
    }

    private static int[] CreateMeshFaces()
    {
        return new[]
        {
            0, 1, 2,
            0, 2, 3
        };
    }

    private static (double X, double Y) GetFaceCentroid(MeshAreaSplitter.SplitResult result, int faceIndex)
    {
        int i0 = result.Faces[faceIndex * 3];
        int i1 = result.Faces[faceIndex * 3 + 1];
        int i2 = result.Faces[faceIndex * 3 + 2];

        double cx = (result.Vertices[i0 * 3] + result.Vertices[i1 * 3] + result.Vertices[i2 * 3]) / 3.0;
        double cy = (result.Vertices[i0 * 3 + 1] + result.Vertices[i1 * 3 + 1] + result.Vertices[i2 * 3 + 1]) / 3.0;
        return (cx, cy);
    }

    private static void AssertVertexZ(MeshAreaSplitter.SplitResult result, double x, double y, double expectedZ)
    {
        const double tolerance = 1e-9;
        int vertexIndex = FindVertex(result, x, y, tolerance);
        Assert.True(vertexIndex >= 0, $"Could not find vertex at ({x}, {y}).");
        Assert.Equal(expectedZ, result.Vertices[vertexIndex * 3 + 2], 6);
    }

    private static int FindVertex(MeshAreaSplitter.SplitResult result, double x, double y, double tolerance)
    {
        for (int i = 0; i < result.VertexCount; i++)
        {
            double dx = result.Vertices[i * 3] - x;
            double dy = result.Vertices[i * 3 + 1] - y;
            if (Math.Abs(dx) <= tolerance && Math.Abs(dy) <= tolerance)
                return i;
        }

        return -1;
    }
}
