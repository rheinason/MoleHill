using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class BracketedVolumeSearchTests
{
    [Fact]
    public void Search_BracketedLinearNet_ConvergesAndRecordsSamples()
    {
        VolumeSearchResult result = BracketedVolumeSearch.Search(
            z => new VolumeSearchSample(z, z, 0), 0, 10, 3, 0.01);

        Assert.Equal("Converged", result.Status);
        Assert.InRange(result.Best!.Value.Elevation, 2.99, 3.01);
        Assert.True(result.Samples.Count >= 3);
    }

    [Fact]
    public void Search_TargetOutsideBounds_ReturnsNoBracketAndNearerEnd()
    {
        VolumeSearchResult result = BracketedVolumeSearch.Search(
            z => new VolumeSearchSample(z, z, 0), 0, 10, 12, 0.01);

        Assert.Equal("NoBracket", result.Status);
        Assert.Equal(2, result.Samples.Count);
        Assert.Equal(10, result.Best!.Value.Elevation);
    }

    [Fact]
    public void Search_ZeroIterationCap_ReturnsBestEndpoint()
    {
        VolumeSearchResult result = BracketedVolumeSearch.Search(
            z => new VolumeSearchSample(z, z, 0), 0, 10, 3, 0.01, iterationCap: 0);

        Assert.Equal("IterationCap", result.Status);
        Assert.Equal(0, result.Best!.Value.Elevation);
    }

    [Fact]
    public void Search_NonMonotoneSamples_DoesNotReportConverged()
    {
        VolumeSearchResult result = BracketedVolumeSearch.Search(
            z => new VolumeSearchSample(z, z < 5 ? z : 15 - z, 0), 0, 10, 4, 0.01);

        Assert.Equal("NonMonotone", result.Status);
        Assert.NotNull(result.Best);
    }

    [Fact]
    public void Search_GradingFallback_PreservesBestAndSamples()
    {
        VolumeSearchResult result = BracketedVolumeSearch.Search(
            z => new VolumeSearchSample(z, z, 0, GradingFallback: z == 5),
            0, 10, 5, 0.01);

        Assert.Equal("GradingFallback", result.Status);
        Assert.Equal(5, result.Best!.Value.Elevation);
        Assert.Equal(3, result.Samples.Count);
    }

    [Fact]
    public void Search_FailedMidpoint_ReturnsFallbackWithBestCompletedSample()
    {
        VolumeSearchResult result = BracketedVolumeSearch.Search(
            z => z == 5 ? null : new VolumeSearchSample(z, z, 0),
            0, 10, 5, 0.01);

        Assert.Equal("GradingFallback", result.Status);
        Assert.Equal(2, result.Samples.Count);
        Assert.NotNull(result.Best);
    }
}
