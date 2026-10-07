using MoleHill.Core.Engine;
using Eto.Forms;
using MoleHill.Core.Sculpting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.UI;
using MoleHill.Shared;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Drives an interactive sculpt session for one sculpt modifier: a long-running GetPoint loop
/// (mouse-up = stroke end, Enter/Esc = done) painting dabs onto a working copy of the sculpt stage's
/// output mesh, plus the floating <see cref="SculptToolbarForm"/> and Blender-style shortcuts
/// (F = radius, Shift+F = strength, Ctrl = invert, Shift = temporary smooth, Ctrl+Z = undo stroke).
/// The get-loop choice is deliberate: inside a running "get" Rhino blocks its own Undo accelerator
/// (so Ctrl+Z can safely mean "undo stroke") and contains stray keystrokes, RMB/MMB navigation passes
/// through, and panel/toolbar buttons stay clickable (the EditSourceObjectIds precedent). Strokes
/// commit their displacement tiles to the modifier definition at mouse-up, which schedules the
/// debounced downstream rebuild; the working mesh keeps display authority (TerrainController sculpt
/// display lock) until the session ends.
/// </summary>
internal sealed class SculptSessionController
{
    public static SculptSessionController Instance { get; } = new();

    // Session preferences — deliberately not on the definition (they would churn the stage
    // fingerprint); they persist across sessions for the lifetime of the Rhino instance.
    public double Radius { get; set; }
    public double Strength { get; set; } = 0.5;
    public SculptBrushKind ActiveBrush { get; set; } = SculptBrushKind.Draw;
    public SculptFalloff Falloff { get; set; } = SculptFalloff.Smooth;

    public bool IsActive { get; private set; }

    private enum AdjustMode { None, Radius, Strength }

    private TerrainController _controller = null!;
    private RhinoDoc _doc = null!;
    private Guid _terrainId;
    private Guid _modifierId;
    private SculptBrushEngine _engine = null!;
    private SculptRayCaster _rayCaster = null!;
    private int _rayCasterTopologyVersion;
    private SculptConstraintMask _constraintMask = null!;
    private RhinoMesh _workingMesh = null!;
    private SculptNormalPatcher _normalPatcher = null!;
    private SculptAnalysisColorizer? _analysisColorizer;
    private SculptToolbarForm? _toolbar;
    private UITimer? _shortcutPollTimer;
    private readonly SculptUndoStack _undoStack = new();
    private readonly SculptZoneFollower _zoneFollower = new();
    private readonly SculptLiveContours _liveContours = new();
    private readonly List<int> _recordAffected = new();

    private AdjustMode _adjustMode;
    private Point3d _adjustAnchor;
    private double _adjustOriginal;
    private Point3d _lastCursor = Point3d.Unset;
    private bool _doneRequested;
    private double _minimumRadius = double.Epsilon;

    /// <summary>Blocks in the get loop until the session ends — invoke via AsyncInvoke from UI code.</summary>
    public void BeginSession(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        if (IsActive)
            return;
        if (!ModelUnitGuard.TryGet(doc, out MoleHill.Shared.ModelUnitContext unitContext))
            return;

        _minimumRadius = Math.Max(doc.ModelAbsoluteTolerance, unitContext.FromMeters(1e-9));

        _controller = TerrainController.Instance;
        _doc = doc;
        _terrainId = terrainId;
        _modifierId = modifierId;

        var terrain = _controller.FindTerrain(doc, terrainId);
        var sculpt = terrain?.Modifiers.FirstOrDefault(m => m.Id == modifierId) as SculptModifierDefinition;
        if (terrain == null || sculpt == null)
            return;

        if (!TryBindWorkingMesh(doc, terrain, sculpt))
        {
            RhinoApp.WriteLine("MoleHill: could not prepare the terrain mesh for sculpting.");
            return;
        }

        if (Radius <= 0.0)
            Radius = sculpt.DetailSize * 20.0;

        IsActive = true;
        _doneRequested = false;
        _adjustMode = AdjustMode.None;
        _lastCursor = Point3d.Unset;
        _fWasDown = false;
        _yWasDown = false;

        uint undoRecord = _controller.BeginTerrainStateUndoRecord(doc, "Sculpt Terrain");
        _controller.BeginSculptDisplayLock(doc, terrainId, _workingMesh, _zoneFollower, _liveContours);
        ShowToolbar(doc, sculpt);
        StartShortcutPolling();
        doc.Views.Redraw();

        try
        {
            RunGetLoop(sculpt);
        }
        finally
        {
            try
            {
                StopShortcutPolling();
                CloseToolbar();

                // Flush the deferred per-stroke saves with one immediate save, close the single
                // session-wide undo step, then hand display back and rebuild canonically.
                _controller.MutateTerrain(doc, terrainId, _ => { }, scheduleRebuild: false);
                if (undoRecord > 0)
                    doc.EndUndoRecord(undoRecord);
                _controller.NotifySculptSessionEnded(doc, terrainId);
            }
            finally
            {
                DisposeWorkingMesh();
                _undoStack.Clear();
                IsActive = false;
            }
        }
    }

