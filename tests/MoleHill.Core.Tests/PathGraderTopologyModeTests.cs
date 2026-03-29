using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class PathGraderTopologyModeTests
{
    [Fact]
    public void ApplyGradingZ_WhenPathAlreadyMatchesTerrain_ReportsZeroChangedVertices()
    {
        double[] vertices = BuildGridVertices(size: 11, spacing: 10.0);
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 55.0, 80.0, 55.0 },
            zValues: new[] { 0.0, 0.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 10.0);

        double[] graded = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, new[] { path }, out int changedVertexCount);

        Assert.Equal(0, changedVertexCount);
        Assert.Equal(vertices, graded);
    }

    [Fact]
    public void RemeshTopologyMode_WithAdditionalHardConstraint_GradesInsertedRoadBand()
    {
        double[] vertices = BuildGridVertices(size: 11, spacing: 10.0);
        int[] faces = BuildGridFaces(size: 11);

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 55.0, 80.0, 55.0 },
            zValues: new[] { 5.0, 5.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 10.0);

        double[] withoutRemesh = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, new[] { path }, out int changedWithoutRemesh);
        Assert.True(changedWithoutRemesh > 0);
        Assert.Empty(FindVerticesNearLine(withoutRemesh, y: 55.0, targetZ: 5.0, tolerance: 1e-6));

        PathGrader.ConstraintSet pathConstraints = PathGrader.CreateConstraints(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            new[] { path },
            tolerance: 1e-3);

        var hardConstraint = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                50.0, 10.0, 2.0,
                50.0, 90.0, 2.0
            },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);

        var constraints = new SurfaceRemesher.ConstraintPolyline[pathConstraints.Constraints.Length + 1];
        Array.Copy(pathConstraints.Constraints, constraints, pathConstraints.Constraints.Length);
        constraints[^1] = hardConstraint;

        var remesh = SurfaceRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = 1e-3,
                RequestedEdgeLength = pathConstraints.SuggestedEdgeLength,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, remesh.Warning);

        double[] graded = PathGrader.ApplyGradingZ(remesh.Vertices, remesh.Vertices.Length / 3, new[] { path }, out int changedWithRemesh);

        Assert.True(changedWithRemesh > 0);
        Assert.NotEmpty(FindVerticesNearLine(graded, y: 55.0, targetZ: 5.0, tolerance: 1e-6));
    }

    [Fact]
    public void RemeshTopologyMode_WithNearbyParallelHardBreakline_PreservesRoadCenterlineVertices()
    {
        double[] vertices = BuildGridVertices(size: 11, spacing: 10.0);
        int[] faces = BuildGridFaces(size: 11);

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 55.0, 80.0, 55.0 },
            zValues: new[] { 5.0, 5.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 10.0);

        PathGrader.ConstraintSet pathConstraints = PathGrader.CreateConstraints(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            new[] { path },
            tolerance: 1e-3);

        var hardConstraint = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                20.0, 57.8, 0.0,
                80.0, 57.8, 0.0
            },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);

        var constraints = new SurfaceRemesher.ConstraintPolyline[pathConstraints.Constraints.Length + 1];
        constraints[0] = hardConstraint;
        Array.Copy(pathConstraints.Constraints, 0, constraints, 1, pathConstraints.Constraints.Length);

        var remesh = SurfaceRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = 1e-3,
                RequestedEdgeLength = pathConstraints.SuggestedEdgeLength,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, remesh.Warning);

        double[] graded = PathGrader.ApplyGradingZ(remesh.Vertices, remesh.Vertices.Length / 3, new[] { path }, out int changedVertexCount);

        Assert.True(changedVertexCount > 0);
        Assert.NotEmpty(FindVerticesNearLine(graded, y: 55.0, targetZ: 5.0, tolerance: 1e-6));
    }

    [Fact]
    public void RemeshTopologyMode_OnCoarseEnvelope_WithNearbyParallelHardBreakline_PreservesRoadCenterlineVertices()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            100.0, 0.0, 0.0,
            100.0, 100.0, 0.0,
            0.0, 100.0, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 2, 3
        };

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 55.0, 80.0, 55.0 },
            zValues: new[] { 5.0, 5.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 10.0);

        PathGrader.ConstraintSet pathConstraints = PathGrader.CreateConstraints(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            new[] { path },
            tolerance: 1e-3);

        var hardConstraint = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                20.0, 57.8, 0.0,
                80.0, 57.8, 0.0
            },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);

        var constraints = new SurfaceRemesher.ConstraintPolyline[pathConstraints.Constraints.Length + 1];
        constraints[0] = hardConstraint;
        Array.Copy(pathConstraints.Constraints, 0, constraints, 1, pathConstraints.Constraints.Length);

        var remesh = SurfaceRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = 1e-3,
                RequestedEdgeLength = pathConstraints.SuggestedEdgeLength,
                ProtectSharpEdges = true
            });

        Assert.True(remesh.Success, remesh.Warning);

        double[] graded = PathGrader.ApplyGradingZ(remesh.Vertices, remesh.Vertices.Length / 3, new[] { path }, out int changedVertexCount);

        Assert.True(changedVertexCount > 0);
        Assert.NotEmpty(FindVerticesNearLine(graded, y: 55.0, targetZ: 5.0, tolerance: 1e-6));
    }

    [Fact]
    public void Grade_WithNearbyParallelHardBreakline_PreservesRoadCenterlineVertices()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            100.0, 0.0, 0.0,
            100.0, 100.0, 0.0,
            0.0, 100.0, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 2, 3
        };

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 55.0, 80.0, 55.0 },
            zValues: new[] { 5.0, 5.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 10.0);

        var hardConstraint = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                20.0, 57.8, 0.0,
                80.0, 57.8, 0.0
            },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);

        GradingResult? result = PathGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            new[] { path },
            new[] { hardConstraint },
            out string? warning);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(warning) || !warning.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(FindVerticesNearLine(result!.Vertices, y: 55.0, targetZ: 5.0, tolerance: 1e-6));
    }

    [Fact]
    public void ApplyGradingZ_WithCrossingPaths_IsOrderIndependentAndBlendsIntersection()
    {
        double[] vertices = BuildGridVertices(size: 3, spacing: 10.0);
        int centerIndex = GetVertexIndex(size: 3, x: 1, y: 1);
        int horizontalOnlyIndex = GetVertexIndex(size: 3, x: 0, y: 1);
        int verticalOnlyIndex = GetVertexIndex(size: 3, x: 1, y: 0);

        var horizontal = new PathGrader.PathDefinition(
            xyVertices: new[] { 0.0, 10.0, 20.0, 10.0 },
            zValues: new[] { 2.0, 2.0 },
            vertexCount: 2,
            width: 12.0,
            slopeAngleDeg: 45.0,
            maxDistance: 5.0);
        var vertical = new PathGrader.PathDefinition(
            xyVertices: new[] { 10.0, 0.0, 10.0, 20.0 },
            zValues: new[] { 8.0, 8.0 },
            vertexCount: 2,
            width: 12.0,
            slopeAngleDeg: 45.0,
            maxDistance: 5.0);

        double[] forward = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, new[] { horizontal, vertical });
        double[] reversed = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, new[] { vertical, horizontal });

        AssertVerticesEqual(forward, reversed, tolerance: 1e-6);
        Assert.Equal(5.0, forward[(centerIndex * 3) + 2], 6);
        Assert.Equal(2.0, forward[(horizontalOnlyIndex * 3) + 2], 6);
        Assert.Equal(8.0, forward[(verticalOnlyIndex * 3) + 2], 6);
    }

    [Fact]
    public void ApplyGradingZ_WithTJunction_IsOrderIndependentNearSharedNode()
    {
        double[] vertices = BuildGridVertices(size: 5, spacing: 5.0);
        int junctionIndex = GetVertexIndex(size: 5, x: 2, y: 2);
        int blendIndex = GetVertexIndex(size: 5, x: 2, y: 1);

        var mainPath = new PathGrader.PathDefinition(
            xyVertices: new[] { 0.0, 10.0, 20.0, 10.0 },
            zValues: new[] { 3.0, 3.0 },
            vertexCount: 2,
            width: 8.0,
            slopeAngleDeg: 45.0,
            maxDistance: 4.0);
        var branchPath = new PathGrader.PathDefinition(
            xyVertices: new[] { 10.0, 0.0, 10.0, 10.0 },
            zValues: new[] { 9.0, 9.0 },
            vertexCount: 2,
            width: 8.0,
            slopeAngleDeg: 45.0,
            maxDistance: 4.0);

        double[] forward = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, new[] { mainPath, branchPath });
        double[] reversed = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, new[] { branchPath, mainPath });

        AssertVerticesEqual(forward, reversed, tolerance: 1e-6);
        Assert.Equal(6.0, forward[(junctionIndex * 3) + 2], 6);

        double blendedZ = forward[(blendIndex * 3) + 2];
        Assert.InRange(blendedZ, 6.0, 9.0);
    }


    private static List<int> FindVerticesNearLine(double[] vertices, double y, double targetZ, double tolerance)
    {
        var matches = new List<int>();
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            if (Math.Abs(vertices[i * 3 + 1] - y) <= tolerance &&
                Math.Abs(vertices[i * 3 + 2] - targetZ) <= tolerance)
            {
                matches.Add(i);
            }
        }

        return matches;
    }

    private static void AssertVerticesEqual(double[] expected, double[] actual, double tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"Vertex value mismatch at index {i}: expected {expected[i]}, got {actual[i]}.");
    }

    private static int GetVertexIndex(int size, int x, int y)
    {
        return (y * size) + x;
    }

    private static double[] BuildGridVertices(int size, double spacing)
    {
        var vertices = new double[size * size * 3];
        int index = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                vertices[index++] = x * spacing;
                vertices[index++] = y * spacing;
                vertices[index++] = 0.0;
            }
        }

        return vertices;
    }

    private static int[] BuildGridFaces(int size)
    {
        var faces = new int[(size - 1) * (size - 1) * 6];
        int index = 0;
        for (int y = 0; y < size - 1; y++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                int a = (y * size) + x;
                int b = a + 1;
                int c = a + size;
                int d = c + 1;

                faces[index++] = a;
                faces[index++] = b;
                faces[index++] = d;
                faces[index++] = a;
                faces[index++] = d;
                faces[index++] = c;
            }
        }

        return faces;
    }
}
