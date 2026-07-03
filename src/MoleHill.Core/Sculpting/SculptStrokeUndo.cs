namespace MoleHill.Core.Sculpting;

/// <summary>
/// Everything needed to undo (and redo) one sculpt stroke: sparse per-vertex Z captured on first
/// touch (old) and at stroke end (new), the displacement tiles the stroke's commit replaced, and the
/// stroke's world-XY dirty bounds. Tile changes are recorded by the commit path (the session), not by
/// the brush engine — brushes only move vertices; the field is written at stroke end.
/// </summary>
public sealed class SculptStrokeUndoRecord
{
    public List<(int VertexIndex, double OldZ, double NewZ)> TouchedZ { get; } = new();

    /// <summary>
    /// Vertices DynTopo created during this stroke, each the midpoint of two lower-index parents (in
    /// creation order). Session topology is forward-only — undo never removes vertices; instead these
    /// are re-seated on the restored surface as the average of their parents' Z, which is exact because
    /// refinement midpoints lie on the piecewise-linear surface by construction.
    /// </summary>
    public List<(int Midpoint, int ParentA, int ParentB)> CreatedMidpoints { get; } = new();

    /// <summary>Tiles replaced when the stroke was committed: payload before (null = tile absent) and after.</summary>
    public List<(int I, int J, float[]? OldTile, float[]? NewTile)> TileChanges { get; } = new();

    public double DirtyMinX { get; set; } = double.MaxValue;
    public double DirtyMaxX { get; set; } = double.MinValue;
    public double DirtyMinY { get; set; } = double.MaxValue;
    public double DirtyMaxY { get; set; } = double.MinValue;

    public bool HasDirtyBounds => DirtyMinX <= DirtyMaxX && DirtyMinY <= DirtyMaxY;

    public void ExpandDirtyBounds(double x, double y, double radius)
    {
        if (x - radius < DirtyMinX) DirtyMinX = x - radius;
        if (x + radius > DirtyMaxX) DirtyMaxX = x + radius;
        if (y - radius < DirtyMinY) DirtyMinY = y - radius;
        if (y + radius > DirtyMaxY) DirtyMaxY = y + radius;
    }
}

/// <summary>Linear stroke history for one sculpt session. Pushing after an undo drops the redo branch.</summary>
public sealed class SculptUndoStack
{
    private readonly List<SculptStrokeUndoRecord> _records = new();
    private int _applied; // number of records currently applied (undo cursor)

    public bool CanUndo => _applied > 0;
    public bool CanRedo => _applied < _records.Count;

    public void Push(SculptStrokeUndoRecord record)
    {
        if (_applied < _records.Count)
            _records.RemoveRange(_applied, _records.Count - _applied);
        _records.Add(record);
        _applied = _records.Count;
    }

    /// <summary>The most recent applied stroke, to be reverted by the caller; null when nothing to undo.</summary>
    public SculptStrokeUndoRecord? Undo()
    {
        if (!CanUndo)
            return null;
        return _records[--_applied];
    }

    /// <summary>The next unapplied stroke, to be re-applied by the caller; null when nothing to redo.</summary>
    public SculptStrokeUndoRecord? Redo()
    {
        if (!CanRedo)
            return null;
        return _records[_applied++];
    }

    public void Clear()
    {
        _records.Clear();
        _applied = 0;
    }
}
