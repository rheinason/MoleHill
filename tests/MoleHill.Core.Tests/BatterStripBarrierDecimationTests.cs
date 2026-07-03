using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Covers <see cref="BatterStripBuilder.BuildDecimatedCarveXy"/>, which collapses a barrier-clamped
/// batter run back onto the wall (lock-curve) breakline so the carve loop does not re-tessellate the
/// retaining-wall face into thin slivers.
/// </summary>
public class BatterStripBarrierDecimationTests
{
    private static BatterStripBuilder.DaylightStation Daylighted(double x, double y) =>
        new(x, y, 0.0, x, y, 0.0, 1.0, BatterStripBuilder.DaylightStatus.Daylighted);

    private static BatterStripBuilder.DaylightStation Clamped(double dayX, double dayY, int barrierSegmentIndex) =>
        new(dayX, dayY - 1.0, 0.0, dayX, dayY, 0.0, 1.0, BatterStripBuilder.DaylightStatus.ClampedToBarrier)
        {
            BarrierSegmentIndex = barrierSegmentIndex
        };

    [Fact]
    public void BuildDecimatedCarveXy_ClampedRunOnBentWall_CollapsesToBreaklineVertices()
    {
        // Wall breakline: an L-shaped lock curve (0,0)->(10,0)->(20,10): barrier segment 0 then 1.
        var lockCurves = new[]
        {
            new PadGrader.LockCurve(new[] { 0.0, 0.0, 10.0, 0.0, 20.0, 10.0 }, 3)
        };
        PreparedBarriers barriers = GradingBarriers.BuildFromLockCurves(lockCurves);

        // A daylighted approach, a dense clamped run riding the wall across the (10,0) bend, then a
        // daylighted departure. The clamped run has FIVE points but the wall only bends once.
        var stations = new[]
        {
            Daylighted(-2.0, 5.0),
            Clamped(2.0, 0.0, 0),
            Clamped(5.0, 0.0, 0),
            Clamped(8.0, 0.0, 0),
            Clamped(14.0, 4.0, 1),
            Clamped(17.0, 7.0, 1),
            Daylighted(24.0, 14.0),
        };
        var loop = new BatterStripBuilder.DaylightLoop { Stations = stations, IsClosed = true };

        double[] carve = BatterStripBuilder.BuildDecimatedCarveXy(loop, barriers, 1e-6);

        var pts = new System.Collections.Generic.List<(double X, double Y)>();
        for (int i = 0; i < carve.Length; i += 2)
            pts.Add((carve[i], carve[i + 1]));

        // The two daylighted points are kept; the clamped run collapses to its run endpoints plus the
        // single wall vertex (10,0) at the bend — NOT the five dense ring points.
        Assert.Contains((-2.0, 5.0), pts);
        Assert.Contains((24.0, 14.0), pts);
        Assert.Contains((10.0, 0.0), pts);          // breakline bend vertex inserted
        Assert.DoesNotContain((5.0, 0.0), pts);     // interior same-segment ring point dropped
        Assert.DoesNotContain((14.0, 4.0), pts);    // interior same-segment ring point dropped

        // run-entry (2,0) and run-exit (17,7) bound the wall run; everything between collapses.
        Assert.Contains((2.0, 0.0), pts);
        Assert.Contains((17.0, 7.0), pts);
        Assert.True(pts.Count <= 6, $"Expected the wall run to collapse, got {pts.Count} points.");
    }

    [Fact]
    public void BuildDecimatedCarveXy_NoBarrierClamping_ReturnsLoopUnchanged()
    {
        PreparedBarriers barriers = GradingBarriers.BuildFromLockCurves(
            new[] { new PadGrader.LockCurve(new[] { 100.0, 100.0, 110.0, 100.0 }, 2) });

        var stations = new[]
        {
            Daylighted(0.0, 0.0),
            Daylighted(5.0, 0.0),
            Daylighted(5.0, 5.0),
            Daylighted(0.0, 5.0),
        };
        var loop = new BatterStripBuilder.DaylightLoop { Stations = stations, IsClosed = true };

        double[] carve = BatterStripBuilder.BuildDecimatedCarveXy(loop, barriers, 1e-6);

        Assert.Equal(loop.DaylightXy(), carve);
    }
}
