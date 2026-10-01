using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The slow-build warning is a modal dialog, so it must only interrupt an edit that is really going to be
/// slow. Found live on 2026-10-01: a 100,911-face terrain with one retaining wall asked before every rail
/// edit, although its last rebuild took 0.07 s.
/// </summary>
public sealed class TerrainSlowBuildWarningPolicyTests
{
    [Fact]
    public void ShouldWarn_LargeTerrainWhoseLastBuildWasFast_DoesNotWarn()
    {
        bool warn = TerrainSlowBuildWarningPolicy.ShouldWarn(
            warningsEnabled: true, TerrainBuildMode.Final, TimeSpan.FromMilliseconds(70),
            displayedFaceCount: 100_911, displayedVertexCount: 50_625, hasExpensiveModifier: true, out _);

        Assert.False(warn);
    }

    [Fact]
    public void ShouldWarn_LastBuildWasSlow_WarnsWithTheMeasuredDuration()
    {
        bool warn = TerrainSlowBuildWarningPolicy.ShouldWarn(
            true, TerrainBuildMode.Final, TimeSpan.FromSeconds(7.2), 100_911, 50_625, true, out string? warning);

        Assert.True(warn);
        Assert.Contains("7.2", warning!.Replace(',', '.'));
    }

    [Theory]
    [InlineData(100_911, false)]
    [InlineData(999_999, false)]
    [InlineData(1_000_000, true)]
    public void ShouldWarn_NothingMeasuredYet_WarnsOnlyForAVeryLargeTerrain(int faces, bool expected)
    {
        bool warn = TerrainSlowBuildWarningPolicy.ShouldWarn(
            true, TerrainBuildMode.Final, lastDuration: null, faces, faces / 2, hasExpensiveModifier: true, out _);

        Assert.Equal(expected, warn);
    }

    [Fact]
    public void ShouldWarn_NothingMeasuredAndNoExpensiveCard_DoesNotWarn()
    {
        Assert.False(TerrainSlowBuildWarningPolicy.ShouldWarn(
            true, TerrainBuildMode.Final, null, 5_000_000, 2_500_000, hasExpensiveModifier: false, out _));
    }

    [Fact]
    public void ShouldWarn_WarningsTurnedOff_NeverWarns()
    {
        Assert.False(TerrainSlowBuildWarningPolicy.ShouldWarn(
            false, TerrainBuildMode.Final, TimeSpan.FromMinutes(3), 5_000_000, 2_500_000, true, out _));
    }

    [Fact]
    public void ShouldWarn_PreviewUsesItsOwnLowerThresholds()
    {
        Assert.True(TerrainSlowBuildWarningPolicy.ShouldWarn(
            true, TerrainBuildMode.Preview, TimeSpan.FromSeconds(2), 10_000, 5_000, true, out _));
        Assert.False(TerrainSlowBuildWarningPolicy.ShouldWarn(
            true, TerrainBuildMode.Final, TimeSpan.FromSeconds(2), 10_000, 5_000, true, out _));
    }
}
