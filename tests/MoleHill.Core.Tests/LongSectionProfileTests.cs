using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class LongSectionProfileTests
{
    [Fact]
    public void Frame_RoundTrips()
    {
        var frame = new LongSectionFrame(100.0, 200.0, Datum: 10.0, Exaggeration: 5.0);
        (double x, double y) = frame.ToDrawing(12.0, 13.0);
        Assert.Equal(112.0, x, 12);
        Assert.Equal(215.0, y, 12);
        (double station, double elevation) = frame.FromDrawing(x, y);
        Assert.Equal(12.0, station, 12);
        Assert.Equal(13.0, elevation, 12);
    }

    [Theory]
    [InlineData(0.0, 10.0)]
    [InlineData(5.0, 11.0)]
    [InlineData(10.0, 12.0)]
    [InlineData(20.0, 11.5)]
    public void ElevationAt_InterpolatesLinearly(double station, double expected)
    {
        double[] s = { 0, 10, 30 };
        double[] z = { 10, 12, 11 };
        Assert.Equal(expected, LongSectionProfile.ElevationAt(s, z, station), 12);
    }

    [Fact]
    public void ElevationAt_OutsideTheProfile_IsNaN()
    {
        Assert.True(double.IsNaN(LongSectionProfile.ElevationAt(new[] { 0.0, 10.0 }, new[] { 1.0, 2.0 }, 11.0)));
    }

    [Fact]
    public void SuggestDatum_IsARoundNumberBelowTheLowestPoint()
    {
        Assert.Equal(40.0, LongSectionProfile.SuggestDatum(47.3, 5.0), 12);
    }
}