    // ── Session setup ──────────────────────────────────────────────────────

    private bool TryBindWorkingMesh(RhinoDoc doc, TerrainDefinition terrain, SculptModifierDefinition sculpt)
    {
        RhinoMesh? stageMesh = GetSculptStageMesh(doc, terrain, sculpt);
        if (stageMesh == null)
        {
            if (!_controller.EnsureFinalBuildForSculpt(doc, terrain.TerrainId))
                return false;
            stageMesh = GetSculptStageMesh(doc, terrain, sculpt);
            if (stageMesh == null)
                return false;
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(stageMesh, out var vertices, out _, out var faces, out _, out _))
            return false;

        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;

        // The engine arrays and the displayed mesh must agree index-for-index, so the working mesh is
        // built 1:1 from the extracted arrays (never normalized — that can reorder vertices).
        _workingMesh = BuildIndexParityMesh(vertices, vertexCount, faces, faceCount);
        _normalPatcher = new SculptNormalPatcher(vertices, vertexCount, faces, faceCount);
        RhinoMesh? baseTerrainMesh = _controller.GetSculptRuntimeCache(doc, terrain.TerrainId).DisplayState?.BaseTerrainMesh;
        _analysisColorizer = SculptAnalysisColorizer.TryCreate(
            doc,
            terrain,
            baseTerrainMesh,
            vertices,
            faces,
            faceCount);
        _analysisColorizer?.ColorAll(_workingMesh);

        var field = SculptFieldCodec.Decode(sculpt);
        TerrainBuildSnapshot snapshot = TerrainBuildSnapshotBuilder.Create(doc, terrain);
        var snapshotSculpt = snapshot.Terrain.Modifiers
            .OfType<SculptModifierDefinition>()
            .FirstOrDefault(item => item.Id == sculpt.Id);
        _constraintMask = snapshotSculpt == null
            ? new SculptConstraintMask(sculpt.EffectiveConstraintFeather)
            : SculptConstraintMaskBuilder.Build(snapshot, snapshot.Terrain, snapshotSculpt);
        _engine = new SculptBrushEngine(
            (double[])vertices.Clone(), vertexCount, faces, faceCount, field, _constraintMask);
        _rayCaster = new SculptRayCaster(_engine.Vertices, vertexCount, faces, faceCount);
        _rayCasterTopologyVersion = _engine.TopologyVersion;
        _liveContours.Bind(doc, terrain, _engine.Vertices, vertexCount, faces, faceCount);
        if (terrain.ShowZoneMeshes && _controller.GetSculptRuntimeCache(doc, terrain.TerrainId).DisplayState is { } displayState)
            _zoneFollower.Bind(displayState.ZoneObjects, _rayCaster, _engine.Vertices);
        return true;
    }

    /// <summary>The cached Final output of this sculpt modifier's own stage — the mesh the session
    /// edits. Binding to stage N (not N-1) keeps existing sculpting visible and lets BaseZ be
    /// recovered as z - field.Sample.</summary>
    private RhinoMesh? GetSculptStageMesh(RhinoDoc doc, TerrainDefinition terrain, SculptModifierDefinition sculpt)
    {
        int index = terrain.Modifiers.IndexOf(sculpt);
        if (index < 0)
            return null;

        var cache = _controller.GetSculptRuntimeCache(doc, terrain.TerrainId);
        string stageKey = TerrainStageKey.ForMode(TerrainBuildMode.Final, TerrainStageKey.CreateModifier(sculpt));
        return cache.StageEntries.TryGetValue(stageKey, out var entry) ? entry.MeshOutput : null;
    }

