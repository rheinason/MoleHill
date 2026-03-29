using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class PathGraderTests
{
    [Fact]
    public void Grade_InsertsShoulderVertices_WhenExplicitMaxDistanceFitsInsideBoundary()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 33.0,
            maxDistance: 1.5);

        var result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(EnumerateVertices(result!), vertex =>
            Math.Abs(vertex.x - 2.0) < 1e-6 &&
            Math.Abs(vertex.y - 7.5) < 1e-6);
    }

    [Fact]
    public void Grade_InsertsShoulderVertices_WhenAutoTransitionFindsTerrainDifference()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        var result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));

        double expectedShoulderY = 5.0 + 1.0 + 1.0 / Math.Tan(33.0 * Math.PI / 180.0);
        Assert.Contains(EnumerateVertices(result!), vertex =>
            Math.Abs(vertex.x - 2.0) < 1e-6 &&
            Math.Abs(vertex.y - expectedShoulderY) < 1e-6);
    }

    [Fact]
    public void Grade_DensifiesLongPathShoulders_WhenTransitionWidthIsLarge()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 50.0, 80.0, 50.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 15.0);

        var result = PathGrader.Grade(
            BuildLargeSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));

        int shoulderVertexCount = EnumerateVertices(result!)
            .Count(vertex => Math.Abs(vertex.y - 66.0) < 1e-6 && vertex.x >= 20.0 - 1e-6 && vertex.x <= 80.0 + 1e-6);

        Assert.True(shoulderVertexCount >= 5, $"Expected a densified shoulder apron, found {shoulderVertexCount} shoulder vertices.");
    }

    [Fact]
    public void CreateConstraints_RemeshesLongPathIntoApronBand()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 50.0, 80.0, 50.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 15.0);

        var constraints = PathGrader.CreateConstraints(
            BuildLargeSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            tolerance: 1e-3);

        var remesh = SurfaceRemesher.Remesh(
            BuildLargeSquareVertices(),
            BuildSquareFaces(),
            constraints.Constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = 1e-3,
                RequestedEdgeLength = constraints.SuggestedEdgeLength,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, remesh.Warning);

        int shoulderVertexCount = EnumerateVertices(remesh.Vertices)
            .Count(vertex => Math.Abs(vertex.y - 66.0) < 1e-6 && vertex.x >= 20.0 - 1e-6 && vertex.x <= 80.0 + 1e-6);

        Assert.True(shoulderVertexCount >= 5, $"Expected remesh constraints to create an apron band, found {shoulderVertexCount} shoulder vertices.");
    }

    [Fact]
    public void CreateConstraints_WithoutBoundaryLoop_StillBuildsRoadAndShoulderPolylines()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 0.0, 5.0, 10.0, 5.0 },
            zValues: new[] { 1.0, 1.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 33.0,
            maxDistance: 1.5);

        var constraints = PathGrader.CreateConstraints(
            new[]
            {
                0.0, 0.0, 0.0,
                10.0, 0.0, 0.0
            },
            2,
            Array.Empty<int>(),
            0,
            new[] { path },
            tolerance: 1e-3);

        Assert.Equal(5, constraints.Constraints.Length);
        Assert.True(constraints.SuggestedEdgeLength > 0.0);
        Assert.All(constraints.Constraints, constraint =>
        {
            Assert.True(constraint.PointCount >= 2);
            Assert.Equal(constraint.PointCount * 3, constraint.Points.Length);
        });
    }

    private static IEnumerable<(double x, double y, double z)> EnumerateVertices(GradingResult result)
    {
        for (int i = 0; i < result.VertexCount; i++)
            yield return (
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
    }

    private static IEnumerable<(double x, double y, double z)> EnumerateVertices(double[] vertices)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
            yield return (
                vertices[i * 3],
                vertices[i * 3 + 1],
                vertices[i * 3 + 2]);
    }

    private static double[] BuildSquareVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            10.0, 0.0, 0.0,
            10.0, 10.0, 0.0,
            0.0, 10.0, 0.0
        };
    }

    private static double[] BuildLargeSquareVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            100.0, 0.0, 0.0,
            100.0, 100.0, 0.0,
            0.0, 100.0, 0.0
        };
    }

    private static int[] BuildSquareFaces()
    {
        return new[]
        {
            0, 1, 2,
            0, 2, 3
        };
    }
}
