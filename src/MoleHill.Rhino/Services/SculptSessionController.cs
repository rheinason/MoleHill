using MoleHill.Core.Sculpting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.UI;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
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
    private RhinoMesh _workingMesh = null!;
    private SculptNormalPatcher _normalPatcher = null!;
    private SculptToolbarForm? _toolbar;
    private readonly SculptUndoStack _undoStack = new();
    private readonly List<int> _recordAffected = new();

    private AdjustMode _adjustMode;
    private Point3d _adjustAnchor;
    private double _adjustOriginal;
    private Point3d _lastCursor = Point3d.Unset;
    private bool _doneRequested;
    private bool _dynTopo;
    private double _detailSize;

    /// <summary>Blocks in the get loop until the session ends — invoke via AsyncInvoke from UI code.</summary>
    public void BeginSession(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        if (IsActive)
            return;

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

        _dynTopo = sculpt.DynTopo;
        _detailSize = sculpt.DetailSize;
        IsActive = true;
        _doneRequested = false;
        _adjustMode = AdjustMode.None;
        _lastCursor = Point3d.Unset;

        uint undoRecord = _controller.BeginTerrainStateUndoRecord(doc, "Sculpt Terrain");
        _controller.BeginSculptDisplayLock(doc, terrainId, _workingMesh);
        ShowToolbar(doc, sculpt);
        doc.Views.Redraw();

        try
        {
            RunGetLoop(sculpt);
        }
        finally
        {
            CloseToolbar();

            // Flush the deferred per-stroke saves with one immediate save, close the single
            // session-wide undo step, then hand display back and rebuild canonically.
            _controller.MutateTerrain(doc, terrainId, _ => { }, scheduleRebuild: false);
            if (undoRecord > 0)
                doc.EndUndoRecord(undoRecord);
            _controller.NotifySculptSessionEnded(doc, terrainId);
            _undoStack.Clear();
            IsActive = false;
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

        if (!RhinoGeometryConversions.TryExtractMeshData(stageMesh, out var vertices, out var faces, out _))
            return false;

        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;

        // The engine arrays and the displayed mesh must agree index-for-index, so the working mesh is
        // built 1:1 from the extracted arrays (never normalized — that can reorder vertices).
        _workingMesh = BuildIndexParityMesh(vertices, vertexCount, faces, faceCount);
        _normalPatcher = new SculptNormalPatcher(vertexCount, faces, faceCount);

        var field = SculptFieldCodec.Decode(sculpt);
        _engine = new SculptBrushEngine((double[])vertices.Clone(), vertexCount, faces, faceCount, field);
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
        string stageKey = TerrainStageKey.ForMode(TerrainBuildMode.Final, TerrainStageKey.CreateModifier(index, sculpt));
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
        var toolbar = new SculptToolbarForm(doc, radiusReference: Radius, dynTopo: sculpt.DynTopo);
        toolbar.BrushChanged += brush => { ActiveBrush = brush; SyncToolbar(); };
        toolbar.RadiusChanged += radius => { Radius = Math.Max(radius, 1e-6); SyncToolbar(); };
        toolbar.StrengthChanged += strength => { Strength = Math.Clamp(strength, 0.0, 1.0); SyncToolbar(); };
        toolbar.FalloffChanged += falloff => { Falloff = falloff; SyncToolbar(); };
        toolbar.DynTopoChanged += enabled =>
        {
            _dynTopo = enabled;
            _controller.MutateTerrain(
                _doc,
                _terrainId,
                terrain =>
                {
                    if (terrain.Modifiers.FirstOrDefault(m => m.Id == _modifierId) is SculptModifierDefinition s)
                        s.DynTopo = enabled;
                },
                suppressImmediateUiRefresh: true);
        };
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
    // Polled with GetAsyncKeyState from the getter's mouse events instead of RhinoApp.KeyboardEvent:
    // the app-level hook stops firing once the command-line edit box takes focus (which the stray
    // typed characters inevitably cause), making the shortcuts flaky. Polling is focus-independent.

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private bool _fWasDown;
    private bool _yWasDown;

    /// <summary>Edge-triggered shortcut detection, called on every getter mouse event.</summary>
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

    private double StrengthSpan => Math.Max(Radius * 2.0, 1e-6);

    private void UpdateAdjustFromCursor(Point3d cursor)
    {
        double distance = new Point3d(cursor.X, cursor.Y, 0).DistanceTo(new Point3d(_adjustAnchor.X, _adjustAnchor.Y, 0));
        if (_adjustMode == AdjustMode.Radius)
            Radius = Math.Max(distance, 1e-6);
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
        // Grab captures vertex indices at stroke start, so its region must be refined before the
        // capture — and never again mid-drag.
        if (_dynTopo)
            RefineUnderBrush(p.CenterX, p.CenterY, Radius);
        _engine.BeginStroke(p);
    }

    /// <summary>DynTopo densification for one drag segment — called once per mouse event (not per
    /// dab: several dabs can fire per event and each refine scans the whole mesh).</summary>
    private void RefineForSpan(double cx, double cy, double spanRadius)
    {
        if (_dynTopo)
            RefineUnderBrush(cx, cy, spanRadius);
    }

    private void ApplyDab(in SculptDabParams p, double grabDeltaZ = 0.0)
    {
        IReadOnlyList<int> affected = _engine.ApplyDab(p, grabDeltaZ);
        if (affected.Count == 0)
            return;

        double[] v = _engine.Vertices;
        foreach (int i in affected)
            _workingMesh.Vertices.SetVertex(i, v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);

        _normalPatcher.PatchNormals(_workingMesh, affected);
    }

    /// <summary>DynTopo: densify the working mesh under the brush; on topology change, rebuild the
    /// display mesh and normal index from the engine arrays and re-point the display lock.</summary>
    private void RefineUnderBrush(double cx, double cy, double radius)
    {
        if (!_engine.RefineRegion(cx, cy, radius, _detailSize))
            return;

        _workingMesh = BuildIndexParityMesh(_engine.Vertices, _engine.VertexCount, _engine.Faces, _engine.FaceCount);
        _normalPatcher = new SculptNormalPatcher(_engine.VertexCount, _engine.Faces, _engine.FaceCount);
        _controller.UpdateSculptPreviewMesh(_doc, _terrainId, _workingMesh);
    }

    private void FinishStroke()
    {
        _controller.SetSculptStrokeInProgress(false);
        SculptStrokeUndoRecord? record = _engine.EndStroke();
        if (record == null || !record.HasDirtyBounds)
            return;

        CaptureTileChanges(record, before: true);
        SculptFieldRasterizer.Rasterize(
            _engine.Vertices, _engine.VertexCount, _engine.Faces, _engine.FaceCount,
            _engine.BaseZ, _engine.Field,
            record.DirtyMinX, record.DirtyMaxX, record.DirtyMinY, record.DirtyMaxY);
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

        _normalPatcher.PatchNormals(_workingMesh, _recordAffected);
    }

    private void RefreshSculptDisplay()
    {
        var displayState = _controller.GetSculptRuntimeCache(_doc, _terrainId).DisplayState;
        displayState?.InvalidatePreviewBounds();
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

            double spacing = Math.Max(_session.Radius * DabSpacingFactor, 1e-9);
            var from2 = new Point3d(_lastDab.X, _lastDab.Y, 0);
            var to2 = new Point3d(hit.X, hit.Y, 0);
            double travel = from2.DistanceTo(to2);
            if (travel < spacing)
                return;

            // One DynTopo pass covering the whole drag segment (cheaper than per dab).
            _session.RefineForSpan(
                (from2.X + to2.X) * 0.5,
                (from2.Y + to2.Y) * 0.5,
                _session.Radius + travel * 0.5);

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

            Point3d[] hits = Intersection.MeshLine(_session._workingMesh, line, out _);
            if (hits is { Length: > 0 })
            {
                // GetFrustumLine runs near-to-far: the hit nearest line.From is nearest the camera.
                Point3d best = hits[0];
                double bestDist = best.DistanceToSquared(line.From);
                for (int i = 1; i < hits.Length; i++)
                {
                    double d = hits[i].DistanceToSquared(line.From);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = hits[i];
                    }
                }

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

/// <summary>
/// Incremental normal updates for the sculpt working mesh: a vertex→face CSR index built once per
/// session; per dab, only faces incident to moved vertices get fresh face normals and only vertices
/// of those faces get re-averaged vertex normals — full-mesh ComputeNormals would dominate the
/// per-dab cost on large terrains.
/// </summary>
internal sealed class SculptNormalPatcher
{
    private readonly int[] _faces;
    private readonly int[] _vertexFaceOffsets;
    private readonly int[] _vertexFaceIndices;
    private readonly int[] _faceStamps;
    private readonly int[] _vertexStamps;
    private readonly List<int> _touchedFaces = new();
    private readonly List<int> _touchedVertices = new();
    private int _stamp;

    public SculptNormalPatcher(int vertexCount, int[] faces, int faceCount)
    {
        _faces = faces;
        _faceStamps = new int[faceCount];
        _vertexStamps = new int[vertexCount];

        var counts = new int[vertexCount];
        for (int f = 0; f < faceCount; f++)
        {
            counts[faces[f * 3]]++;
            counts[faces[f * 3 + 1]]++;
            counts[faces[f * 3 + 2]]++;
        }

        _vertexFaceOffsets = new int[vertexCount + 1];
        for (int i = 0; i < vertexCount; i++)
            _vertexFaceOffsets[i + 1] = _vertexFaceOffsets[i] + counts[i];

        _vertexFaceIndices = new int[_vertexFaceOffsets[vertexCount]];
        var cursors = new int[vertexCount];
        Array.Copy(_vertexFaceOffsets, cursors, vertexCount);
        for (int f = 0; f < faceCount; f++)
        {
            _vertexFaceIndices[cursors[_faces[f * 3]]++] = f;
            _vertexFaceIndices[cursors[_faces[f * 3 + 1]]++] = f;
            _vertexFaceIndices[cursors[_faces[f * 3 + 2]]++] = f;
        }
    }

    public void PatchNormals(RhinoMesh mesh, IReadOnlyList<int> movedVertices)
    {
        _stamp++;
        _touchedFaces.Clear();
        _touchedVertices.Clear();

        foreach (int v in movedVertices)
        {
            for (int k = _vertexFaceOffsets[v]; k < _vertexFaceOffsets[v + 1]; k++)
            {
                int f = _vertexFaceIndices[k];
                if (_faceStamps[f] == _stamp)
                    continue;

                _faceStamps[f] = _stamp;
                _touchedFaces.Add(f);
            }
        }

        foreach (int f in _touchedFaces)
        {
            var a = mesh.Vertices[_faces[f * 3]];
            var b = mesh.Vertices[_faces[f * 3 + 1]];
            var c = mesh.Vertices[_faces[f * 3 + 2]];
            var normal = Vector3d.CrossProduct(
                new Vector3d(b.X - a.X, b.Y - a.Y, b.Z - a.Z),
                new Vector3d(c.X - a.X, c.Y - a.Y, c.Z - a.Z));
            normal.Unitize();
            mesh.FaceNormals.SetFaceNormal(f, normal);

            for (int corner = 0; corner < 3; corner++)
            {
                int v = _faces[f * 3 + corner];
                if (_vertexStamps[v] == _stamp)
                    continue;

                _vertexStamps[v] = _stamp;
                _touchedVertices.Add(v);
            }
        }

        foreach (int v in _touchedVertices)
        {
            var sum = Vector3d.Zero;
            for (int k = _vertexFaceOffsets[v]; k < _vertexFaceOffsets[v + 1]; k++)
                sum += mesh.FaceNormals[_vertexFaceIndices[k]];
            sum.Unitize();
            mesh.Normals.SetNormal(v, sum);
        }
    }
}
