using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class TerrainBoundaryTrimmerTests
{
    private static readonly double[] Vertices =
    {
        0, 0, 0,
        2, 0, 2,
        2, 2, 4,
        0, 2, 2
    };

    private static readonly int[] Faces = { 0, 1, 2, 0, 2, 3 };

    [Fact]
    public void Trim_OuterBoundary_ConformsAndInterpolatesFinishedSurface()
    {
        var outer = Boundary(0.25, 0.25, 1.75, 1.75);

        TerrainBoundaryTrimmer.Result result = TerrainBoundaryTrimmer.Trim(
            Vertices, 4, Faces, 2, outer,
            Array.Empty<MeshAreaSplitter.AreaBoundary>(), Array.Empty<MeshAreaSplitter.AreaBoundary>(),
            1e-8, out string? error) !;

        Assert.Null(error);
        Assert.True(result.FaceCount > 0);
        for (int i = 0; i < result.VertexCount; i++)
        {
            double x = result.Vertices[i * 3];
            double y = result.Vertices[(i * 3) + 1];
            double z = result.Vertices[(i * 3) + 2];
            Assert.InRange(x, 0.25 - 1e-8, 1.75 + 1e-8);
            Assert.InRange(y, 0.25 - 1e-8, 1.75 + 1e-8);
            Assert.Equal(x + y, z, 7);
        }
    }

    [Fact]
    public void Trim_HideAndShow_ShowRestoresNestedIsland()
    {
        TerrainBoundaryTrimmer.Result result = TerrainBoundaryTrimmer.Trim(
            Vertices, 4, Faces, 2, null,
            new[] { Boundary(0.25, 0.25, 1.75, 1.75) },
            new[] { Boundary(0.75, 0.75, 1.25, 1.25) },
            1e-8, out string? error) !;

        Assert.Null(error);
        Assert.True(result.ShowRestoredAnyFace);
        Assert.True(result.FaceCount > 0);
        Assert.Contains(Enumerable.Range(0, result.FaceCount), face =>
        {
            int a = result.Faces[face * 3];
            int b = result.Faces[(face * 3) + 1];
            int c = result.Faces[(face * 3) + 2];
            double x = (result.Vertices[a * 3] + result.Vertices[b * 3] + result.Vertices[c * 3]) / 3.0;
            double y = (result.Vertices[(a * 3) + 1] + result.Vertices[(b * 3) + 1] + result.Vertices[(c * 3) + 1]) / 3.0;
            return x > 0.75 && x < 1.25 && y > 0.75 && y < 1.25;
        });
    }

    [Fact]
    public void Trim_HideEverything_ReturnsEmptyResult()
    {
        TerrainBoundaryTrimmer.Result result = TerrainBoundaryTrimmer.Trim(
            Vertices, 4, Faces, 2, null,
            new[] { Boundary(-1, -1, 3, 3) }, Array.Empty<MeshAreaSplitter.AreaBoundary>(),
            1e-8, out string? error) !;

        Assert.Null(error);
        Assert.Equal(0, result.FaceCount);
        Assert.Equal(0, result.VertexCount);
    }

    private static MeshAreaSplitter.AreaBoundary Boundary(double minX, double minY, double maxX, double maxY) =>
        new(new[] { minX, minY, maxX, minY, maxX, maxY, minX, maxY }, 4);
}
