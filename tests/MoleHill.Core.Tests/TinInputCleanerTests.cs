using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class TinInputCleanerTests
{
    [Fact]
    public void Clean_RemovesDegenerateAndDuplicateSegments()
    {
        var input = CreateMergedData(
            new[]
            {
                (0.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (1.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline)
            },
            new[] { 0, 1, 1, 0, 1, 1, 1, 2 });

        var cleaned = TinInputCleaner.Clean(input, 0.01);

        Assert.Equal(1, cleaned.DuplicateSegmentsRemoved);
        Assert.Equal(1, cleaned.DegenerateSegmentsRemoved);
        Assert.Equal(1, cleaned.CollinearVerticesCollapsed);
        Assert.Equal(1, cleaned.SegmentCount);
        Assert.Equal(2, cleaned.VertexCount);
    }

    [Fact]
    public void Clean_CollapsesCollinearBreaklineVertex_ButKeepsSpotVertex()
    {
        var input = CreateMergedData(
            new[]
            {
                (0.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (1.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline | PointCloudProcessor.VertexSource.Spot),
                (2.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline)
            },
            new[] { 0, 1, 1, 2 });

        var cleaned = TinInputCleaner.Clean(input, 0.01);

        Assert.Equal(1, cleaned.CollinearVerticesCollapsed);
        Assert.Equal(1, cleaned.SegmentCount);
        Assert.Equal(3, cleaned.VertexCount);
        Assert.Contains(cleaned.Sources, source => (source & PointCloudProcessor.VertexSource.Spot) != 0);
    }

    [Fact]
    public void Clean_SplitsCompatibleCrossingSegments()
    {
        var input = CreateMergedData(
            new[]
            {
                (0.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 2.0, 2.0, PointCloudProcessor.VertexSource.Breakline),
                (0.0, 2.0, 2.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline)
            },
            new[] { 0, 1, 2, 3 });

        var cleaned = TinInputCleaner.Clean(input, 0.01);

        Assert.Equal(1, cleaned.IntersectionsSplit);
        Assert.Equal(5, cleaned.VertexCount);
        Assert.Equal(4, cleaned.SegmentCount);
        Assert.Contains(Enumerable.Range(0, cleaned.VertexCount), i =>
            Math.Abs(cleaned.XyCoords[i * 2] - 1.0) < 1e-9 &&
            Math.Abs(cleaned.XyCoords[i * 2 + 1] - 1.0) < 1e-9 &&
            Math.Abs(cleaned.ZValues[i] - 1.0) < 1e-6);
    }

    [Fact]
    public void Clean_DoesNotSplitConflictingCrossingSegments()
    {
        var input = CreateMergedData(
            new[]
            {
                (0.0, 0.0, 0.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 2.0, 2.0, PointCloudProcessor.VertexSource.Breakline),
                (0.0, 2.0, 10.0, PointCloudProcessor.VertexSource.Breakline),
                (2.0, 0.0, 10.0, PointCloudProcessor.VertexSource.Breakline)
            },
            new[] { 0, 1, 2, 3 });

        var cleaned = TinInputCleaner.Clean(input, 0.01);

        Assert.Equal(0, cleaned.IntersectionsSplit);
        Assert.Equal(1, cleaned.IntersectionConflictsDetected);
        Assert.Equal(4, cleaned.VertexCount);
        Assert.Equal(2, cleaned.SegmentCount);
    }

    private static PointCloudProcessor.MergedData CreateMergedData(
        IReadOnlyList<(double x, double y, double z, PointCloudProcessor.VertexSource source)> vertices,
        int[] segments)
    {
        var xyCoords = new double[vertices.Count * 2];
        var zValues = new double[vertices.Count];
        var sources = new PointCloudProcessor.VertexSource[vertices.Count];

        for (int i = 0; i < vertices.Count; i++)
        {
            xyCoords[i * 2] = vertices[i].x;
            xyCoords[i * 2 + 1] = vertices[i].y;
            zValues[i] = vertices[i].z;
            sources[i] = vertices[i].source;
        }

        return new PointCloudProcessor.MergedData(
            xyCoords,
            zValues,
            vertices.Count,
            sources,
            segments,
            segments.Length / 2,
            0,
            0);
    }
}
