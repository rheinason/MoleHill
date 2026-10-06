using MoleHill.Core.Engine;
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

    [Fact]
    public void Trim_HideOnFineMesh_RemovesExactlyTheHiddenArea()
    {
        // RiR Master 002: Hide classified the conformed faces with the 12.5 mm boundary tolerance, so whole
        // outside triangles whose centroid fell within it were removed too. On a 20 mm grid that is every
        // outside triangle touching the hole: the hole grows and its border no longer follows the curve.
        const int n = 50;
        const double cell = 0.02;
        var vertices = new List<double>();
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                vertices.AddRange([x * cell, y * cell, 0.0]);
        var faces = new List<int>();
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int a = (y * (n + 1)) + x, b = a + 1, c = a + n + 2, d = a + n + 1;
                faces.AddRange([a, b, c, a, c, d]);
            }
        }

        TerrainBoundaryTrimmer.Result? result = TerrainBoundaryTrimmer.Trim(
            vertices.ToArray(), vertices.Count / 3, faces.ToArray(), faces.Count / 3,
            null, new[] { Boundary(0.3, 0.3, 0.7, 0.7) }, Array.Empty<MeshAreaSplitter.AreaBoundary>(),
            0.0125, out string? error);

        Assert.NotNull(result);
        Assert.Null(error);
        double area = 0.0;
        for (int t = 0; t < result!.FaceCount; t++)
        {
            int a = result.Faces[t * 3], b = result.Faces[(t * 3) + 1], c = result.Faces[(t * 3) + 2];
            double[] v = result.Vertices;
            area += Math.Abs(((v[b * 3] - v[a * 3]) * (v[(c * 3) + 1] - v[(a * 3) + 1])) - ((v[(b * 3) + 1] - v[(a * 3) + 1]) * (v[c * 3] - v[a * 3]))) * 0.5;
        }

        Assert.Equal(1.0 - 0.16, area, 9);
        Assert.Equal(2, MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount).BoundaryComponentCount);
    }

    private static MeshAreaSplitter.AreaBoundary Boundary(double minX, double minY, double maxX, double maxY) =>
        new(new[] { minX, minY, maxX, minY, maxX, maxY, minX, maxY }, 4);
}
