using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class PathGraderBoundaryClippingTests
{
    [Fact]
    public void CreateConstraints_WhenShoulderCrossesBoundary_ClipsRunToBoundary()
    {
        var path = BuildBoundaryCrossingPath();

        PathGrader.ConstraintSet constraints = PathGrader.CreateConstraints(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            tolerance: 1e-3);

        (double expectedX, double expectedY) = ComputeExpectedClippedLeftShoulderPoint(path);
        Assert.Contains(
            constraints.Constraints,
            constraint => ContainsVertex(constraint.Points, expectedX, expectedY, tolerance: 1e-6));
    }

    [Fact]
    public void Grade_WhenShoulderCrossesBoundary_PreservesClippedShoulderVertex()
    {
        var path = BuildBoundaryCrossingPath();

        GradingResult? result = PathGrader.Grade(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));

        (double expectedX, double expectedY) = ComputeExpectedClippedLeftShoulderPoint(path);
        Assert.Contains(EnumerateVertices(result!), vertex =>
            Math.Abs(vertex.x - expectedX) <= 1e-6 &&
            Math.Abs(vertex.y - expectedY) <= 1e-6);
    }

    private static PathGrader.PathDefinition BuildBoundaryCrossingPath()
    {
        // Path Z = 5, terrain Z = 0 → slope-cast distance = 5, capped at maxDistance = 2.
        // This ensures per-vertex distance equals maxDistance (same geometry as the original test intent).
        return new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 5.0, 8.0, 9.0 },
            zValues: new[] { 5.0, 5.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 2.0);
    }

    private static (double x, double y) ComputeExpectedClippedLeftShoulderPoint(PathGrader.PathDefinition path)
    {
        double ax = path.XyVertices[0];
        double ay = path.XyVertices[1];
        double bx = path.XyVertices[2];
        double by = path.XyVertices[3];
        double dx = bx - ax;
        double dy = by - ay;
        double len = Math.Sqrt((dx * dx) + (dy * dy));
        double shoulderOffset = (path.Width * 0.5) + path.MaxDistance;

        double leftX0 = ax - ((dy / len) * shoulderOffset);
        double leftY0 = ay + ((dx / len) * shoulderOffset);
        double leftX1 = bx - ((dy / len) * shoulderOffset);
        double leftY1 = by + ((dx / len) * shoulderOffset);

        double t = (10.0 - leftY0) / (leftY1 - leftY0);
        return (leftX0 + ((leftX1 - leftX0) * t), 10.0);
    }

    private static bool ContainsVertex(double[] points, double x, double y, double tolerance)
    {
        for (int i = 0; i < points.Length / 3; i++)
        {
            if (Math.Abs(points[i * 3] - x) <= tolerance &&
                Math.Abs(points[i * 3 + 1] - y) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(double x, double y, double z)> EnumerateVertices(GradingResult result)
    {
        for (int i = 0; i < result.VertexCount; i++)
            yield return (
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
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

    private static int[] BuildSquareFaces()
    {
        return new[]
        {
            0, 1, 2,
            0, 2, 3
        };
    }
}
