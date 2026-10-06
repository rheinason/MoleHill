using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Services;

// Cross-terrain comparison health. Cut/fill, earthworks and section cards compare against another
// terrain's finished mesh. When that terrain had no mesh, the build fell back to this terrain's own base
// triangulation without a word, so the card showed plausible figures against the wrong ground. These say
// which case applies, and back the card's one-click rebuild so the user need not switch terrains to run it.
internal sealed partial class TerrainController
{
    /// <summary>Terrains edited while Live Update was off, so their surface predates the edit.</summary>
    private readonly HashSet<(uint DocSerial, Guid TerrainId)> _unbuiltEdits = new();

    /// <summary>
    /// Terrains to rebuild once a terrain they compare against finishes building, keyed by that terrain.
    /// Filled by the card's rebuild link, so the comparing terrain catches up even with Live Update off —
    /// the automatic follow-up (<see cref="ScheduleTerrainDependents"/>) only covers live terrains.
    /// </summary>
    private readonly Dictionary<(uint DocSerial, Guid ReferenceId), HashSet<Guid>> _rebuildWhenReady = new();

    public ReferenceTerrainStatus GetReferenceTerrainStatus(RhinoDoc doc, Guid ownerId, Guid referenceId)
    {
        uint docSerial = doc.RuntimeSerialNumber;
        TerrainDefinition? reference = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == referenceId);
        if (reference == null)
            return new ReferenceTerrainStatus(ReferenceTerrainState.Missing, string.Empty);

        ulong? usedFingerprint = _runtimeCaches.TryGetValue((docSerial, ownerId), out TerrainRuntimeCache? ownerCache)
            && ownerCache.ReferencedTerrainFingerprints is { } used
            && used.TryGetValue(referenceId, out ulong fingerprint)
                ? fingerprint
                : null;
        var facts = new ReferenceTerrainFacts(
            ReferenceBuildingOrQueued: IsBuildingOrQueued(docSerial, referenceId),
            ReferenceHasSurface: GetFinalTerrainMesh(doc, referenceId) is { Faces.Count: > 0 },
            ReferenceBuildAttempted: _rebuildStates.TryGetValue((docSerial, referenceId), out TerrainRebuildState? rebuild)
                && rebuild.AppliedVersion > 0,
            ReferenceHasUnbuiltEdits: _unbuiltEdits.Contains((docSerial, referenceId)),
            OwnerBuildingOrQueued: IsBuildingOrQueued(docSerial, ownerId),
            FingerprintOwnerUsed: usedFingerprint,
            ReferenceCurrentFingerprint: _runtimeCaches.TryGetValue((docSerial, referenceId), out TerrainRuntimeCache? referenceCache)
                ? referenceCache.LastFinalMeshFingerprint
                : 0UL);
        return new ReferenceTerrainStatus(ReferenceTerrainStatusRules.Classify(facts), reference.Name);
    }

    /// <summary>
    /// The card's rebuild link: builds the terrain being compared against, then the terrain comparing,
    /// once the first has landed — whatever either one's Live Update setting.
    /// </summary>
    public void RebuildReferenceTerrain(RhinoDoc doc, Guid ownerId, Guid referenceId)
    {
        var key = (doc.RuntimeSerialNumber, referenceId);
        if (!_rebuildWhenReady.TryGetValue(key, out HashSet<Guid>? waiting))
        {
            waiting = new HashSet<Guid>();
            _rebuildWhenReady[key] = waiting;
        }

        waiting.Add(ownerId);
        RebuildTerrain(doc, referenceId);
    }

    /// <summary>Called when a terrain's final build lands: rebuilds anything that asked to wait on it.</summary>
    private void RebuildTerrainsWaitingOn(RhinoDoc doc, Guid referenceId)
    {
        if (!_rebuildWhenReady.Remove((doc.RuntimeSerialNumber, referenceId), out HashSet<Guid>? waiting))
            return;

        DocumentState state = GetState(doc);
        foreach (Guid ownerId in waiting)
        {
            if (ownerId != referenceId && state.Terrains.Any(item => item.TerrainId == ownerId))
                ScheduleRebuild(doc, ownerId, notify: false);
        }
    }

    /// <summary>
    /// Records that a terrain changed while Live Update was off. Nothing else notices — no build is
    /// scheduled — so without this a terrain compared against would look current until someone rebuilt it.
    /// </summary>
    private void MarkUnbuiltEdits(RhinoDoc doc, Guid terrainId)
    {
        if (_unbuiltEdits.Add((doc.RuntimeSerialNumber, terrainId)))
            RaiseStatusChanged();
    }

    /// <summary>A final build snapshots the definition as it stands, so every edit so far is included.</summary>
    private void ClearUnbuiltEdits(uint docSerial, Guid terrainId) =>
        _unbuiltEdits.Remove((docSerial, terrainId));

    private bool IsBuildingOrQueued(uint docSerial, Guid terrainId) =>
        (_rebuildStates.TryGetValue((docSerial, terrainId), out TerrainRebuildState? rebuild) && rebuild.IsBuilding)
        || _pendingRebuilds.ContainsKey((docSerial, terrainId, TerrainBuildMode.Final))
        || _pendingRebuilds.ContainsKey((docSerial, terrainId, TerrainBuildMode.Preview));
}
