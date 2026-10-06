using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Services;

// Undo: snapshotting terrain state into Rhino undo records, and edit gestures that fold a drag or a
// typed value into one record instead of one per intermediate value.
internal sealed partial class TerrainController
{
    private TerrainUndoSnapshot CaptureUndoState(DocumentState state)
    {
        return new TerrainUndoSnapshot
        {
            Json = TerrainSerializer.Serialize(state.Terrains),
            SelectedTerrainId = state.SelectedTerrainId
        };
    }

    internal void BeginTerrainEditGesture(RhinoDoc doc, string description = "Edit MoleHill Terrain")
    {
        if (doc.UndoRecordingIsActive)
            return;

        PendingTerrainEdit pending = EnsurePendingTerrainEdit(doc, description);
        pending.Depth++;
    }

    internal void EndTerrainEditGesture(RhinoDoc doc)
    {
        if (!_pendingTerrainEdits.TryGetValue(doc.RuntimeSerialNumber, out PendingTerrainEdit? pending))
            return;

        if (pending.Depth > 0)
            pending.Depth--;
        if (pending.Depth == 0)
        {
            if (_pendingDocumentSaves.ContainsKey(doc.RuntimeSerialNumber))
                Save(doc, GetState(doc));
            CommitPendingTerrainEdit(doc);
        }
    }

    private PendingTerrainEdit EnsurePendingTerrainEdit(RhinoDoc doc, string description)
    {
        if (_pendingTerrainEdits.TryGetValue(doc.RuntimeSerialNumber, out PendingTerrainEdit? pending))
            return pending;

        pending = new PendingTerrainEdit(
            CaptureUndoState(GetState(doc)),
            string.IsNullOrWhiteSpace(description) ? "Edit MoleHill Terrain" : description);
        _pendingTerrainEdits[doc.RuntimeSerialNumber] = pending;
        return pending;
    }

    private void CommitPendingTerrainEdit(RhinoDoc doc)
    {
        if (!_pendingTerrainEdits.Remove(doc.RuntimeSerialNumber, out PendingTerrainEdit? pending))
            return;

        RegisterTerrainUndoState(doc, pending.Description, pending.Before);
    }

    private TerrainUndoTransaction? BeginTerrainUndoTransaction(RhinoDoc doc, string description)
    {
        if (!doc.UndoRecordingEnabled)
            return null;

        if (doc.UndoRecordingIsActive)
        {
            JoinActiveTerrainUndoRecord(doc, description);
            return null;
        }

        return new TerrainUndoTransaction(
            this,
            doc,
            string.IsNullOrWhiteSpace(description) ? "Edit MoleHill Terrain" : description,
            CaptureUndoState(GetState(doc)));
    }

    /// <summary>
    /// Attaches one terrain state event to the undo record another command already opened. Pass
    /// <paramref name="before"/> when the edit has already been applied (a coalesced panel gesture);
    /// omit it only when calling ahead of the mutation, where the current state is the before-state.
    /// </summary>
    private void JoinActiveTerrainUndoRecord(RhinoDoc doc, string description, TerrainUndoSnapshot? before = null)
    {
        uint undoSerial = doc.CurrentUndoRecordSerialNumber;
        if (undoSerial == 0)
            return;

        // Record serials are monotonic and only one record is open at a time, so every smaller serial
        // for this document is closed and can never be joined again.
        _terrainUndoRecords.RemoveWhere(item => item.docSerial == doc.RuntimeSerialNumber && item.undoSerial < undoSerial);
        if (!_terrainUndoRecords.Add((doc.RuntimeSerialNumber, undoSerial)))
            return;

        string resolvedDescription = string.IsNullOrWhiteSpace(description) ? "Edit MoleHill Terrain" : description;
        doc.AddCustomUndoEvent(
            resolvedDescription,
            OnRestoreStateUndo,
            (before ?? CaptureUndoState(GetState(doc))).WithDescription(resolvedDescription));
    }

