namespace MoleHill.Rhino.Services;

// Whether a terrain another terrain compares against (cut/fill, earthworks, sections) can be compared
// against as it stands. The facts are gathered by TerrainController; the decision is here, free of Rhino,
// so it can be tested.

/// <summary>Where a terrain that another terrain compares against stands, from the comparing card's side.</summary>
internal enum ReferenceTerrainState
{
    /// <summary>The comparison used the reference terrain's current surface.</summary>
    Ready,

    /// <summary>The terrain doing the comparing is rebuilding; its card refreshes when that lands.</summary>
    OwnerUpdating,

    /// <summary>The chosen terrain no longer exists in the document.</summary>
    Missing,

    /// <summary>The chosen terrain is building or has a build queued.</summary>
    Building,

    /// <summary>The chosen terrain has not finished a build in this session.</summary>
    NotBuilt,

    /// <summary>The chosen terrain finished a build that produced no surface — usually it has no sources.</summary>
    Empty,

    /// <summary>The chosen terrain was edited with Live Update off, so its surface predates the edit.</summary>
    HasUnbuiltEdits,

    /// <summary>The chosen terrain has changed since this terrain last compared against it.</summary>
    OwnerOutOfDate
}

internal readonly record struct ReferenceTerrainStatus(ReferenceTerrainState State, string Name);

/// <param name="FingerprintOwnerUsed">The reference's final-mesh fingerprint as the comparing terrain's last
/// final build saw it (0 when it had no surface then); null when that build did not compare against it at
/// all, or no final build has landed.</param>
internal readonly record struct ReferenceTerrainFacts(
    bool ReferenceBuildingOrQueued,
    bool ReferenceHasSurface,
    bool ReferenceBuildAttempted,
    bool ReferenceHasUnbuiltEdits,
    bool OwnerBuildingOrQueued,
    ulong? FingerprintOwnerUsed,
    ulong ReferenceCurrentFingerprint);

internal static class ReferenceTerrainStatusRules
{
    /// <summary>
    /// The first thing wrong, in the order a user can act on it: the reference must finish building, then
    /// have a surface, then have its edits built, and only then can the comparing terrain be out of date
    /// against it. While the comparing terrain is itself rebuilding, a mismatch is about to resolve.
    /// </summary>
    public static ReferenceTerrainState Classify(ReferenceTerrainFacts facts)
    {
        if (facts.ReferenceBuildingOrQueued)
            return ReferenceTerrainState.Building;

        if (!facts.ReferenceHasSurface)
            return facts.ReferenceBuildAttempted ? ReferenceTerrainState.Empty : ReferenceTerrainState.NotBuilt;

        if (facts.ReferenceHasUnbuiltEdits)
            return ReferenceTerrainState.HasUnbuiltEdits;

        if (facts.OwnerBuildingOrQueued)
            return ReferenceTerrainState.OwnerUpdating;

        return facts.FingerprintOwnerUsed == facts.ReferenceCurrentFingerprint
            ? ReferenceTerrainState.Ready
            : ReferenceTerrainState.OwnerOutOfDate;
    }
}
