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

        Assert.Contains(
            constraints.Constraints,
            constraint => ContainsInteriorBoundaryVertex(constraint.Points, boundaryY: 10.0, tolerance: 1e-3));
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

        Assert.Contains(EnumerateVertices(result!), vertex =>
            Math.Abs(vertex.y - 10.0) <= 1e-3 &&
            vertex.x > 0.1 &&
            vertex.x < 9.9);
    }

    [Fact]
    public void Grade_WhenShoulderCrossesBoundary_ProducesSingleBoundaryLoop()
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
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase), errorMessage);
        Assert.Equal(1, CountBoundaryLoops(result!.Faces, result.FaceCount));
    }

    [Fact]
    public void CreateConstraints_WhenPathExtendsBeyondBoundary_ClipsAllConstraintPointsToBoundary()
    {
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 5.0, 8.0, 5.0, 12.0 },
            zValues: new[] { 5.0, 5.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 2.0);

        PathGrader.ConstraintSet constraints = PathGrader.CreateConstraints(
            BuildSquareVertices(),
            4,
            BuildSquareFaces(),
            2,
            new[] { path },
            tolerance: 1e-3);

        Assert.NotEmpty(constraints.Constraints);
        Assert.All(constraints.Constraints, AssertConstraintPointsInsideSquare);
        Assert.Contains(
            constraints.Constraints,
            constraint => ContainsInteriorBoundaryVertex(constraint.Points, boundaryY: 10.0, tolerance: 1e-3));
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

    private static bool ContainsInteriorBoundaryVertex(double[] points, double boundaryY, double tolerance)
    {
        for (int i = 0; i < points.Length / 3; i++)
        {
            double x = points[i * 3];
            double y = points[(i * 3) + 1];
            if (Math.Abs(y - boundaryY) <= tolerance && x > tolerance && x < 10.0 - tolerance)
                return true;
        }

        return false;
    }

    private static void AssertConstraintPointsInsideSquare(SurfaceRemesher.ConstraintPolyline constraint)
    {
        for (int i = 0; i < constraint.PointCount; i++)
        {
            double x = constraint.Points[i * 3];
            double y = constraint.Points[(i * 3) + 1];
            Assert.InRange(x, -1e-6, 10.0 + 1e-6);
            Assert.InRange(y, -1e-6, 10.0 + 1e-6);
        }
    }

    private static IEnumerable<(double x, double y, double z)> EnumerateVertices(GradingResult result)
    {
        for (int i = 0; i < result.VertexCount; i++)
            yield return (
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
    }

    private static int CountBoundaryLoops(int[] faces, int faceCount)
    {
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            IncrementEdge(edgeFaceCount, faces[faceIndex * 3], faces[faceIndex * 3 + 1]);
            IncrementEdge(edgeFaceCount, faces[faceIndex * 3 + 1], faces[faceIndex * 3 + 2]);
            IncrementEdge(edgeFaceCount, faces[faceIndex * 3 + 2], faces[faceIndex * 3]);
        }

        var adjacency = new Dictionary<int, HashSet<int>>();
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
        }

        int loops = 0;
        var visited = new HashSet<int>();
        foreach (int vertex in adjacency.Keys)
        {
            if (!visited.Add(vertex))
                continue;

            loops++;
            var queue = new Queue<int>();
            queue.Enqueue(vertex);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in adjacency[current])
                {
                    if (visited.Add(next))
                        queue.Enqueue(next);
                }
            }
        }

        return loops;
    }

    private static void IncrementEdge(Dictionary<long, int> edgeFaceCount, int a, int b)
    {
        long key = IndexedMeshTools.GetEdgeKey(a, b);
        edgeFaceCount.TryGetValue(key, out int value);
        edgeFaceCount[key] = value + 1;
    }

    private static void AddBoundaryNeighbor(Dictionary<int, HashSet<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out HashSet<int>? neighbors))
        {
            neighbors = new HashSet<int>();
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
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
