using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class WaterflowTracerTests
{
    [Fact]
    public void Trace_PlanarDownhillSurface_ReachesBoundaryAndPreservesTerrainElevation()
    {
        // A 10 x 10 square split into two triangles, z = 10 - x - y.
        double[] vertices =
        {
            0, 0, 10,
            10, 0, 0,
            10, 10, -10,
            0, 10, 0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        WaterflowTracer.Result result = WaterflowTracer.Trace(
            vertices, 4, faces, 2,
            new[] { 2.0, 2.0 }, 1);

        WaterflowTracer.Path path = Assert.Single(result.Paths);
        Assert.True(path.ReachedBoundary);
        Assert.False(path.TerminatedAtSink);
        Assert.Equal(0, result.RejectedStartCount);
        Assert.True(path.PointCount >= 2);

        for (int i = 0; i < path.PointCount; i++)
        {
            double x = path.PointsXyz[i * 3];
            double y = path.PointsXyz[(i * 3) + 1];
            double z = path.PointsXyz[(i * 3) + 2];
            Assert.Equal(10.0 - x - y, z, 8);
        }

        double endX = path.PointsXyz[^3];
        double endY = path.PointsXyz[^2];
        Assert.True(endX >= 9.999999 || endY >= 9.999999);
    }

    [Fact]
    public void Trace_FlatFace_StopsAtSink()
    {
        double[] vertices = { 0, 0, 3, 10, 0, 3, 0, 10, 3 };
        int[] faces = { 0, 1, 2 };

        WaterflowTracer.Result result = WaterflowTracer.Trace(
            vertices, 3, faces, 1,
            new[] { 2.0, 2.0 }, 1);

        WaterflowTracer.Path path = Assert.Single(result.Paths);
        Assert.True(path.TerminatedAtSink);
        Assert.False(path.ReachedBoundary);
        Assert.Equal(1, path.PointCount);
        Assert.Equal(3.0, path.PointsXyz[2], 8);
    }

    [Fact]
    public void Trace_OutOfBoundsStarts_AreRejectedWithoutOutput()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 0, 0, 10, 0 };
        int[] faces = { 0, 1, 2 };

        WaterflowTracer.Result result = WaterflowTracer.Trace(
            vertices, 3, faces, 1,
            new[] { 20.0, 20.0, 1.0, 1.0 }, 2);

        WaterflowTracer.Path path = Assert.Single(result.Paths);
        Assert.Equal(1, result.RejectedStartCount);
        Assert.Equal(1, path.PointCount);
    }
}
