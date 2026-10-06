using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// A comparison against another terrain used to fall back to this terrain's own base triangulation in
/// silence when that terrain had no surface. These pin which warning each state produces.
/// </summary>
public class ReferenceTerrainStatusRulesTests
{
    private static readonly ReferenceTerrainFacts Current = new(
        ReferenceBuildingOrQueued: false,
        ReferenceHasSurface: true,
        ReferenceBuildAttempted: true,
        ReferenceHasUnbuiltEdits: false,
        OwnerBuildingOrQueued: false,
        FingerprintOwnerUsed: 42,
        ReferenceCurrentFingerprint: 42);

    [Fact]
    public void Classify_ComparedAgainstTheCurrentSurface_IsReady()
    {
        Assert.Equal(ReferenceTerrainState.Ready, ReferenceTerrainStatusRules.Classify(Current));
    }

    [Fact]
    public void Classify_ReferenceNeverBuilt_IsNotBuilt()
    {
        var facts = Current with { ReferenceHasSurface = false, ReferenceBuildAttempted = false, FingerprintOwnerUsed = 0 };

        Assert.Equal(ReferenceTerrainState.NotBuilt, ReferenceTerrainStatusRules.Classify(facts));
    }

    [Fact]
    public void Classify_ReferenceBuiltButProducedNothing_IsEmpty()
    {
        var facts = Current with { ReferenceHasSurface = false, FingerprintOwnerUsed = 0 };

        Assert.Equal(ReferenceTerrainState.Empty, ReferenceTerrainStatusRules.Classify(facts));
    }

    [Fact]
    public void Classify_ReferenceBuilding_WinsOverHavingNoSurfaceYet()
    {
        var facts = Current with { ReferenceBuildingOrQueued = true, ReferenceHasSurface = false };

        Assert.Equal(ReferenceTerrainState.Building, ReferenceTerrainStatusRules.Classify(facts));
    }

    [Fact]
    public void Classify_ReferenceEditedWithLiveUpdateOff_SaysSoEvenWhenFingerprintsMatch()
    {
        var facts = Current with { ReferenceHasUnbuiltEdits = true };

        Assert.Equal(ReferenceTerrainState.HasUnbuiltEdits, ReferenceTerrainStatusRules.Classify(facts));
    }

    [Theory]
    [InlineData(41UL)]
    [InlineData(0UL)]
    [InlineData(null)]
    public void Classify_ReferenceChangedSinceTheOwnerBuilt_IsOwnerOutOfDate(ulong? used)
    {
        // 0: the owner built while the reference had no surface (and silently used its base mesh).
        // null: the owner's last build did not compare against this terrain at all.
        var facts = Current with { FingerprintOwnerUsed = used };

        Assert.Equal(ReferenceTerrainState.OwnerOutOfDate, ReferenceTerrainStatusRules.Classify(facts));
    }

    [Fact]
    public void Classify_OwnerAlreadyRebuilding_DoesNotWarnAboutTheMismatchItIsFixing()
    {
        var facts = Current with { FingerprintOwnerUsed = 0, OwnerBuildingOrQueued = true };

        Assert.Equal(ReferenceTerrainState.OwnerUpdating, ReferenceTerrainStatusRules.Classify(facts));
    }
}
