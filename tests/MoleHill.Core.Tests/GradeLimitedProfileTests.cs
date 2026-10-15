using MoleHill.Core.Routing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class GradeLimitedProfileTests
{
    [Fact]
    public void TryFit_GroundWithinLimit_IsTheGround()
    {
        double[] stations = Enumerable.Range(0, 51).Select(i => (double)i).ToArray();
        double[] ground = stations.Select(s => 0.03 * s + (0.5 * Math.Sin(s / 10.0) * 0.1)).ToArray();

        Assert.True(GradeLimitedProfile.TryFit(stations, ground, 0.05, ground[0], ground[^1], out double[] profile, out _));
        for (int i = 0; i < stations.Length; i++)
            Assert.Equal(ground[i], profile[i], 9);
    }

    [Fact]
    public void TryFit_BumpOverLimit_HoldsGradeAndSplitsCutAndFill()
    {
        double[] stations = Enumerable.Range(0, 101).Select(i => (double)i).ToArray();
        double[] ground = stations.Select(s => Math.Max(0.0, 3.0 - Math.Abs(s - 50.0))).ToArray();

        Assert.True(GradeLimitedProfile.TryFit(stations, ground, 0.05, 0.0, 0.0, out double[] profile, out _));
        double maxCut = 0.0, maxFill = 0.0;
        for (int i = 1; i < stations.Length; i++)
            Assert.True(Math.Abs(profile[i] - profile[i - 1]) <= 0.05 + 1e-12);
        for (int i = 0; i < stations.Length; i++)
        {
            maxCut = Math.Max(maxCut, ground[i] - profile[i]);
            maxFill = Math.Max(maxFill, profile[i] - ground[i]);
        }

        Assert.True(maxCut > 0.0);
        Assert.True(maxFill > 0.0);
        Assert.Equal(0.0, profile[0], 12);
        Assert.Equal(0.0, profile[^1], 12);
    }

    [Fact]
    public void TryFit_EndsTooFarApart_ReportsRequiredGrade()
    {
        double[] stations = { 0, 10, 20 };
        double[] ground = { 0, 1, 2 };

        Assert.False(GradeLimitedProfile.TryFit(stations, ground, 0.05, 0.0, 2.0, out _, out double required));
        Assert.Equal(0.1, required, 12);
    }

    [Fact]
    public void TryFit_EndsExactlyAtLimit_Pass()
    {
        double[] stations = { 0, 10, 20 };
        double[] ground = { 0, 5, 1 };

        Assert.True(GradeLimitedProfile.TryFit(stations, ground, 0.05, 0.0, 1.0, out double[] profile, out _));
        Assert.Equal(0.5, profile[1], 12);
    }
}
