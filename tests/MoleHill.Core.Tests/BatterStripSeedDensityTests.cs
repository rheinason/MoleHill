using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Covers <see cref="BatterStripBuilder.BuildBatterSeeds"/>: batter interior seeds are spaced per
/// station from that station's OWN reach, so a shallow cross-section is not packed with the deepest
/// section's row count (the cause of the tight parallel banding in Grade Path corridors).
/// </summary>
public class BatterStripSeedDensityTests
{
    private static BatterStripBuilder.DaylightStation Station(
        double fx, double fy, double fz, double dx, double dy, double dz, double reach) =>
        new(fx, fy, fz, dx, dy, dz, reach, BatterStripBuilder.DaylightStatus.Daylighted);

    [Fact]
    public void BuildBatterSeeds_PerStationReach_DoesNotBandShallowStations()
    {
        // One deep station (reach 10) and one shallow (reach 1), edge length 1.
        var stations = new[]
        {
            Station(0, 0, 10, 10, 0, 0, 10.0),  // deep
            Station(0, 5, 10, 1, 5, 9, 1.0),    // shallow
        };
        var loop = new BatterStripBuilder.DaylightLoop { Stations = stations, IsClosed = false };

        double[] seeds = BatterStripBuilder.BuildBatterSeeds(loop, edgeLength: 1.0);

        int deep = 0, shallow = 0;
        for (int i = 0; i < seeds.Length / 3; i++)
        {
            double y = seeds[i * 3 + 1];
            if (y < 2.5) deep++; else shallow++;
        }

        // Deep station: ceil(10/1)=10 rows -> 9 interior seeds. Shallow: reach == edgeLength -> 1 row ->
        // NO interior seeds (the old global-maxReach grid would have crammed ~9 rows into reach 1).
        Assert.Equal(9, deep);
        Assert.Equal(0, shallow);
    }

    [Fact]
    public void BuildBatterSeeds_FlatStation_ContributesNoSeeds()
    {
        var stations = new[]
        {
            new BatterStripBuilder.DaylightStation(0, 0, 5, 0, 0, 5, 0.0, BatterStripBuilder.DaylightStatus.Flat),
            Station(0, 1, 5, 4, 1, 0, 4.0),
        };
        var loop = new BatterStripBuilder.DaylightLoop { Stations = stations, IsClosed = false };

        double[] seeds = BatterStripBuilder.BuildBatterSeeds(loop, edgeLength: 1.0);

        // Only the reach-4 station contributes (ceil(4/1)=4 rows -> 3 interior seeds).
        Assert.Equal(3, seeds.Length / 3);
    }
}
