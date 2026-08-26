using MoleHill.Core.Analysis;
using System.Diagnostics;
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

    [Fact]
    public void Trace_CancellationRequested_StopsBeforeStartLookup()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 0, 0, 10, 0 };
        int[] faces = { 0, 1, 2 };

        Assert.Throws<OperationCanceledException>(() => WaterflowTracer.Trace(
            vertices, 3, faces, 1,
            new[] { 1.0, 1.0 }, 1,
            new WaterflowTracer.Options { CancellationRequested = () => true }));
    }

    [Fact]
    public void Trace_ManyStartsOnLargeGrid_CompletesWithIndexedLookup()
    {
        const int cells = 100;
        int vertexWidth = cells + 1;
        var vertices = new double[vertexWidth * vertexWidth * 3];
        for (int y = 0; y < vertexWidth; y++)
        {
            for (int x = 0; x < vertexWidth; x++)
            {
                int offset = ((y * vertexWidth) + x) * 3;
                vertices[offset] = x;
                vertices[offset + 1] = y;
                vertices[offset + 2] = 0.0;
            }
        }

        var faces = new int[cells * cells * 6];
        int faceOffset = 0;
        for (int y = 0; y < cells; y++)
        {
            for (int x = 0; x < cells; x++)
            {
                int a = (y * vertexWidth) + x;
                int b = a + 1;
                int d = a + vertexWidth;
                int c = d + 1;
                faces[faceOffset++] = a; faces[faceOffset++] = b; faces[faceOffset++] = c;
                faces[faceOffset++] = a; faces[faceOffset++] = c; faces[faceOffset++] = d;
            }
        }

        const int startCount = 500;
        var starts = new double[startCount * 2];
        for (int index = 0; index < startCount; index++)
        {
            starts[index * 2] = (index % cells) + 0.25;
            starts[index * 2 + 1] = ((index * 37) % cells) + 0.25;
        }

        var timer = Stopwatch.StartNew();
        WaterflowTracer.Result result = WaterflowTracer.Trace(
            vertices,
            vertexWidth * vertexWidth,
            faces,
            cells * cells * 2,
            starts,
            startCount);
        timer.Stop();

        Assert.Equal(startCount, result.Paths.Count);
        Assert.Equal(0, result.RejectedStartCount);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Indexed trace took {timer.Elapsed}.");
    }
}