    private static RhinoMesh BuildIndexParityMesh(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        var mesh = new RhinoMesh();
        mesh.Vertices.Capacity = vertexCount;
        mesh.Faces.Capacity = faceCount;
        for (int i = 0; i < vertexCount; i++)
            mesh.Vertices.Add(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);
        for (int f = 0; f < faceCount; f++)
            mesh.Faces.AddFace(faces[f * 3], faces[f * 3 + 1], faces[f * 3 + 2]);
        mesh.FaceNormals.ComputeFaceNormals();
        mesh.Normals.ComputeNormals();
        return mesh;
    }

    // ── Toolbar ────────────────────────────────────────────────────────────

    private void ShowToolbar(RhinoDoc doc, SculptModifierDefinition sculpt)
    {
        var toolbar = new SculptToolbarForm(doc, radiusReference: Radius);
        toolbar.BrushChanged += brush => { ActiveBrush = brush; SyncToolbar(); };
        toolbar.RadiusChanged += radius => { Radius = Math.Max(radius, _minimumRadius); SyncToolbar(); };
        toolbar.StrengthChanged += strength => { Strength = Math.Clamp(strength, 0.0, 1.0); SyncToolbar(); };
        toolbar.FalloffChanged += falloff => { Falloff = falloff; SyncToolbar(); };
        toolbar.DoneRequested += RequestDone;
        toolbar.Closed += (_, _) =>
        {
            if (IsActive && !_doneRequested)
                RequestDone();
        };

        toolbar.Show();
        toolbar.PositionOverActiveView(doc);
        _toolbar = toolbar;
        SyncToolbar();
        RhinoApp.SetFocusToMainWindow();
    }

    private void SyncToolbar() => _toolbar?.SyncFromSession(ActiveBrush, Radius, Strength, Falloff);

    private void CloseToolbar()
    {
        var toolbar = _toolbar;
        _toolbar = null;
        toolbar?.Close();
    }

    /// <summary>Ends the session from outside the viewport (toolbar Done / window closed): the cancel
    /// keystroke pops the waiting getter, and the done flag makes the loop exit unconditionally.</summary>
    private void RequestDone()
    {
        _doneRequested = true;
        RhinoApp.SendKeystrokes("!", false);
    }

    // ── The get loop ───────────────────────────────────────────────────────

    private void RunGetLoop(SculptModifierDefinition sculpt)
    {
        while (true)
        {
            var getPoint = new SculptGetPoint(this);
            getPoint.SetCommandPrompt(
                "Sculpt. Drag to sculpt, Ctrl = invert, Shift = smooth, F = radius, Shift+F = strength, Ctrl+Z = undo stroke. Enter/Esc = done");
            getPoint.AcceptNothing(true);
            getPoint.AcceptUndo(true);
            getPoint.PermitObjectSnap(false);

            GetResult result = getPoint.Get(true); // true: return on mouse-up => one Get() per stroke

            if (_doneRequested)
            {
                FinishStroke();
                return;
            }

            switch (result)
            {
                case GetResult.Point:
                    if (_adjustMode != AdjustMode.None)
                        _adjustMode = AdjustMode.None; // click confirms the F/Shift+F adjust
                    else
                        FinishStroke();
                    continue;

                case GetResult.Undo:
                    FinishStroke();
                    UndoStroke();
                    continue;

                case GetResult.Nothing: // Enter
                    if (_adjustMode != AdjustMode.None)
                    {
                        _adjustMode = AdjustMode.None;
                        continue;
                    }

                    FinishStroke();
                    return;

                case GetResult.Cancel: // Esc
                    if (_adjustMode != AdjustMode.None)
                    {
                        CancelAdjust();
                        continue;
                    }

                    FinishStroke();
                    return;

                default:
                    FinishStroke();
                    return;
            }
        }
    }

    // ── Keyboard (F / Shift+F / Ctrl+Y) ────────────────────────────────────
    //
    // Polled with GetAsyncKeyState from a short UI timer plus getter mouse events instead of
    // RhinoApp.KeyboardEvent:
    // the app-level hook stops firing once the command-line edit box takes focus (which the stray
    // typed characters inevitably cause), making the shortcuts flaky. Polling is focus-independent.

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private bool _fWasDown;
    private bool _yWasDown;

    private void StartShortcutPolling()
    {
        StopShortcutPolling();
        _shortcutPollTimer = new UITimer { Interval = 0.03 };
        _shortcutPollTimer.Elapsed += OnShortcutPollTimerElapsed;
        _shortcutPollTimer.Start();
    }

