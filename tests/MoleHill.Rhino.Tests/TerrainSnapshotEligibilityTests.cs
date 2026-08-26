using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class TerrainSnapshotEligibilityTests
{
    [Theory]
    [InlineData(false, false, false, "no completed build")]
    [InlineData(true, true, false, "showing a preview")]
    [InlineData(true, false, true, "showing a preview")]
    public void GetRejectionReason_NonFinalState_IsRejected(
        bool hasMesh,
        bool isPreview,
        bool hasDeferredOutputs,
        string expected)
    {
        string? reason = TerrainSnapshotEligibility.GetRejectionReason(
            "Site",
            hasMesh,
            isPreview,
            hasDeferredOutputs);

        Assert.Contains(expected, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRejectionReason_CompletedFinalState_IsAccepted()
    {
        Assert.Null(TerrainSnapshotEligibility.GetRejectionReason(
            "Site",
            hasCompletedMesh: true,
            isPreview: false,
            hasDeferredOutputs: false));
    }
}
