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
        int[] faces = BuildGridFaces(size: 11);
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 55.0, 80.0, 55.0 },
            zValues: new[] { 0.0, 0.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 10.0);

        double[] graded = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { path }, out int changedVertexCount);

        Assert.Equal(0, changedVertexCount);
        Assert.Equal(vertices, graded);
    }

    [Fact]
    public void ApplyGradingZ_WithPreservedBreaklineBetweenRoadAndVertex_StopsShoulderPropagation()
    {
        const int size = 11;
        double[] vertices = BuildGridVertices(size, spacing: 10.0);
        int[] faces = BuildGridFaces(size);
        int blockedVertexIndex = GetVertexIndex(size, x: 5, y: 6);

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 55.0, 80.0, 55.0 },
            zValues: new[] { 5.0, 5.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 10.0);

        double[] withoutBarrier = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { path }, out _);
        Assert.Equal(2.0, withoutBarrier[(blockedVertexIndex * 3) + 2], 6);

        var hardConstraint = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                20.0, 58.0, 0.0,
                80.0, 58.0, 0.0
            },
            PointCount: 2,
            IsClosed: false,
            PreserveInputElevation: true);

        double[] withBarrier = PathGrader.ApplyGradingZ(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            new[] { path },
            new[] { hardConstraint },
            out int changedVertexCount);

        Assert.True(changedVertexCount > 0);
        Assert.Equal(0.0, withBarrier[(blockedVertexIndex * 3) + 2], 6);
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

        double[] withoutRemesh = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { path }, out int changedWithoutRemesh);
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

        double[] graded = PathGrader.ApplyGradingZ(remesh.Vertices, remesh.Vertices.Length / 3, remesh.Faces, remesh.Faces.Length / 3, new[] { path }, out int changedWithRemesh);

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

        double[] graded = PathGrader.ApplyGradingZ(remesh.Vertices, remesh.Vertices.Length / 3, remesh.Faces, remesh.Faces.Length / 3, new[] { path }, out int changedVertexCount);

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

        double[] graded = PathGrader.ApplyGradingZ(remesh.Vertices, remesh.Vertices.Length / 3, remesh.Faces, remesh.Faces.Length / 3, new[] { path }, out int changedVertexCount);

        Assert.True(changedVertexCount > 0);
        Assert.NotEmpty(FindVerticesNearLine(graded, y: 55.0, targetZ: 5.0, tolerance: 1e-6));
    }

    [Fact]
    public void CreateConstraints_OnCoarseEnvelope_DoesNotEmitShoulderGuideCrossSections()
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

        Assert.DoesNotContain(pathConstraints.Constraints, constraint => constraint.PointCount == 2);
    }

    [Fact]
    public void Grade_OnCoarseEnvelope_DoesNotRequireShoulderGuideEdgesInTopology()
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

        GradingResult? result = PathGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            new[] { path },
            out string? warning);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(warning) || !warning.Contains("failed", StringComparison.OrdinalIgnoreCase));
        PadInvariantAssert.AssertWatertightManifold(result!);

        // Road edge sits at the road elevation (z=5) along y=57; the batter daylights to ground
        // (z=0) at y=62 (terrain z=0, path z=5, 45° slope → reach 5, shoulder at 55+2+5=62).
        Assert.True(HasVertexAt(result!.Vertices, y: 57.0, z: 5.0), "Expected a road edge vertex at y=57, z=5.");
        Assert.True(HasVertexAt(result.Vertices, y: 62.0, z: 0.0), "Expected a daylight vertex at y=62, z=0.");
    }

    private static bool HasVertexAt(double[] vertices, double y, double z, double yTol = 0.25, double zTol = 0.25)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            if (Math.Abs(vertices[i * 3 + 1] - y) <= yTol && Math.Abs(vertices[i * 3 + 2] - z) <= zTol)
                return true;
        }

        return false;
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
        PadInvariantAssert.AssertWatertightManifold(result!);
        // The road surface is held at its elevation (z=5) across the corridor band (y in [53,57]).
        Assert.True(HasVertexInBandAtZ(result!.Vertices, minY: 53.0, maxY: 57.0, z: 5.0),
            "Expected a preserved road-surface vertex at z=5 within the corridor band.");
    }

    private static bool HasVertexInBandAtZ(double[] vertices, double minY, double maxY, double z, double zTol = 0.25)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            double y = vertices[i * 3 + 1];
            if (y >= minY - 1e-6 && y <= maxY + 1e-6 && Math.Abs(vertices[i * 3 + 2] - z) <= zTol)
                return true;
        }

        return false;
    }

    [Fact]
    public void Grade_PreservesUntouchedElevationsOutsidePathCorridor()
    {
        double[] vertices = BuildGridVertices(size: 6, spacing: 10.0);
        int[] faces = BuildGridFaces(size: 6);

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 10.0, 25.0, 40.0, 25.0 },
            zValues: new[] { 5.0, 5.0 },
            vertexCount: 2,
            width: 4.0,
            slopeAngleDeg: 45.0,
            maxDistance: 6.0);

        GradingResult? result = PathGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            new[] { path },
            out string? warning);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(warning) || !warning.Contains("failed", StringComparison.OrdinalIgnoreCase));

        // Constraint-first rebuilds may change triangulation, but far-field elevations must stay unchanged.
        var farFieldVertices = Enumerable.Range(0, result!.VertexCount)
            .Where(index =>
                result.Vertices[index * 3] <= 10.0 + 1e-6 &&
                result.Vertices[(index * 3) + 1] <= 10.0 + 1e-6)
            .ToArray();

        Assert.NotEmpty(farFieldVertices);
        foreach (int index in farFieldVertices)
            Assert.Equal(0.0, result.Vertices[(index * 3) + 2], 6);
    }

    [Fact]
    public void ApplyGradingZ_WithCrossingPaths_IsOrderIndependentAndBlendsIntersection()
    {
        double[] vertices = BuildGridVertices(size: 3, spacing: 10.0);
        int[] faces = BuildGridFaces(size: 3);
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

        double[] forward = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { horizontal, vertical }, out _);
        double[] reversed = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { vertical, horizontal }, out _);

        AssertVerticesEqual(forward, reversed, tolerance: 1e-6);
        Assert.Equal(5.0, forward[(centerIndex * 3) + 2], 6);
        Assert.Equal(2.0, forward[(horizontalOnlyIndex * 3) + 2], 6);
        Assert.Equal(8.0, forward[(verticalOnlyIndex * 3) + 2], 6);
    }

    [Fact]
    public void ApplyGradingZ_WithTJunction_IsOrderIndependentNearSharedNode()
    {
        double[] vertices = BuildGridVertices(size: 5, spacing: 5.0);
        int[] faces = BuildGridFaces(size: 5);
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

        double[] forward = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { mainPath, branchPath }, out _);
        double[] reversed = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { branchPath, mainPath }, out _);

        AssertVerticesEqual(forward, reversed, tolerance: 1e-6);
        Assert.Equal(6.0, forward[(junctionIndex * 3) + 2], 6);

        double blendedZ = forward[(blendIndex * 3) + 2];
        Assert.InRange(blendedZ, 6.0, 9.0);
    }

    [Fact]
    public void ApplyGradingZ_WithNoisyShoulderTerrain_BuildsContinuousShoulderBand()
    {
        const int size = 21;
        double[] vertices = BuildGridVertices(size, spacing: 1.0);
        int[] faces = BuildGridFaces(size);
        for (int y = 12; y <= 14; y++)
        {
            for (int x = 2; x <= 18; x++)
                vertices[(GetVertexIndex(size, x, y) * 3) + 2] = (x % 4) < 2 ? 4.0 : 0.0;
        }

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 10.0, 18.0, 10.0 },
            zValues: new[] { 0.0, 0.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 4.0);

        double[] graded = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { path }, out int changedVertexCount);

        Assert.True(changedVertexCount > 0);

        int solvedCount = 0;
        for (int x = 6; x <= 14; x++)
        {
            double z = graded[(GetVertexIndex(size, x, 12) * 3) + 2];
            Assert.InRange(z, -1e-6, 1.05);
            if (z > 0.9)
                solvedCount++;
        }

        Assert.True(solvedCount >= 6, $"Expected most row-12 samples to follow the solved one-meter cut, found only {solvedCount} solved samples.");
    }

    [Fact]
    public void ApplyGradingZ_WithExplicitMaxDistanceGreaterThanTrueDaylight_StopsAtFirstDaylight()
    {
        const int size = 21;
        double[] vertices = BuildGridVertices(size, spacing: 1.0);
        int[] faces = BuildGridFaces(size);
        int insideTransition = GetVertexIndex(size, x: 10, y: 12);
        int beyondDaylight = GetVertexIndex(size, x: 10, y: 14);

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 2.0, 10.0, 18.0, 10.0 },
            zValues: new[] { 2.0, 2.0 },
            vertexCount: 2,
            width: 2.0,
            slopeAngleDeg: 45.0,
            maxDistance: 10.0);

        double[] graded = PathGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { path }, out int changedVertexCount);

        Assert.True(changedVertexCount > 0);
        Assert.Equal(1.0, graded[(insideTransition * 3) + 2], 3);
        Assert.Equal(0.0, graded[(beyondDaylight * 3) + 2], 3);
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

    private static int FindVertexIndex(double[] vertices, double x, double y)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            if (Math.Abs(vertices[i * 3] - x) <= 1e-6 &&
                Math.Abs(vertices[i * 3 + 1] - y) <= 1e-6)
            {
                return i;
            }
        }

        throw new Xunit.Sdk.XunitException($"Could not find vertex at ({x}, {y}).");
    }

    private static bool ContainsVertex(double[] vertices, double x, double y, double tolerance = 1e-6)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            if (Math.Abs(vertices[i * 3] - x) <= tolerance &&
                Math.Abs(vertices[i * 3 + 1] - y) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMeshEdge(int[] faces, int faceCount, int a, int b)
    {
        for (int i = 0; i < faceCount; i++)
        {
            int i0 = faces[i * 3];
            int i1 = faces[i * 3 + 1];
            int i2 = faces[i * 3 + 2];
            if (HasEdge(i0, i1, a, b) || HasEdge(i1, i2, a, b) || HasEdge(i2, i0, a, b))
                return true;
        }

        return false;
    }

    private static bool HasTriangle(int[] faces, int faceCount, int a, int b, int c)
    {
        for (int i = 0; i < faceCount; i++)
        {
            int i0 = faces[i * 3];
            int i1 = faces[i * 3 + 1];
            int i2 = faces[i * 3 + 2];
            if (SameTriangle(i0, i1, i2, a, b, c))
                return true;
        }

        return false;
    }

    private static bool SameTriangle(int i0, int i1, int i2, int a, int b, int c)
    {
        return ContainsIndex(i0, i1, i2, a) &&
               ContainsIndex(i0, i1, i2, b) &&
               ContainsIndex(i0, i1, i2, c);
    }

    private static bool ContainsIndex(int i0, int i1, int i2, int value)
    {
        return i0 == value || i1 == value || i2 == value;
    }

    private static bool HasEdge(int start, int end, int a, int b)
    {
        return (start == a && end == b) || (start == b && end == a);
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
