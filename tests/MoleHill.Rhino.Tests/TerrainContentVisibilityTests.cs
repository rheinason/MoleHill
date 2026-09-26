using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainContentVisibilityTests
{
    [Fact]
    public void IsOwnerVisible_OwnerNoLongerOnTheTerrain_IsHidden()
    {
        var terrain = new TerrainDefinition();

        Assert.False(TerrainContentVisibility.IsOwnerVisible(terrain, Guid.NewGuid()));
    }

    [Fact]
    public void IsOwnerVisible_EnabledAnalysis_FollowsTheTerrainGate()
    {
        var slope = new SlopeAnalysisDefinition();
        var terrain = new TerrainDefinition { ShowAnalysisOutputs = true };
        terrain.Analyses.Add(slope);

        Assert.True(TerrainContentVisibility.IsOwnerVisible(terrain, slope.Id));

        terrain.ShowAnalysisOutputs = false;
        Assert.False(TerrainContentVisibility.IsOwnerVisible(terrain, slope.Id));
    }
}
