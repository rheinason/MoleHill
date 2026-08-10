using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class PointCloudProcessorTests
{
    [Fact]
    public void Merge_SkipsSpotPointsWithInvalidZ_AndTracksElevationCount()
    {
        double[] spotXyz =
        {
            0.0, 0.0, 1.0,
            1.0, 0.0, double.NaN,
            0.0, 1.0, 2.0
        };

        var merged = PointCloudProcessor.Merge(
            spotXyz,
            spotCount: 3,
            new BreaklineDiscretizer.BreaklineData(Array.Empty<double>(), 0, Array.Empty<int>(), 0),
            tolerance: 1e-6);

        Assert.Equal(2, merged.VertexCount);
        Assert.Equal(0, merged.InvalidCoordinatesSkipped);
        Assert.Equal(1, merged.InvalidElevationsSkipped);
        Assert.Equal(1, merged.InvalidsSkipped);
        Assert.Equal("1 invalid points skipped due to invalid Z values", merged.DescribeInvalidPoints());
    }

    [Fact]
    public void Merge_SkipsBreaklineVerticesWithInvalidZ_AndDropsDependentSegments()
    {
        var breaklineData = new BreaklineDiscretizer.BreaklineData(
            new[]
            {
                0.0, 0.0, 10.0,
                1.0, 0.0, double.PositiveInfinity,
                2.0, 0.0, 12.0
            },
            vertexCount: 3,
            new[] { 0, 1, 1, 2 },
            segmentCount: 2);

        var merged = PointCloudProcessor.Merge(
            Array.Empty<double>(),
            spotCount: 0,
            breaklineData,
            tolerance: 1e-6);

        Assert.Equal(2, merged.VertexCount);
        Assert.Equal(0, merged.SegmentCount);
        Assert.Equal(0, merged.InvalidCoordinatesSkipped);
        Assert.Equal(1, merged.InvalidElevationsSkipped);
        Assert.All(merged.Sources, source => Assert.True((source & PointCloudProcessor.VertexSource.Breakline) != 0));
    }

    [Fact]
    public void Merge_SeparatesInvalidCoordinateAndElevationCounts()
    {
        double[] spotXyz =
        {
            0.0, 0.0, 1.0,
            double.NaN, 2.0, 3.0,
            5.0, 5.0, double.NegativeInfinity
        };

        var merged = PointCloudProcessor.Merge(
            spotXyz,
            spotCount: 3,
            new BreaklineDiscretizer.BreaklineData(Array.Empty<double>(), 0, Array.Empty<int>(), 0),
            tolerance: 1e-6);

        Assert.Equal(1, merged.VertexCount);
        Assert.Equal(1, merged.InvalidCoordinatesSkipped);
        Assert.Equal(1, merged.InvalidElevationsSkipped);
        Assert.Equal(2, merged.InvalidsSkipped);
        Assert.Equal(
            "2 invalid points skipped (1 with invalid XY, 1 with invalid Z)",
            merged.DescribeInvalidPoints());
    }

    [Fact]
    public void Merge_MultipleCandidatesInOneCell_PreservesFirstMatchOrder()
    {
        double[] spotXyz =
        {
            0.0, 0.0, 10.0,
            1.5, 0.0, 20.0,
            0.75, 0.0, 30.0
        };

        var merged = PointCloudProcessor.Merge(
            spotXyz,
            spotCount: 3,
            new BreaklineDiscretizer.BreaklineData(Array.Empty<double>(), 0, Array.Empty<int>(), 0),
            tolerance: 1.0);

        Assert.Equal(2, merged.VertexCount);
        Assert.Equal(1, merged.DuplicatesRemoved);
        Assert.Equal(new[] { 0.0, 0.0, 1.5, 0.0 }, merged.XyCoords);
        Assert.Equal(new[] { 10.0, 20.0 }, merged.ZValues);
    }

    [Fact]
    public void Merge_CoincidentBreaklineVerticesAtDifferentElevations_PreservesBoth()
    {
        var breaklineData = new BreaklineDiscretizer.BreaklineData(
            new[]
            {
                0.0, 0.0, 10.0,
                0.0, 0.0, 12.0,
                0.0, 0.0, 12.0
            },
            vertexCount: 3,
            new[] { 0, 1, 1, 2 },
            segmentCount: 2);

        var merged = PointCloudProcessor.Merge(
            Array.Empty<double>(),
            spotCount: 0,
            breaklineData,
            tolerance: 0.01);

        Assert.Equal(2, merged.VertexCount);
        Assert.Equal(1, merged.DuplicatesRemoved);
        Assert.Equal(new[] { 10.0, 12.0 }, merged.ZValues);
        Assert.Equal(new[] { 0, 1 }, merged.Segments);
        Assert.Equal(1, merged.SegmentCount);
    }
}
