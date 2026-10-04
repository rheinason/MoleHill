using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class TriangulationHelperTests
{
    [Fact]
    public void Triangulate_SimpleSquare_SucceedsWithoutFlags()
    {
        var outcome = TriangulationHelper.Triangulate(
            new List<double>
            {
                0.0, 0.0,
                10.0, 0.0,
                10.0, 10.0,
                0.0, 10.0
            },
            vertexCount: 4,
            new List<(int a, int b)>(),
            maxArea: 0.0,
            minAngle: 0.0,
            convex: true);

        Assert.NotNull(outcome.Mesh);
        Assert.Equal(TriangulationWarningFlags.None, outcome.Flags);
        Assert.Null(outcome.WarningMessage);
        Assert.Empty(outcome.FailureDetails);
    }

    [Fact]
    public void Triangulate_AllAttemptsFail_ReturnsFailureDetails()
    {
        var outcome = TriangulationHelper.Triangulate(
            new List<double>
            {
                0.0, 0.0,
                1.0, 0.0,
                2.0, 0.0
            },
            vertexCount: 3,
            new List<(int a, int b)>(),
            maxArea: 0.0,
            minAngle: 0.0,
            convex: true);

        Assert.Null(outcome.Mesh);
        Assert.Contains("All triangulation attempts failed", outcome.WarningMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.NotEmpty(outcome.FailureDetails);
    }

    [Fact]
    public void Triangulate_TJunction_SplitsConstraintWithoutAddingVertices()
    {
        var xy = new List<double>
        {
            0.0, 0.0,
            10.0, 0.0,
            10.0, 10.0,
            0.0, 10.0,
            5.0, 0.0,
            5.0, 5.0
        };
        var segments = new List<(int a, int b)>
        {
            (0, 1),
            (4, 5)
        };

        var outcome = TriangulationHelper.Triangulate(
            xy, vertexCount: 6, segments, maxArea: 0.0, minAngle: 0.0, convex: true);

        Assert.NotNull(outcome.Mesh);
        Assert.Equal(TriangulationWarningFlags.None, outcome.Flags);
        Assert.Equal(new List<(int a, int b)> { (0, 1), (4, 5) }, segments);
        Assert.Equal(6, outcome.Mesh!.Vertices.Count);
        var edges = outcome.Mesh.Segments
            .Select(segment => (Math.Min(segment.P0, segment.P1), Math.Max(segment.P0, segment.P1)))
            .ToHashSet();
        Assert.Contains((0, 4), edges);
        Assert.Contains((1, 4), edges);
        Assert.Contains((4, 5), edges);
        Assert.DoesNotContain((0, 1), edges);
    }
}
