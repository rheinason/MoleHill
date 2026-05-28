using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class SurfaceStripGraderTests
{
    [Fact]
    public void Grade_DegenerateTerrainFace_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 0.4,
                2.0, 1.0, 0.4,
                0.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.2,
            planeYCoeff: 0.0,
            planeConstant: 0.0);

        GradingResult? result = SurfaceStripGrader.Grade(
            BuildGridVertices(),
            15,
            new[] { 0, 1, 1 },
            1,
            surface,
            out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("degenerate", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_NonFiniteSurfaceBoundary_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, double.NaN,
                2.0, 1.0, 0.4,
                0.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.2,
            planeYCoeff: 0.0,
            planeConstant: 0.0);

        GradingResult? result = SurfaceStripGrader.Grade(
            BuildGridVertices(),
            15,
            BuildGridFaces(),
            16,
            surface,
            out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("finite", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_NonFiniteBarrierConstraint_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 0.4,
                2.0, 1.0, 0.4,
                0.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.2,
            planeYCoeff: 0.0,
            planeConstant: 0.0);
        var barrier = new SurfaceRemesher.ConstraintPolyline(
            new[] { 0.0, 0.5, 0.0, 2.0, double.PositiveInfinity, 0.0 },
            2,
            IsClosed: false,
            PreserveInputElevation: true);

        GradingResult? result = SurfaceStripGrader.Grade(
            BuildGridVertices(),
            15,
            BuildGridFaces(),
            16,
            surface,
            new[] { barrier },
            out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("finite", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_SurfaceFootprint_RegradesInteriorToPlane()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 0.4,
                2.0, 1.0, 0.4,
                0.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.2,
            planeYCoeff: 0.0,
            planeConstant: 0.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        var result = SurfaceStripGrader.Grade(
            BuildGridVertices(),
            15,
            BuildGridFaces(),
            16,
            surface,
            out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(EnumerateVertices(result!), vertex =>
            Math.Abs(vertex.x - 2.0) < 1e-6 &&
            Math.Abs(vertex.y - 0.5) < 1e-6 &&
            Math.Abs(vertex.z - 0.4) < 1e-6);
    }

    [Fact]
    public void Grade_InvalidBoundary_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 1.0, 0.0 },
            footprintVertexCount: 2,
            boundaryVertices: new[] { 0.0, 0.0, 0.0, 1.0, 0.0, 0.0 },
            boundaryVertexCount: 2,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 0.0);

        var result = SurfaceStripGrader.Grade(
            BuildGridVertices(),
            15,
            BuildGridFaces(),
            16,
            surface,
            out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("footprint", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_FootprintOutsideTerrainBoundary_ReturnsFailure()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 3.0, 0.0, 4.5, 0.0, 4.5, 1.0, 3.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                3.0, 0.0, 0.0,
                4.5, 0.0, 0.0,
                4.5, 1.0, 0.0,
                3.0, 1.0, 0.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 0.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        var result = SurfaceStripGrader.Grade(
            BuildGridVertices(),
            15,
            BuildGridFaces(),
            16,
            surface,
            out string? errorMessage);

        Assert.Null(result);
        Assert.Contains("terrain boundary", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grade_HardBarrier_BlocksGradingAcrossRetainingWall()
    {
        var surface = new SurfaceStripGrader.SurfaceDefinition(
            footprintXy: new[] { 0.0, 0.0, 1.2, 0.0, 1.2, 1.0, 0.0, 1.0 },
            footprintVertexCount: 4,
            boundaryVertices: new[]
            {
                0.0, 0.0, 1.0,
                1.2, 0.0, 1.0,
                1.2, 1.0, 1.0,
                0.0, 1.0, 1.0
            },
            boundaryVertexCount: 4,
            planeXCoeff: 0.0,
            planeYCoeff: 0.0,
            planeConstant: 1.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        var withoutBarrier = SurfaceStripGrader.Grade(
            BuildGridVertices(),
            15,
            BuildGridFaces(),
            16,
            surface,
            out string? withoutBarrierWarning);

        var barrier = new SurfaceRemesher.ConstraintPolyline(
            new[]
            {
                1.4, -1.0, 0.0,
                1.4,  2.0, 0.0
            },
            2,
            IsClosed: false,
            PreserveInputElevation: true);

        var withBarrier = SurfaceStripGrader.Grade(
            BuildGridVertices(),
            15,
            BuildGridFaces(),
            16,
            surface,
            new[] { barrier },
            out string? withBarrierWarning);

        Assert.NotNull(withoutBarrier);
        Assert.NotNull(withBarrier);
        Assert.True(string.IsNullOrWhiteSpace(withoutBarrierWarning) || !withoutBarrierWarning.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.True(string.IsNullOrWhiteSpace(withBarrierWarning) || !withBarrierWarning.Contains("failed", StringComparison.OrdinalIgnoreCase));

        double rightWithoutBarrier = FindVertexZ(withoutBarrier!, 2.0, 0.5);
        double rightWithBarrier = FindVertexZ(withBarrier!, 2.0, 0.5);
        double leftWithBarrier = FindVertexZ(withBarrier, -1.0, 0.5);

        Assert.True(rightWithoutBarrier > 0.2, $"Expected right-side grading without barrier, got {rightWithoutBarrier:G6}.");
        Assert.True(Math.Abs(rightWithBarrier) < 1e-6, $"Expected wall-blocked right side to remain unchanged, got {rightWithBarrier:G6}.");
        Assert.True(leftWithBarrier > 0.2, $"Expected unblocked left side to keep grading, got {leftWithBarrier:G6}.");
    }

    private static IEnumerable<(double x, double y, double z)> EnumerateVertices(GradingResult result)
    {
        for (int i = 0; i < result.VertexCount; i++)
            yield return (
                result.Vertices[i * 3],
                result.Vertices[i * 3 + 1],
                result.Vertices[i * 3 + 2]);
    }

    private static double FindVertexZ(GradingResult result, double x, double y)
    {
        foreach (var vertex in EnumerateVertices(result))
        {
            if (Math.Abs(vertex.x - x) <= 1e-6 && Math.Abs(vertex.y - y) <= 1e-6)
                return vertex.z;
        }

        throw new Xunit.Sdk.XunitException($"Could not find vertex at ({x}, {y}).");
    }

    private static double[] BuildGridVertices()
    {
        return new[]
        {
            -1.0, -1.0, 0.0,
             0.5, -1.0, 0.0,
             2.0, -1.0, 0.0,
             3.5, -1.0, 0.0,
            -1.0,  0.5, 0.0,
             0.5,  0.5, 0.0,
             2.0,  0.5, 0.0,
             3.5,  0.5, 0.0,
            -1.0,  2.0, 0.0,
             0.5,  2.0, 0.0,
             2.0,  2.0, 0.0,
             3.5,  2.0, 0.0,
            -1.0,  3.5, 0.0,
             0.5,  3.5, 0.0,
             2.0,  3.5, 0.0
        };
    }

    private static int[] BuildGridFaces()
    {
        return new[]
        {
            0, 1, 5,
            0, 5, 4,
            1, 2, 6,
            1, 6, 5,
            2, 3, 7,
            2, 7, 6,
            4, 5, 9,
            4, 9, 8,
            5, 6, 10,
            5, 10, 9,
            6, 7, 11,
            6, 11, 10,
            8, 9, 13,
            8, 13, 12,
            9, 10, 14,
            9, 14, 13
        };
    }
}
