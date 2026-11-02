namespace MoleHill.Rhino.Services;

/// <summary>
/// Mirrors one document's Rhino undo stack by record serial, and pairs each automatic placement record
/// with the record that caused it, so one native Undo or Redo steps over both.
///
/// A live terrain re-places Plant/Orient objects after the edit that moved them has closed its record,
/// so that placement needs a record of its own: rewriting the objects outside any record leaves the
/// earlier record pointing at geometry that no longer exists, and its Undo resurrects the original
/// beside the placed copy (two cars became four, 2026-10-30). But undoing that placement record alone
/// restores the pre-placement pose, which the live rebuild immediately places again, so Undo would never
/// get past it. The pair is therefore undone and redone together: undoing a placement asks for an Undo
/// of its trigger, and redoing a trigger asks for a Redo of its placement. Chains (a placement triggered
/// by a placement) cascade one link at a time.
///
/// Rhino's undo events, as observed live on Rhino 8.35 (the model below depends on each):
/// <list type="bullet">
/// <item>Every command opens a record (BeginRecording, then EndRecording). An empty one is purged at once.</item>
/// <item>_Undo opens a record of its own; the record it undoes reports BeginUndo/EndUndo inside it and is
/// then purged from the undo list. The _Undo record is not an undo-list entry: it is what Redo replays.</item>
/// <item>_Redo reports BeginRedo/EndRedo with the serial of that _Undo record, and the redone change goes
/// back on the undo list under the _Redo command's own serial.</item>
/// </list>
/// So one logical change wears a new serial after every Redo. Pairs are kept by logical id (the serial
/// the change was first recorded under) and every serial maps to one.
///
/// Pure bookkeeping, no Rhino calls: the controller feeds it Rhino's undo events and performs the
/// cascades it asks for.
/// </summary>
internal sealed class PlacementUndoChain
{
    private const int MaxTrackedRecords = 4096;

    private readonly List<uint> _undoStack = new();
    private readonly Dictionary<uint, uint> _logicalBySerial = new();
    private readonly Dictionary<uint, uint> _triggerByPlacement = new();
    private readonly Dictionary<uint, uint> _placementByTrigger = new();
    // Serial of an _Undo command's record -> logical id of the change it undid. Redo names the former.
    private readonly Dictionary<uint, uint> _undoneBy = new();
    private uint _openRecord;
    private bool _openRecordReplays;

    /// <summary>The record on top of the undo stack, or 0 when it is not known.</summary>
    public uint Top => _undoStack.Count == 0 ? 0 : _undoStack[^1];

    public int PairCount => _triggerByPlacement.Count;

    public void RecordingBegan(uint serial)
    {
        _openRecord = serial;
        _openRecordReplays = false;
    }

    /// <summary>An Undo or Redo has begun inside the open record, which is therefore an undo/redo
    /// command's record rather than a new change.</summary>
    public void ReplayBegan() => _openRecordReplays = true;

    public void RecordingEnded(uint serial)
    {
        bool replays = serial == _openRecord && _openRecordReplays;
        if (serial == _openRecord)
        {
            _openRecord = 0;
            _openRecordReplays = false;
        }

        // Pairs are deliberately never pruned when a new record clears Rhino's redo list: a change Rhino
        // can no longer redo never raises RedoEnded, and serials are never reused, so a stale pair is inert.
        if (serial != 0 && !replays)
        {
            Remember(_logicalBySerial, serial, serial);
            Push(serial);
        }
    }

    /// <summary>Pairs a placement record with its trigger. Ignored unless the trigger is the record
    /// directly beneath the placement, which is what makes undoing one always reach the other.</summary>
    public bool Pair(uint placementSerial, uint triggerSerial)
    {
        if (placementSerial == 0 || triggerSerial == 0 || placementSerial == triggerSerial)
            return false;

        int index = _undoStack.LastIndexOf(placementSerial);
        if (index < 1 || _undoStack[index - 1] != triggerSerial)
            return false;

        uint placement = Logical(placementSerial);
        uint trigger = Logical(triggerSerial);
        _triggerByPlacement[placement] = trigger;
        _placementByTrigger[trigger] = placement;
        if (_triggerByPlacement.Count > MaxTrackedRecords)
        {
            uint oldest = _triggerByPlacement.Keys.Min();
            _placementByTrigger.Remove(_triggerByPlacement[oldest]);
            _triggerByPlacement.Remove(oldest);
        }

        return true;
    }

    /// <summary>Returns true when the undone record was an automatic placement, so its trigger must be
    /// undone too.</summary>
    public bool UndoEnded(uint serial)
    {
        int index = _undoStack.LastIndexOf(serial);
        if (index >= 0)
            _undoStack.RemoveRange(index, _undoStack.Count - index);

        uint logical = Logical(serial);
        if (_openRecord != 0)
            Remember(_undoneBy, _openRecord, logical);
        return _triggerByPlacement.ContainsKey(logical);
    }

    /// <summary>Returns true when the redone change triggered an automatic placement, so that placement
    /// must be redone too. <paramref name="serial"/> is what Rhino reports: the _Undo record's serial.</summary>
    public bool RedoEnded(uint serial)
    {
        if (!_undoneBy.TryGetValue(serial, out uint logical))
            return false;

        // The change is undoable again, under the _Redo command's record.
        if (_openRecord != 0)
        {
            Remember(_logicalBySerial, _openRecord, logical);
            Push(_openRecord);
        }

        return _placementByTrigger.ContainsKey(logical);
    }

    /// <summary>A purge only leaves the mirror. Rhino purges each record from its undo list as it is
    /// undone, so dropping the pair here would lose the Redo it still needs.</summary>
    public void Purged(uint serial) => _undoStack.Remove(serial);

    private uint Logical(uint serial) => _logicalBySerial.TryGetValue(serial, out uint logical) ? logical : serial;

    private static void Remember(Dictionary<uint, uint> map, uint key, uint value)
    {
        map[key] = value;
        if (map.Count > MaxTrackedRecords)
            map.Remove(map.Keys.Min());
    }

    private void Push(uint serial)
    {
        _undoStack.Remove(serial);
        _undoStack.Add(serial);
        if (_undoStack.Count > MaxTrackedRecords)
            _undoStack.RemoveRange(0, _undoStack.Count - MaxTrackedRecords);
    }
}
