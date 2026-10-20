using MoleHill.Core.Geometry;
using MoleHill.Core.Scan;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ScanOutlineHygieneTests
{
    [Fact]
    public void Find_CrossingAndFoldedChains_AreReportedButJunctionsAreNot()
    {
        double[] diagonal = { 0, 0, 0, 10, 10, 0 };
        double[] across = { 0, 10, 0, 10, 0, 0 };
        double[] touching = { 10, 10, 0, 20, 10, 0 };   // shares an end with the diagonal
        double[] bowTie = { 30, 0, 0, 40, 10, 0, 40, 0, 0, 30, 10, 0 };
        double[] folded = { 50, 0, 0, 60, 0, 0, 50, .01, 0 };
        var found = PolylineCrossings.Find(new[] { (diagonal, false), (across, false), (touching, false), (bowTie, true), (folded, false) });
        Assert.Equal(new HashSet<int> { 0, 1, 3, 4 }, found);
    }

    [Fact]
    public void Find_LongSegmentsAmongShortOnes_StillCross()
    {
        var chains = new List<(double[], bool)> { (new double[] { -500, -500, 0, 500, 500, 0 }, false) };
        for (int i = 0; i < 200; i++) chains.Add((new double[] { i * 5, i * 5 + 1, 0, i * 5 + .5, i * 5 + 1, 0 }, false));
        chains.Add((new double[] { 100, 101, 0, 101, 99, 0 }, false));
        Assert.Equal(new HashSet<int> { 0, 201 }, PolylineCrossings.Find(chains));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Regions_GardensJoinedByANarrowStrip_AreSeparateAreas(bool diagonal)
    {
        // Two 40-cell gardens joined by a strip 6 cells wide; a 20-cell neck width splits them at the strip.
        const int size = 160;
        var soft = new bool[size * size];
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                double u = column, v = row;
                if (diagonal) { u = (column + row) / Math.Sqrt(2); v = (row - column) / Math.Sqrt(2) + 80; }
                bool garden = Math.Abs(v - 80) < 20 && (Math.Abs(u - 40) < 20 || Math.Abs(u - 120) < 20);
                bool strip = Math.Abs(v - 80) < 3 && u >= 40 && u <= 120;
                soft[row * size + column] = garden || strip;
            }
        int[] regions = ScanSoftNecks.Regions(soft, size, size, 10, 100, CancellationToken.None);
        Assert.All(Enumerable.Range(0, soft.Length), node => Assert.Equal(soft[node], regions[node] >= 0));
        var areas = regions.Where(r => r >= 0).GroupBy(r => r).Select(g => g.Count()).OrderBy(c => c).ToArray();
        Assert.Equal(2, areas.Length);
        Assert.InRange(areas[0], areas[1] * .9, areas[1]);

        // A strip wider than the neck keeps the gardens one area.
        int[] whole = ScanSoftNecks.Regions(soft, size, size, 2, 100, CancellationToken.None);
        Assert.Single(whole.Where(r => r >= 0).Distinct());
    }
}
