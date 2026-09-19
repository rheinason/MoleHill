using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The debounce used to be a flat 500 ms, which was most of the edit-to-visible wait on any terrain
/// that rebuilds quickly. These tests pin the two properties that make the adaptive rule safe: it never
/// waits longer than the old constant, and it never drops below a slider drag's event spacing.
/// </summary>
public sealed class TerrainDebouncePolicyTests
{
    [Fact]
    public void ResolveFinalDebounceMs_NoPriorBuild_FallsBackToTheHistoricalConstant()
    {
        Assert.Equal(
            TerrainDebouncePolicy.MaxFinalDebounceMs,
            TerrainDebouncePolicy.ResolveFinalDebounceMs(null));
    }

    [Fact]
    public void ResolveFinalDebounceMs_FastCachedRebuild_CollapsesToTheFloor()
    {
        // The measured cached rebuild of the trailer-ramp fixture: 3 ms of real work behind 530 ms of wait.
        Assert.Equal(
            TerrainDebouncePolicy.MinFinalDebounceMs,
            TerrainDebouncePolicy.ResolveFinalDebounceMs(TimeSpan.FromMilliseconds(3)));
    }

    [Fact]
    public void ResolveFinalDebounceMs_ModerateBuild_TracksTheBuildCost()
    {
        Assert.Equal(117, TerrainDebouncePolicy.ResolveFinalDebounceMs(TimeSpan.FromMilliseconds(117)));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2_000)]
    [InlineData(120_000)]
    public void ResolveFinalDebounceMs_SlowBuild_NeverWaitsLongerThanTheOldConstant(double milliseconds)
    {
        // A slow terrain must keep exactly today's protection against discarded work - no more, no less.
        Assert.Equal(
            TerrainDebouncePolicy.MaxFinalDebounceMs,
            TerrainDebouncePolicy.ResolveFinalDebounceMs(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ResolveFinalDebounceMs_DegenerateDuration_StillCoalescesADrag(double milliseconds)
    {
        Assert.Equal(
            TerrainDebouncePolicy.MinFinalDebounceMs,
            TerrainDebouncePolicy.ResolveFinalDebounceMs(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void ResolveFinalDebounceMs_AlwaysWithinBounds()
    {
        double[] durations = { 0, 1, 59, 60, 61, 250, 499, 501, 5_000 };
        foreach (double milliseconds in durations)
        {
            int resolved = TerrainDebouncePolicy.ResolveFinalDebounceMs(TimeSpan.FromMilliseconds(milliseconds));
            Assert.InRange(resolved, TerrainDebouncePolicy.MinFinalDebounceMs, TerrainDebouncePolicy.MaxFinalDebounceMs);
        }
    }
}
