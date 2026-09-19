using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The debounce exists to coalesce a gesture, not to slow down an isolated edit. The original flat
/// 500 ms trailing delay did the latter, which put a floor under edit-to-visible that no amount of
/// build optimization could lift. These tests pin the leading-edge rule that replaced it.
/// </summary>
public sealed class TerrainDebouncePolicyTests
{
    [Fact]
    public void ResolveDelayMs_FirstEditAfterAQuietPeriod_DispatchesImmediately()
    {
        // The case that matters for feeling realtime: nothing has dispatched recently, so nothing waits.
        Assert.Equal(0, TerrainDebouncePolicy.ResolveDelayMs(TimeSpan.FromMilliseconds(3), null));
    }

    [Fact]
    public void ResolveDelayMs_EditLongAfterTheLastDispatch_DispatchesImmediately()
    {
        Assert.Equal(0, TerrainDebouncePolicy.ResolveDelayMs(
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ResolveDelayMs_SecondEditWithinTheInterval_WaitsOutTheRemainder()
    {
        // A 400 ms build throttles to one dispatch per 400 ms; 120 ms in, 280 ms remain.
        Assert.Equal(280, TerrainDebouncePolicy.ResolveDelayMs(
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(120)));
    }

    [Fact]
    public void ResolveDelayMs_FastTerrainMidDrag_WaitsOnlyTheFloor()
    {
        // A 3 ms rebuild is free to keep up with the pointer, bounded by a drag's event spacing.
        Assert.Equal(
            TerrainDebouncePolicy.MinIntervalMs,
            TerrainDebouncePolicy.ResolveDelayMs(TimeSpan.FromMilliseconds(3), TimeSpan.Zero));
    }

    [Fact]
    public void ResolveDelayMs_SlowTerrainMidDrag_IsCappedAtTheHistoricalConstant()
    {
        // A slow terrain keeps exactly its old protection against discarded work - no more, no less.
        Assert.Equal(
            TerrainDebouncePolicy.MaxIntervalMs,
            TerrainDebouncePolicy.ResolveDelayMs(TimeSpan.FromSeconds(9), TimeSpan.Zero));
    }

    [Fact]
    public void ResolveDelayMs_NeverExceedsTheOldFlatDelay()
    {
        double[] durations = { 0, 3, 61, 250, 499, 501, 5_000, 120_000 };
        double[] sinceDispatch = { 0, 1, 30, 59, 60, 250, 499 };
        foreach (double duration in durations)
        foreach (double since in sinceDispatch)
        {
            int delay = TerrainDebouncePolicy.ResolveDelayMs(
                TimeSpan.FromMilliseconds(duration),
                TimeSpan.FromMilliseconds(since));
            Assert.InRange(delay, 0, TerrainDebouncePolicy.MaxIntervalMs);
        }
    }

    [Fact]
    public void ResolveIntervalMs_NoPriorBuild_FallsBackToTheHistoricalConstant()
    {
        Assert.Equal(TerrainDebouncePolicy.MaxIntervalMs, TerrainDebouncePolicy.ResolveIntervalMs(null));
    }

    [Fact]
    public void ResolveIntervalMs_TracksTheBuildCostBetweenTheBounds()
    {
        Assert.Equal(117, TerrainDebouncePolicy.ResolveIntervalMs(TimeSpan.FromMilliseconds(117)));
        Assert.Equal(TerrainDebouncePolicy.MinIntervalMs, TerrainDebouncePolicy.ResolveIntervalMs(TimeSpan.FromMilliseconds(3)));
        Assert.Equal(TerrainDebouncePolicy.MaxIntervalMs, TerrainDebouncePolicy.ResolveIntervalMs(TimeSpan.FromSeconds(9)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ResolveIntervalMs_DegenerateDuration_StillCoalescesADrag(double milliseconds)
    {
        Assert.Equal(
            TerrainDebouncePolicy.MinIntervalMs,
            TerrainDebouncePolicy.ResolveIntervalMs(TimeSpan.FromMilliseconds(milliseconds)));
    }
}
