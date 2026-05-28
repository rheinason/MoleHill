using System;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainOutputSyncPolicyTests
{
    [Fact]
    public void ShouldSyncAuxiliaryOutput_RetainingWall_ReturnsTrue()
    {
        var generated = new GeneratedRhinoObject
        {
            Name = "Wall",
            Kind = GeneratedObjectKind.RetainingWall
        };

        Assert.True(TerrainOutputSyncPolicy.ShouldSyncAuxiliaryOutput(generated));
    }

    [Fact]
    public void ShouldSyncAuxiliaryOutput_AnalysisOutput_ReturnsFalse()
    {
        var generated = new GeneratedRhinoObject
        {
            Name = "Analysis Label",
            AnalysisId = Guid.NewGuid()
        };

        Assert.False(TerrainOutputSyncPolicy.ShouldSyncAuxiliaryOutput(generated));
    }
}
