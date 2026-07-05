using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Sculpt-session support: the display lock that lets SculptSessionController own the drawn preview
// mesh while a session runs, plus the narrow internal accessors the session needs. The session
// mutates the working mesh in place per dab — a deliberate, gated exception to the swap-only
// DisplayState convention, made safe by re-asserting the working mesh whenever a background build
// (triggered by per-stroke commits) swaps in a fresh state, and by deferring build dispatch while a
// stroke is actively being painted.
internal sealed partial class TerrainController
{
    private Guid? _sculptSessionTerrainId;
    private Mesh? _sculptSessionPreviewMesh;
    private bool _sculptStrokeInProgress;

    internal bool IsSculptSessionActive => _sculptSessionTerrainId.HasValue;

    /// <summary>Takes display authority for a sculpt session: the working mesh replaces the terrain's
    /// preview mesh until <see cref="EndSculptDisplayLock"/>, surviving background build applies.</summary>
    internal void BeginSculptDisplayLock(RhinoDoc doc, Guid terrainId, Mesh workingMesh)
    {
        _sculptSessionTerrainId = terrainId;
        _sculptSessionPreviewMesh = workingMesh;
        _sculptStrokeInProgress = false;
        RemovePendingBuild(doc.RuntimeSerialNumber, terrainId, TerrainBuildMode.Preview);
        RemovePendingBuild(doc.RuntimeSerialNumber, terrainId, TerrainBuildMode.Final);
        ReassertSculptPreviewMesh(doc.RuntimeSerialNumber, terrainId);
    }

    /// <summary>While true, pending builds for the sculpted terrain stay queued (dispatched only
    /// between strokes) so a background apply can't race the in-place vertex edits.</summary>
    internal void SetSculptStrokeInProgress(bool inProgress) => _sculptStrokeInProgress = inProgress;

    /// <summary>Swaps the session's working mesh (DynTopo rebuilt it) while keeping the display lock.</summary>
    internal void UpdateSculptPreviewMesh(RhinoDoc doc, Guid terrainId, Mesh workingMesh)
    {
        if (_sculptSessionTerrainId != terrainId)
            return;

        _sculptSessionPreviewMesh = workingMesh;
        ReassertSculptPreviewMesh(doc.RuntimeSerialNumber, terrainId);
    }

    internal void EndSculptDisplayLock(RhinoDoc doc, Guid terrainId)
    {
        Mesh? releasedMesh = _sculptSessionTerrainId == terrainId
            ? _sculptSessionPreviewMesh
            : null;

        _sculptSessionTerrainId = null;
        _sculptSessionPreviewMesh = null;
        _sculptStrokeInProgress = false;

        if (releasedMesh == null)
            return;

        var displayState = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId).DisplayState;
        if (displayState == null || !ReferenceEquals(displayState.PreviewTerrainMesh, releasedMesh))
            return;

        displayState.PreviewTerrainMesh = displayState.TerrainMesh;
        displayState.InvalidatePreviewBounds();
    }

    private bool ShouldDeferBuildForSculpt(Guid terrainId)
    {
        return _sculptStrokeInProgress && _sculptSessionTerrainId == terrainId;
    }

    private void ReassertSculptPreviewMesh(uint docSerial, Guid terrainId)
    {
        if (_sculptSessionTerrainId != terrainId || _sculptSessionPreviewMesh == null)
            return;

        var displayState = GetRuntimeCache(docSerial, terrainId).DisplayState;
        if (displayState == null)
            return;

        displayState.PreviewTerrainMesh = _sculptSessionPreviewMesh;
        displayState.InvalidatePreviewBounds();
    }

    internal TerrainDefinition? FindTerrain(RhinoDoc doc, Guid terrainId)
    {
        return GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
    }

    internal TerrainRuntimeCache GetSculptRuntimeCache(RhinoDoc doc, Guid terrainId)
    {
        return GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
    }

    /// <summary>Runs a synchronous Final build when the runtime cache has no up-to-date sculpt-stage
    /// output to bind a session to. Returns false when the build failed or was declined.</summary>
    internal bool EnsureFinalBuildForSculpt(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return false;

        return BuildTerrainSynchronously(doc, state, terrain, TerrainBuildMode.Final);
    }

    /// <summary>Opens a document undo record that restores the whole terrain state (the
    /// <see cref="ConvertToRhino"/> precedent). Caller ends it with <c>doc.EndUndoRecord</c>.</summary>
    internal uint BeginTerrainStateUndoRecord(RhinoDoc doc, string description)
    {
        uint undoRecord = doc.BeginUndoRecord(description);
        doc.AddCustomUndoEvent(description, OnRestoreStateUndo, CaptureUndoState(GetState(doc)));
        return undoRecord;
    }

    internal void NotifySculptSessionEnded(RhinoDoc doc, Guid terrainId)
    {
        EndSculptDisplayLock(doc, terrainId);
        RebuildTerrain(doc, terrainId);
    }
}