    private void StopShortcutPolling()
    {
        if (_shortcutPollTimer == null)
            return;

        _shortcutPollTimer.Stop();
        _shortcutPollTimer.Elapsed -= OnShortcutPollTimerElapsed;
        _shortcutPollTimer = null;
    }

    private void OnShortcutPollTimerElapsed(object? sender, EventArgs e)
    {
        if (!IsActive || _doneRequested)
            return;

        PollShortcuts();
    }

    /// <summary>Edge-triggered shortcut detection, called from the poll timer and getter mouse events.</summary>
    private void PollShortcuts()
    {
        const int VkShift = 0x10;
        const int VkControl = 0x11;
        const int VkF = 0x46;
        const int VkY = 0x59;

        bool f = IsKeyDown(VkF);
        bool y = IsKeyDown(VkY);
        bool ctrl = IsKeyDown(VkControl);
        bool shift = IsKeyDown(VkShift);

        if (f && !_fWasDown && !ctrl && _adjustMode == AdjustMode.None && !_engine.StrokeActive && _lastCursor.IsValid)
        {
            // Anchor placement makes the current value the starting point (Blender's cursor-warp trick
            // done with geometry): distance from anchor to cursor equals the value being adjusted.
            if (shift)
            {
                _adjustMode = AdjustMode.Strength;
                _adjustOriginal = Strength;
                _adjustAnchor = _lastCursor - new Vector3d(Strength * StrengthSpan, 0, 0);
            }
            else
            {
                _adjustMode = AdjustMode.Radius;
                _adjustOriginal = Radius;
                _adjustAnchor = _lastCursor - new Vector3d(Radius, 0, 0);
            }

            _doc.Views.Redraw();
        }
        else if (y && !_yWasDown && ctrl)
        {
            RedoStroke();
        }

        _fWasDown = f;
        _yWasDown = y;
    }

    private double StrengthSpan => Math.Max(Radius * 2.0, _minimumRadius);

    private void UpdateAdjustFromCursor(Point3d cursor)
    {
        double distance = new Point3d(cursor.X, cursor.Y, 0).DistanceTo(new Point3d(_adjustAnchor.X, _adjustAnchor.Y, 0));
        if (_adjustMode == AdjustMode.Radius)
            Radius = Math.Max(distance, _minimumRadius);
        else
            Strength = Math.Clamp(distance / StrengthSpan, 0.0, 1.0);

        SyncToolbar();
    }

    private void CancelAdjust()
    {
        if (_adjustMode == AdjustMode.Radius)
            Radius = _adjustOriginal;
        else if (_adjustMode == AdjustMode.Strength)
            Strength = _adjustOriginal;

        _adjustMode = AdjustMode.None;
        SyncToolbar();
        _doc.Views.Redraw();
    }

    // ── Strokes ────────────────────────────────────────────────────────────

    private SculptDabParams MakeDabParams(double cx, double cy, bool invert, bool temporarySmooth)
    {
        var brush = temporarySmooth ? SculptBrushKind.Smooth : ActiveBrush;
        return new SculptDabParams(cx, cy, Radius, Strength, brush, Falloff, invert, NoiseSeed: 7919);
    }

    private void BeginStroke(in SculptDabParams p)
    {
        _controller.SetSculptStrokeInProgress(true);
        BindZones();
        _engine.BeginStroke(p);
    }

    /// <summary>Points the zone follower at the zones on screen now. Each post-stroke rebuild replaces
    /// them; the follower re-keys its copies to the replacements, and binds (the expensive part) only at
    /// the first stroke with zones shown or when a zone has really changed.</summary>
    private void BindZones()
    {
        TerrainDefinition? terrain = _controller.FindTerrain(_doc, _terrainId);
        TerrainDisplayState? displayState = _controller.GetSculptRuntimeCache(_doc, _terrainId).DisplayState;
        if (terrain == null || displayState == null || !terrain.ShowZoneMeshes)
            return;

        if (_zoneFollower.IsBound)
            _zoneFollower.Rebind(displayState.ZoneObjects, _rayCaster, _engine.Vertices);
        else
            _zoneFollower.Bind(displayState.ZoneObjects, _rayCaster, _engine.Vertices);
    }

