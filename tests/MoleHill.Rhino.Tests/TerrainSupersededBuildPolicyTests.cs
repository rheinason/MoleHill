using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainSupersededBuildPolicyTests
{
    [Fact]
    public void ShouldCancelRunningBuild_UnmeasuredTerrain_Cancels()
    {
        // The first build of a session is the one most likely to be slow, and letting an unbounded
        // build finish would delay the newest edit by an unbounded amount.
        Assert.True(TerrainSupersededBuildPolicy.ShouldCancelRunningBuild(null));
    }

    [Fact]
    public void ShouldCancelRunningBuild_SmallTerrain_LetsItFinish()
    {
        // 16 ms is the measured warm rail edit on the 2,694-face fixture.
        Assert.False(TerrainSupersededBuildPolicy.ShouldCancelRunningBuild(TimeSpan.FromMilliseconds(16)));
    }

    [Fact]
    public void ShouldCancelRunningBuild_MediumTerrain_LetsItFinish()
    {
        // 81 ms on 25k faces: over the 66 ms input-to-visible target, but well inside the cadence a
        // gesture is already rate-limited to, so a frame every 81 ms beats no frames at all.
        Assert.False(TerrainSupersededBuildPolicy.ShouldCancelRunningBuild(TimeSpan.FromMilliseconds(81)));
    }

    [Fact]
    public void ShouldCancelRunningBuild_AtTheCadenceCap_LetsItFinish()
    {
        Assert.False(TerrainSupersededBuildPolicy.ShouldCancelRunningBuild(
            TimeSpan.FromMilliseconds(TerrainSupersededBuildPolicy.MaxFinishableMs)));
    }

    [Fact]
    public void ShouldCancelRunningBuild_BeyondTheCadenceCap_Cancels()
    {
        // A build slower than the worst-case dispatch cadence falls further behind the pointer with
        // every sample, which is the regime cancelling exists for.
        Assert.True(TerrainSupersededBuildPolicy.ShouldCancelRunningBuild(
            TimeSpan.FromMilliseconds(TerrainSupersededBuildPolicy.MaxFinishableMs + 1)));
    }

    [Fact]
    public void ShouldCancelRunningBuild_GeometryHeavyTerrain_Cancels()
    {
        // ~3 s: the measured geometry-heavy stack.
        Assert.True(TerrainSupersededBuildPolicy.ShouldCancelRunningBuild(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ShouldPublishSupersededResult_FreshFrameAheadOfTheScreen_Publishes()
    {
        Assert.True(TerrainSupersededBuildPolicy.ShouldPublishSupersededResult(
            hasMesh: true,
            buildElapsed: TimeSpan.FromMilliseconds(16),
            resultVersion: 7,
            displayedGeometryRevision: 6));
    }

    [Fact]
    public void ShouldPublishSupersededResult_NoMesh_Declines()
    {
        Assert.False(TerrainSupersededBuildPolicy.ShouldPublishSupersededResult(
            hasMesh: false,
            buildElapsed: TimeSpan.FromMilliseconds(16),
            resultVersion: 7,
            displayedGeometryRevision: 6));
    }

    [Theory]
    [InlineData(7)]  // the same frame is already shown
    [InlineData(8)]  // a newer frame is already shown
    public void ShouldPublishSupersededResult_NotAheadOfTheScreen_Declines(long displayedGeometryRevision)
    {
        // Monotonic: a slower build finishing after a faster one must not walk the terrain backwards,
        // and re-publishing the frame already shown is pure cost.
        Assert.False(TerrainSupersededBuildPolicy.ShouldPublishSupersededResult(
            hasMesh: true,
            buildElapsed: TimeSpan.FromMilliseconds(16),
            resultVersion: 7,
            displayedGeometryRevision));
    }

    [Fact]
    public void ShouldPublishSupersededResult_TooOldToBeUseful_Declines()
    {
        // A build that began before its duration was ever measured can outrun the cancel policy; the
        // age limit is what stops a frame predating several samples from reaching the screen.
        Assert.False(TerrainSupersededBuildPolicy.ShouldPublishSupersededResult(
            hasMesh: true,
            buildElapsed: TimeSpan.FromMilliseconds(TerrainSupersededBuildPolicy.MaxFinishableMs + 1),
            resultVersion: 7,
            displayedGeometryRevision: 6));
    }

    [Fact]
    public void MaxFinishable_MatchesTheGestureDispatchCadence()
    {
        // The two constants are the same fact: a build inside the worst-case dispatch interval costs a
        // gesture nothing it was not already spending. If the debounce cap moves, this moves with it.
        Assert.Equal(TerrainDebouncePolicy.MaxIntervalMs, TerrainSupersededBuildPolicy.MaxFinishableMs);
    }
}
