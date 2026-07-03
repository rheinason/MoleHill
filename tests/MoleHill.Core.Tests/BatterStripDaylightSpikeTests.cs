using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Covers <see cref="BatterStripBuilder.RegularizeDaylightSpikes"/>, the median despike that tames the
/// grazing-daylight instability (a cut batter reaching far into rising terrain while its neighbours stop
/// short, producing a jagged daylight line and sliver spikes).
/// </summary>
public class BatterStripDaylightSpikeTests
{
    // A large flat terrain at z = 0, so InterpolateZ at the clamped daylight point is well-defined.
    private static TerrainFaceGrid FlatTerrain() =>
        new(
            new double[] { -100, -100, 0, 100, -100, 0, 100, 100, 0, -100, 100, 0 },
            4,
            new[] { 0, 1, 2, 0, 2, 3 },
            2);

    private static BatterStripBuilder.DaylightStation DaylitStation(double fx, double reach) =>
        // foot at (fx,0,5), outward +Y, daylight point at (fx, reach, 0)
        new(fx, 0, 5, fx, reach, 0, reach, BatterStripBuilder.DaylightStatus.Daylighted);

    [Fact]
    public void RegularizeDaylightSpikes_SingleSpike_ClampedToNeighbourMedian()
    {
        var stations = new[]
        {
            DaylitStation(0, 2.0),
            DaylitStation(1, 10.0), // spike: 5x its neighbours
            DaylitStation(2, 2.0),
        };

        BatterStripBuilder.RegularizeDaylightSpikes(stations, isClosed: false, FlatTerrain());

        Assert.Equal(2.0, stations[1].Reach, 3);   // clamped down to the median
        Assert.Equal(2.0, stations[1].DayY, 3);     // daylight point pulled in along its normal
        Assert.Equal(0.0, stations[1].DayZ, 3);     // re-read on terrain
    }

    [Fact]
    public void RegularizeDaylightSpikes_SmoothRamp_Unchanged()
    {
        var stations = new[]
        {
            DaylitStation(0, 2.0),
            DaylitStation(1, 3.0),
            DaylitStation(2, 4.0),
        };

        BatterStripBuilder.RegularizeDaylightSpikes(stations, isClosed: false, FlatTerrain());

        Assert.Equal(3.0, stations[1].Reach, 3); // a monotone ramp is its own median — untouched
    }

    [Fact]
    public void RegularizeDaylightSpikes_NeverLengthensADip()
    {
        var stations = new[]
        {
            DaylitStation(0, 8.0),
            DaylitStation(1, 2.0), // a dip — must NOT be pushed out past where it met ground
            DaylitStation(2, 8.0),
        };

        BatterStripBuilder.RegularizeDaylightSpikes(stations, isClosed: false, FlatTerrain());

        Assert.Equal(2.0, stations[1].Reach, 3); // conservative: only clamps spikes down
    }
}