    private void ApplyDab(in SculptDabParams p, double grabDeltaZ = 0.0)
    {
        IReadOnlyList<int> affected = _engine.ApplyDab(p, grabDeltaZ);
        if (affected.Count == 0)
            return;

        double[] v = _engine.Vertices;
        foreach (int i in affected)
            _workingMesh.Vertices.SetVertex(i, v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);

        _normalPatcher.PatchNormals(_workingMesh, _engine.Vertices, affected);
        _analysisColorizer?.Recolor(_workingMesh, _normalPatcher.LastTouchedVertices);
        _liveContours.Update(_engine.Vertices, _normalPatcher.LastTouchedFaces);
        _zoneFollower.Follow(
            _engine.Vertices, _engine.Faces,
            p.CenterX - p.Radius, p.CenterX + p.Radius, p.CenterY - p.Radius, p.CenterY + p.Radius);
    }

    private void DisposeWorkingMesh()
    {
        _workingMesh?.Dispose();
        _workingMesh = null!;
        _normalPatcher = null!;
        _analysisColorizer = null;
        _rayCaster = null!;
        _zoneFollower.Clear();
        _liveContours.Clear();
    }

    /// <summary>The cursor ray against the engine's live vertices. Never Intersection.MeshLine on the
    /// working mesh: every dab moves its vertices, so Rhino rebuilds the mesh's search tree on the next
    /// cast — 702 ms per mouse move on a 540k-face terrain.</summary>
    private bool TryCastCursorRay(Line line, out Point3d hit)
    {
        hit = Point3d.Unset;
        if (_engine.TopologyVersion != _rayCasterTopologyVersion)
        {
            _rayCaster = new SculptRayCaster(_engine.Vertices, _engine.VertexCount, _engine.Faces, _engine.FaceCount);
            _rayCasterTopologyVersion = _engine.TopologyVersion;
        }

        Vector3d d = line.Direction;
        if (!_rayCaster.TryIntersect(_engine.Vertices, line.From.X, line.From.Y, line.From.Z, d.X, d.Y, d.Z, out double t))
            return false;

        hit = line.PointAt(t);
        return true;
    }

    private void FinishStroke()
    {
        _controller.SetSculptStrokeInProgress(false);
        SculptStrokeUndoRecord? record = _engine.EndStroke();
        if (record == null || !record.HasDirtyBounds)
            return;

        CaptureTileChanges(record, before: true);
        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(_engine.Vertices, _engine.VertexCount, _engine.Faces, _engine.FaceCount), _engine.BaseZ, _engine.Field, record.DirtyMinX, record.DirtyMaxX, record.DirtyMinY, record.DirtyMaxY, _constraintMask);
        _engine.Field.PruneZeroTiles();
        CaptureTileChanges(record, before: false);
        _undoStack.Push(record);

