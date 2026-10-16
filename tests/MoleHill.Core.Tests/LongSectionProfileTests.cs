using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class LongSectionProfileTests
{
    private static readonly LongSectionFrame Frame = new(100.0, 200.0, Datum: 10.0, Exaggeration: 5.0);

    [Fact]
    public void Frame_RoundTrips()
    {
        (double x, double y) = Frame.ToDrawing(12.0, 13.0);
        Assert.Equal(112.0, x, 12);
        Assert.Equal(215.0, y, 12);
        (double station, double elevation) = Frame.FromDrawing(x, y);
        Assert.Equal(12.0, station, 12);
        Assert.Equal(13.0, elevation, 12);
    }

    [Fact]
    public void TryRead_ForwardProfile_ReadsStationsAndElevations()
    {
        double[] drawing = { 100, 200, 110, 210, 130, 205 };

        Assert.True(LongSectionProfile.TryRead(drawing, Frame, 1e-6, out double[] s, out double[] z, out string? error), error);
        Assert.Equal(new[] { 0.0, 10.0, 30.0 }, s);
        Assert.Equal(10.0, z[0], 12);
        Assert.Equal(12.0, z[1], 12);
        Assert.Equal(11.0, z[2], 12);
    }

    [Fact]
    public void TryRead_ReversedProfile_IsReadForwards()
    {
        double[] drawing = { 130, 205, 110, 210, 100, 200 };

        Assert.True(LongSectionProfile.TryRead(drawing, Frame, 1e-6, out double[] s, out double[] z, out _));
        Assert.Equal(new[] { 0.0, 10.0, 30.0 }, s);
        Assert.Equal(11.0, z[2], 12);
    }

    [Fact]
    public void TryRead_DoublingBack_IsRefused()
    {
        double[] drawing = { 100, 200, 120, 210, 115, 205, 130, 205 };

        Assert.False(LongSectionProfile.TryRead(drawing, Frame, 1e-6, out _, out _, out string? error));
        Assert.Contains("doubles back", error);
    }

    [Fact]
    public void TryRead_VerticalStep_IsRefused()
    {
        double[] drawing = { 100, 200, 120, 210, 120, 220, 130, 205 };

        Assert.False(LongSectionProfile.TryRead(drawing, Frame, 1e-6, out _, out _, out string? error));
        Assert.Contains("vertically", error);
    }

    [Fact]
    public void TryRead_DuplicatePoint_IsMerged()
    {
        double[] drawing = { 100, 200, 120, 210, 120, 210, 130, 205 };

        Assert.True(LongSectionProfile.TryRead(drawing, Frame, 1e-6, out double[] s, out _, out _));
        Assert.Equal(3, s.Length);
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

    [Fact]
    public void Apply_KeepsPlanAndAddsProfileBreaks()
    {
        // An L-shaped path: 10 along X, then 10 along Y, all at Z = 0.
        double[] path = { 0, 0, 0, 10, 0, 0, 10, 10, 0 };
        double[] stations = { 0, 5, 20 };
        double[] elevations = { 1, 3, 1 };

        double[] result = LongSectionProfile.Apply(path, stations, elevations, 1e-9, out int uncovered);

        Assert.Equal(0, uncovered);
        Assert.Equal(4, result.Length / 3);
        Assert.Equal(new[] { 5.0, 0.0, 3.0 }, result[3..6]);
        // The corner at station 10 keeps its plan position and reads 3 - 2 * 5 / 15.
        Assert.Equal(10.0, result[6], 12);
        Assert.Equal(0.0, result[7], 12);
        Assert.Equal(3.0 - 2.0 * 5.0 / 15.0, result[8], 12);
    }

    [Fact]
    public void Apply_ProfileShorterThanPath_KeepsPathElevationBeyondIt()
    {
        double[] path = { 0, 0, 7, 10, 0, 7, 20, 0, 7 };

        double[] result = LongSectionProfile.Apply(path, new[] { 0.0, 10.0 }, new[] { 1.0, 2.0 }, 1e-9, out int uncovered);

        Assert.Equal(1, uncovered);
        Assert.Equal(7.0, result[^1], 12);
        Assert.Equal(2.0, result[5], 12);
    }
}