    private void RegisterTerrainUndoState(RhinoDoc doc, string description, TerrainUndoSnapshot before)
    {
        if (!doc.UndoRecordingEnabled || before.HasSameState(CaptureUndoState(GetState(doc))))
            return;

        // A nested BeginUndoRecord returns 0, so a gesture that ends while a command record is open
        // has to join that record instead of silently dropping its history.
        if (doc.UndoRecordingIsActive)
        {
            JoinActiveTerrainUndoRecord(doc, description, before);
            return;
        }

        uint undoRecord = doc.BeginUndoRecord(description);
        if (undoRecord == 0)
            return;

        try
        {
            doc.AddCustomUndoEvent(description, OnRestoreStateUndo, before.WithDescription(description));
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }
    }

    private void RestoreUndoState(RhinoDoc doc, TerrainUndoSnapshot snapshot)
    {
        var restoredTerrains = TerrainSerializer.Deserialize(
            snapshot.Json,
            MoleHill.Shared.ModelUnitContext.FromDocument(doc));
        Guid? restoredSelection = snapshot.SelectedTerrainId is Guid selectedId &&
                                  restoredTerrains.Any(item => item.TerrainId == selectedId)
            ? selectedId
            : restoredTerrains.FirstOrDefault()?.TerrainId;
        var restoredState = new DocumentState
        {
            Terrains = restoredTerrains,
            SelectedTerrainId = restoredSelection
        };

        _pendingTerrainEdits.Remove(doc.RuntimeSerialNumber);
        _pendingSourceReferencePrunes.Remove(doc.RuntimeSerialNumber);
        _pendingObjectReplacements.Remove(doc.RuntimeSerialNumber);
        RemovePendingDocumentSave(doc.RuntimeSerialNumber);
        _states[doc.RuntimeSerialNumber] = restoredState;
        ClearRebuildStates(doc.RuntimeSerialNumber);
        ClearRuntimeCaches(doc.RuntimeSerialNumber);
        Save(doc, restoredState, raiseStateChanged: false);
        foreach (TerrainDefinition terrain in restoredState.Terrains.Where(item => item.LiveUpdateEnabled))
            ScheduleRebuild(doc, terrain.TerrainId, notify: false);
        NotifyRenderMeshesChanged(doc);
        doc.Views.Redraw();
        RaiseStateChanged();
    }

    private sealed class PendingTerrainEdit
    {
        public PendingTerrainEdit(TerrainUndoSnapshot before, string description)
        {
            Before = before;
            Description = description;
        }

        public TerrainUndoSnapshot Before { get; }
        public string Description { get; }
        public int Depth { get; set; }
    }

    private sealed class TerrainUndoTransaction : IDisposable
    {
        private readonly TerrainController _controller;
        private readonly RhinoDoc _doc;
        private readonly string _description;
        private readonly TerrainUndoSnapshot _before;
        private readonly uint _undoRecord;
        private bool _disposed;

        public TerrainUndoTransaction(TerrainController controller, RhinoDoc doc, string description, TerrainUndoSnapshot before)
        {
            _controller = controller;
            _doc = doc;
            _description = description;
            _before = before;
            _undoRecord = doc.BeginUndoRecord(description);
            if (_undoRecord > 0)
                controller._terrainUndoRecords.Add((doc.RuntimeSerialNumber, _undoRecord));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_undoRecord == 0)
                return;

            try
            {
                TerrainUndoSnapshot after = _controller.CaptureUndoState(_controller.GetState(_doc));
                if (!_before.HasSameState(after))
                {
                    _doc.AddCustomUndoEvent(
                        _description,
                        _controller.OnRestoreStateUndo,
                        _before.WithDescription(_description));
                }
            }
            finally
            {
                _doc.EndUndoRecord(_undoRecord);
                _controller._terrainUndoRecords.Remove((_doc.RuntimeSerialNumber, _undoRecord));
            }
        }
    }
}
