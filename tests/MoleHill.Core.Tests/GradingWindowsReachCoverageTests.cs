using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A rail whose reach covers the whole terrain makes one window of everything, so the windowed insertion hands
/// over to the whole-mesh one instead of paying for assignment, keys and stitching.
/// </summary>
public class GradingWindowsReachCoverageTests
{
    // A 100 x 50 site.
    private static readonly double[] Site = { 0, 0, 0, 100, 0, 0, 100, 50, 0, 0, 50, 0 };

    private static GradingWindows.Reach Line(double radius, params double[] xy) =>
        new(xy, xy.Length / 2, Closed: false, Filled: false, Radius: radius);

    [Fact]
    public void AnyReachCoversAll_ReachBeyondEveryCorner_ReturnsTrue()
    {
        // From the segment (40, 25)-(60, 25), the farthest corner is sqrt(40^2 + 25^2) ~ 47.2 away.
        Assert.True(GradingWindows.AnyReachCoversAll(Site, 4, new[] { Line(48, 40, 25, 60, 25) }));
    }

    [Fact]
    public void AnyReachCoversAll_ReachShortOfACorner_ReturnsFalse()
    {
        Assert.False(GradingWindows.AnyReachCoversAll(Site, 4, new[] { Line(47, 40, 25, 60, 25) }));
    }

    [Fact]
    public void AnyReachCoversAll_SeveralSmallReaches_ReturnsFalse()
    {
        // Together they span the site, but no single window would hold all of it.
        var reach = new[] { Line(30, 0, 25, 50, 25), Line(30, 50, 25, 100, 25) };

        Assert.False(GradingWindows.AnyReachCoversAll(Site, 4, reach));
    }
}
