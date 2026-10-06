using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Services;

// Per-document terrain state: loading it, saving it through TerrainDocumentStore, and recovering
// from terrain data that could not be read.
internal sealed partial class TerrainController
{
    /// <summary>
    /// True when this document's stored terrain JSON could not be read. The panel should surface a
    /// "Reset terrain data" action while this is set, since <see cref="GetTerrains"/> will otherwise
    /// silently look like "no terrains" and any save is refused (see <see cref="ResetTerrainDataAfterFailedLoad"/>).
    /// </summary>
    public bool IsTerrainDataUnreadable(RhinoDoc doc) => GetState(doc).LoadFailed;

    public void ReloadDocumentState(RhinoDoc doc)
    {
        ClearDocumentState(doc.RuntimeSerialNumber);
        NotifyRenderMeshesChanged(doc);
        doc.Views.Redraw();
        RaiseStateChanged();
    }

    private DocumentState GetState(RhinoDoc doc)
    {
        if (_states.TryGetValue(doc.RuntimeSerialNumber, out var state))
            return state;

        List<TerrainDefinition>? terrains = _documentStore.Load(doc, out string? failureMessage);
        state = new DocumentState
        {
            Terrains = terrains ?? new List<TerrainDefinition>(),
            LoadFailed = terrains == null
        };
        if (terrains == null)
        {
            RhinoApp.WriteLine(
                $"[MoleHill] Could not read terrain data in this document: {failureMessage}. " +
                "Terrain state is read-only until resolved (use \"Reset terrain data\" in the terrain picker to discard it).");
        }
        state.SelectedTerrainId = state.Terrains.FirstOrDefault()?.TerrainId;
        _states[doc.RuntimeSerialNumber] = state;
        return state;
    }

    private void Save(RhinoDoc doc, DocumentState state, bool raiseStateChanged = true)
    {
        if (state.LoadFailed)
        {
            RhinoApp.WriteLine("[MoleHill] Not saving terrain data: the stored data could not be read " +
                "and would be overwritten with an empty list. Use \"Reset terrain data\" in the terrain picker to discard it.");
            return;
        }

        RemovePendingDocumentSave(doc.RuntimeSerialNumber);
        // Writing the document strings raises DocumentPropertiesChanged, which would echo back as a full
        // panel refresh (twice: the backup entry and the terrain entry). The caller already decides that.
        using (new EventSuppression(this))
            _documentStore.Save(doc, state.Terrains);
        if (raiseStateChanged)
            RaiseStateChanged();
    }

    /// <summary>
    /// Explicit user action to discard unreadable terrain JSON (from the terrain picker's
    /// "Reset terrain data" item) so the document can be saved to again. Not reachable from any
    /// automatic path.
    /// </summary>
    public void ResetTerrainDataAfterFailedLoad(RhinoDoc doc)
    {
        var state = GetState(doc);
        if (!state.LoadFailed)
            return;

        state.LoadFailed = false;
        Save(doc, state);
    }

    private void ClearDocumentState(uint docSerial)
    {
        _states.Remove(docSerial);
        _pendingTerrainEdits.Remove(docSerial);
        _terrainUndoRecords.RemoveWhere(item => item.docSerial == docSerial);
        _pendingSourceReferencePrunes.Remove(docSerial);
        _pendingObjectReplacements.Remove(docSerial);
        _unbuiltEdits.RemoveWhere(item => item.DocSerial == docSerial);
        foreach (var key in _rebuildWhenReady.Keys.Where(key => key.DocSerial == docSerial).ToList())
            _rebuildWhenReady.Remove(key);
        ClearRuntimeCaches(docSerial);
        ClearRebuildStates(docSerial);
        RemovePendingDocumentSave(docSerial);
    }

    private sealed class DocumentState
    {
        public List<TerrainDefinition> Terrains { get; set; } = new();
        public Guid? SelectedTerrainId { get; set; }

        /// <summary>
        /// Set when the stored terrain JSON could not be read (truncated, or an unrecognized
        /// `$type`). While set, <see cref="Save"/> refuses to persist so the unreadable original is
        /// never overwritten with an empty list. Cleared only by an explicit user reset.
        /// </summary>
        public bool LoadFailed { get; set; }
    }
}
