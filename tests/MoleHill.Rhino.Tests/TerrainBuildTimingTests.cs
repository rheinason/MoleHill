using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainBuildTimingTests
{
    [Fact]
    public void RecordTiming_CacheHitDetail_SetsStructuredCacheHitFlag()
    {
        var result = new TerrainBuildResult();

        result.RecordTiming("Remesh", TimeSpan.FromMilliseconds(2), "8,000 faces; cache hit");

        TerrainBuildTiming timing = Assert.Single(result.Timings);
        Assert.True(timing.IsCacheHit);
    }

    [Theory]
    [InlineData("timing.Grade Path: 0.8 s | cold")]
    [InlineData("Retaining Wall remesh timing\nfallback.prepare_input: 140 ms")]
    [InlineData("[Info] Retaining wall planner timing: preprocess 300 ms")]
    public void IsCachedTimingDiagnostic_ColdTimingMessages_ReturnsTrue(string message)
    {
        Assert.True(TerrainBuildService.IsCachedTimingDiagnostic(message));
    }

    [Fact]
    public void IsCachedTimingDiagnostic_NonTimingWarning_ReturnsFalse()
    {
        Assert.False(TerrainBuildService.IsCachedTimingDiagnostic("Retaining Wall kept the upstream mesh."));
    }
}
