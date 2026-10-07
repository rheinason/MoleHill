using MoleHill.Core.IO;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class GeoTiffUnitTests
{
    [Theory]
    [InlineData(9001, 1.0)]
    [InlineData(9002, 0.3048)]
    [InlineData(9003, 0.3048006096012192)]
    public void TryReadLinearUnitFromGeoKeyDirectory_KnownEpsgUnit_ReturnsMetersPerUnit(
        ushort epsgUnit,
        double expected)
    {
        ushort[] directory = [1, 1, 0, 1, 3076, 0, 1, epsgUnit];

        bool read = GeoTiffLinearUnitReader.TryRead(directory, out var unit);

        Assert.True(read);
        Assert.Equal(expected, unit.MetersPerUnit, 12);
    }

    [Fact]
    public void TryReadLinearUnitFromGeoKeyDirectory_GeographicLinearUnit_IsIgnored()
    {
        ushort[] directory = [1, 1, 0, 1, 2052, 0, 1, 9001];

        bool read = GeoTiffLinearUnitReader.TryRead(directory, out _);

        Assert.False(read);
    }

    [Fact]
    public void ScaleCoordinates_UnitConversion_ScalesAffineAndOrigin()
    {
        var georeference = new RasterGeoreference(2, 3, 4, -5, 100, 200);

        RasterGeoreference scaled = georeference.ScaleCoordinates(1000);

        Assert.Equal(2000, scaled.XPixel);
        Assert.Equal(3000, scaled.XLine);
        Assert.Equal(4000, scaled.YPixel);
        Assert.Equal(-5000, scaled.YLine);
        Assert.Equal(100000, scaled.XUpperLeft);
        Assert.Equal(200000, scaled.YUpperLeft);
    }
}
