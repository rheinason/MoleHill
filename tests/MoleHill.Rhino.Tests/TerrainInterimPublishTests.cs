using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Showing the terrain before its dependent outputs settle is only safe while stale outputs are
/// *visibly* stale. These tests hold both halves: the policy that decides when it is worth doing, and
/// the freshness state that stops a partial publication being mistaken for a finished build.
/// </summary>
public sealed class TerrainInterimPublishTests
{
    [Fact]
    public void ShouldPublishGeometryEarly_SlowOutputs_PublishesEarly()
    {
        // The measured heavy fixture: 5.1 s of analyses behind a mesh finished at 698 ms.
        Assert.True(TerrainInterimPublishPolicy.ShouldPublishGeometryEarly(
            TerrainBuildMode.Final,
            TimeSpan.FromMilliseconds(5_144),
            hasPreviousDisplayState: true));
    }

    [Fact]
    public void ShouldPublishGeometryEarly_TrivialOutputs_DoesNot()
    {
        // The measured small fixture: 0.5 ms of outputs, where a copy and an extra redraw are pure loss.
        Assert.False(TerrainInterimPublishPolicy.ShouldPublishGeometryEarly(
            TerrainBuildMode.Final,
            TimeSpan.FromMilliseconds(0.5),
            hasPreviousDisplayState: true));
    }

    [Fact]
    public void ShouldPublishGeometryEarly_CachedOutputsAfterASlowBuild_StillPublishesEarly()
    {
        // The peak is what matters. Keying on the last build meant one cached-output rebuild (~3 ms)
        // switched early publication off again right before the next expensive edit - seen live.
        Assert.True(TerrainInterimPublishPolicy.ShouldPublishGeometryEarly(
            TerrainBuildMode.Final,
            TimeSpan.FromMilliseconds(5_144),
            hasPreviousDisplayState: true));
    }

    [Fact]
    public void ShouldPublishGeometryEarly_NoPriorMeasurement_DoesNot()
    {
        Assert.False(TerrainInterimPublishPolicy.ShouldPublishGeometryEarly(
            TerrainBuildMode.Final,
            null,
            hasPreviousDisplayState: true));
    }

    [Fact]
    public void ShouldPublishGeometryEarly_NothingOnScreenYet_DoesNot()
    {
        // With no previous state there are no outputs to carry forward, so an interim publication would
        // show a terrain with no annotations at all rather than with older ones.
        Assert.False(TerrainInterimPublishPolicy.ShouldPublishGeometryEarly(
            TerrainBuildMode.Final,
            TimeSpan.FromSeconds(5),
            hasPreviousDisplayState: false));
    }

    [Fact]
    public void ShouldPublishGeometryEarly_PreviewBuild_DoesNot()
    {
        // A preview already defers its outputs; it has no separate geometry to publish ahead of them.
        Assert.False(TerrainInterimPublishPolicy.ShouldPublishGeometryEarly(
            TerrainBuildMode.Preview,
            TimeSpan.FromSeconds(5),
            hasPreviousDisplayState: true));
    }

    [Fact]
    public void DisplayState_InterimPublication_ReportsItsOutputsAsStale()
    {
        var state = new TerrainDisplayState
        {
            HasDeferredOutputs = true,
            GeometryRevision = 9,
            OutputsRevision = 8
        };

        Assert.True(state.OutputsAreStale);
        // The existing bake / interop / Grasshopper gates all key off this flag, so it must stay set.
        Assert.True(state.HasDeferredOutputs);
    }

    [Fact]
    public void DisplayState_CompletedFinalBuild_IsNotStale()
    {
        var state = new TerrainDisplayState
        {
            HasDeferredOutputs = false,
            GeometryRevision = 9,
            OutputsRevision = 9
        };

        Assert.False(state.OutputsAreStale);
    }

    [Fact]
    public void DisplayState_Clone_CarriesTheRevisions()
    {
        var state = new TerrainDisplayState { GeometryRevision = 4, OutputsRevision = 3, HasDeferredOutputs = true };

        TerrainDisplayState clone = state.Clone();

        Assert.Equal(4, clone.GeometryRevision);
        Assert.Equal(3, clone.OutputsRevision);
        Assert.True(clone.OutputsAreStale);
    }
}