        CommitFieldToDefinition();
        RefreshSculptDisplay();
    }

    private void UndoStroke()
    {
        SculptStrokeUndoRecord? record = _undoStack.Undo();
        if (record == null)
            return;

        _engine.ApplyUndo(record);
        ApplyRecordToMesh(record);
        CommitFieldToDefinition();
        RefreshSculptDisplay();
    }

    private void RedoStroke()
    {
        SculptStrokeUndoRecord? record = _undoStack.Redo();
        if (record == null)
            return;

        _engine.ApplyRedo(record);
        ApplyRecordToMesh(record);
        CommitFieldToDefinition();
        RefreshSculptDisplay();
    }

    private void ApplyRecordToMesh(SculptStrokeUndoRecord record)
    {
        double[] v = _engine.Vertices;
        _recordAffected.Clear();
        foreach (var (index, _, _) in record.TouchedZ)
        {
            _workingMesh.Vertices.SetVertex(index, v[index * 3], v[index * 3 + 1], v[index * 3 + 2]);
            _recordAffected.Add(index);
        }

        // Midpoints the stroke created were re-seated by the engine undo — sync them too.
        foreach (var (m, _, _) in record.CreatedMidpoints)
        {
            _workingMesh.Vertices.SetVertex(m, v[m * 3], v[m * 3 + 1], v[m * 3 + 2]);
            _recordAffected.Add(m);
        }

        _normalPatcher.PatchNormals(_workingMesh, _engine.Vertices, _recordAffected);
        _analysisColorizer?.Recolor(_workingMesh, _normalPatcher.LastTouchedVertices);
        _liveContours.Update(_engine.Vertices, _normalPatcher.LastTouchedFaces);
        if (record.HasDirtyBounds)
        {
            BindZones();
            _zoneFollower.Follow(
                _engine.Vertices, _engine.Faces,
                record.DirtyMinX, record.DirtyMaxX, record.DirtyMinY, record.DirtyMaxY);
        }
    }

    private void RefreshSculptDisplay()
    {
        _controller.NotifySculptPreviewChanged(_doc, _terrainId);
        _doc.Views.Redraw();
    }

    /// <summary>Snapshots the payload of every tile the stroke's dirty bounds can touch — before the
    /// rasterize (old) or after it (new) — so stroke undo can restore the field exactly.</summary>
    private void CaptureTileChanges(SculptStrokeUndoRecord record, bool before)
    {
        var field = _engine.Field;
        int tiMin = TileFloor(record.DirtyMinX, field.CellSize) - 1;
        int tiMax = TileFloor(record.DirtyMaxX, field.CellSize) + 1;
        int tjMin = TileFloor(record.DirtyMinY, field.CellSize) - 1;
        int tjMax = TileFloor(record.DirtyMaxY, field.CellSize) + 1;

        if (before)
        {
            for (int tj = tjMin; tj <= tjMax; tj++)
            {
                for (int ti = tiMin; ti <= tiMax; ti++)
                    record.TileChanges.Add((ti, tj, field.CopyTile(ti, tj), null));
            }

            return;
        }

        for (int k = 0; k < record.TileChanges.Count; k++)
        {
            var (i, j, oldTile, _) = record.TileChanges[k];
            record.TileChanges[k] = (i, j, oldTile, field.CopyTile(i, j));
        }
    }

    private static int TileFloor(double worldCoordinate, double cellSize)
    {
        int sample = (int)Math.Floor(worldCoordinate / cellSize);
        return (int)Math.Floor(sample / (double)SculptDisplacementField.TileSize);
    }

    private void CommitFieldToDefinition()
    {
        var tiles = SculptFieldCodec.Encode(_engine.Field);
        double cellSize = _engine.Field.CellSize;
        _controller.MutateTerrain(
            _doc,
            _terrainId,
            terrain =>
            {
                if (terrain.Modifiers.FirstOrDefault(m => m.Id == _modifierId) is SculptModifierDefinition sculpt)
                {
                    sculpt.Tiles = tiles;
                    // Pin the spacing the tiles were written at — later DetailSize edits must not
                    // reinterpret them (that rescales the sculpt toward the world origin).
                    sculpt.CellSize = cellSize;
                }
            },
            scheduleRebuild: true,
            deferDocumentSave: true,
            suppressImmediateUiRefresh: true);
    }

    /// <summary>The modal get driving one stroke: mouse-down starts it, drag emits spaced dabs,
    /// mouse-up returns from Get(). Dynamic draw renders the brush cursor circle, and the F/Shift+F
    /// adjust modes hijack mouse-move to scrub radius/strength.</summary>
    private sealed class SculptGetPoint : GetPoint
    {
        private const double DabSpacingFactor = 0.25;

        private readonly SculptSessionController _session;
        private Point3d _cursor = Point3d.Unset;
        private bool _strokeActive;
        private bool _grabStroke;
        private Point3d _lastDab;
        private double _grabStartWindowY;
        private double _grabPixelsPerUnit;
        private double _lastHitZ;
        private bool _hasHitZ;

        public SculptGetPoint(SculptSessionController session)
        {
            _session = session;
        }

        protected override void OnMouseDown(GetPointMouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_session._adjustMode != AdjustMode.None)
                return; // the click confirms the adjust; RunGetLoop clears the mode on GetResult.Point

            if (!TryHitTerrain(e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, out Point3d hit))
                return;

            _cursor = hit;
            _session._lastCursor = hit;
            var p = _session.MakeDabParams(hit.X, hit.Y, e.ControlKeyDown, e.ShiftKeyDown);
            _session.BeginStroke(p);
            _strokeActive = true;
            _grabStroke = p.Brush == SculptBrushKind.Grab;
            _lastDab = hit;

            if (_grabStroke)
            {
                _grabStartWindowY = e.WindowPoint.Y;
                e.Viewport.GetWorldToScreenScale(hit, out _grabPixelsPerUnit);
                if (_grabPixelsPerUnit <= 1e-12)
                    _grabPixelsPerUnit = 1.0;
            }

            _session.ApplyDab(p);
            e.Viewport.ParentView?.Document.Views.Redraw();
        }

        protected override void OnMouseMove(GetPointMouseEventArgs e)
        {
            base.OnMouseMove(e);
            _session.PollShortcuts();

            if (_session._adjustMode != AdjustMode.None)
            {
                if (TryHitTerrain(e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, out Point3d adjustHit))
                {
                    _cursor = adjustHit;
                    _session._lastCursor = adjustHit;
                    _session.UpdateAdjustFromCursor(adjustHit);
                }

                e.Viewport.ParentView?.Document.Views.Redraw();
                return;
            }

            if (_strokeActive && _grabStroke && e.LeftButtonDown)
            {
                // Grab maps vertical screen drag to a Z translation of the captured region.
                double deltaZ = (_grabStartWindowY - e.WindowPoint.Y) / _grabPixelsPerUnit;
                var p = _session.MakeDabParams(_lastDab.X, _lastDab.Y, e.ControlKeyDown, temporarySmooth: false);
                _session.ApplyDab(p, deltaZ);
                e.Viewport.ParentView?.Document.Views.Redraw();
                return;
            }

            if (!TryHitTerrain(e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, out Point3d hit))
                return;

            _cursor = hit;
            _session._lastCursor = hit;
            if (!_strokeActive || !e.LeftButtonDown)
                return;

            double spacing = Math.Max(_session.Radius * DabSpacingFactor, _session._minimumRadius);
            var from2 = new Point3d(_lastDab.X, _lastDab.Y, 0);
            var to2 = new Point3d(hit.X, hit.Y, 0);
            double travel = from2.DistanceTo(to2);
            if (travel < spacing)
                return;

            // Emit evenly spaced dabs along the drag segment so stroke speed doesn't change intensity.
            Vector3d direction = (to2 - from2) / travel;
            bool applied = false;
            while (travel >= spacing)
            {
                from2 += direction * spacing;
                travel -= spacing;
                var p = _session.MakeDabParams(from2.X, from2.Y, e.ControlKeyDown, e.ShiftKeyDown);
                _session.ApplyDab(p);
                applied = true;
            }

            _lastDab = new Point3d(from2.X, from2.Y, hit.Z);
            if (applied)
                e.Viewport.ParentView?.Document.Views.Redraw();
        }

        protected override void OnDynamicDraw(GetPointDrawEventArgs e)
        {
            if (_session._adjustMode != AdjustMode.None)
            {
                var anchorPlane = new Plane(_session._adjustAnchor, Vector3d.ZAxis);
                var adjustColor = System.Drawing.Color.DeepSkyBlue;
                e.Display.DrawCircle(new Circle(anchorPlane, _session.Radius), adjustColor, 2);
                string label = _session._adjustMode == AdjustMode.Radius
                    ? $"R {_session.Radius:0.###}"
                    : $"S {_session.Strength:0.##}";
                if (_cursor.IsValid)
                    e.Display.DrawDot(_cursor, label);
                return;
            }

            if (_cursor.IsValid)
            {
                var plane = new Plane(_cursor, Vector3d.ZAxis);
                var color = _strokeActive ? System.Drawing.Color.Orange : System.Drawing.Color.White;
                e.Display.DrawCircle(new Circle(plane, _session.Radius), color, 2);
                e.Display.DrawPoint(_cursor, PointStyle.RoundSimple, 2, color);
            }

            // Deliberately no base call: the default crosshair/point feedback is noise while painting.
        }

        private bool TryHitTerrain(RhinoViewport viewport, double screenX, double screenY, out Point3d hit)
        {
            hit = Point3d.Unset;
            if (!viewport.GetFrustumLine(screenX, screenY, out Line line))
                return false;

            // GetFrustumLine runs near-to-far, so the nearest hit is the one nearest the camera.
            if (_session.TryCastCursorRay(line, out Point3d best))
            {
                hit = best;
                _lastHitZ = best.Z;
                _hasHitZ = true;
                return true;
            }

            // Off-mesh: keep painting on the horizontal plane of the last hit so strokes can run off
            // the terrain edge without the cursor jumping.
            if (_hasHitZ)
            {
                double dz = line.To.Z - line.From.Z;
                if (Math.Abs(dz) > 1e-12)
                {
                    double t = (_lastHitZ - line.From.Z) / dz;
                    hit = line.PointAt(t);
                    return true;
                }
            }

            return false;
        }
    }
}
