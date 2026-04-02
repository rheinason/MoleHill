using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainCommitGuardTests
{
    [Fact]
    public void HasMeaningfulNumericChange_UnchangedValue_ReturnsFalse()
    {
        Assert.False(TerrainCommitGuard.HasMeaningfulNumericChange(12.5, 12.5));
    }

    [Fact]
    public void HasMeaningfulNumericChange_SubEpsilonDelta_ReturnsFalse()
    {
        Assert.False(TerrainCommitGuard.HasMeaningfulNumericChange(12.5, 12.5 + 1e-10));
    }

    [Fact]
    public void HasMeaningfulNumericChange_LargerDelta_ReturnsTrue()
    {
        Assert.True(TerrainCommitGuard.HasMeaningfulNumericChange(12.5, 12.5001));
    }
}
