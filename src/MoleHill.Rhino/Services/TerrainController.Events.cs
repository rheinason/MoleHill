using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Rhino document event handlers, idle processing, and source-object scheduling/pruning/transform sync.
internal sealed partial class TerrainController
{
    private void OnAddRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.TheObject == null)
            return;

        CompletePendingObjectReplacement(e.TheObject.Document, e.ObjectId);
        ScheduleRelevantTerrains(e.TheObject.Document, e.ObjectId, GetLayerPath(e.TheObject.Document, e.TheObject.Attributes.LayerIndex));
    }

    private void OnDeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.TheObject == null)
            return;

        ScheduleRelevantTerrains(e.TheObject.Document, e.ObjectId, GetLayerPath(e.TheObject.Document, e.TheObject.Attributes.LayerIndex));
        ScheduleSourceReferencePrune(e.TheObject.Document);
    }

    private void OnReplaceRhinoObject(object? sender, RhinoReplaceObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.OldRhinoObject == null)
            return;

        string? oldLayerPath = GetLayerPath(e.Document, e.OldRhinoObject.Attributes.LayerIndex);
        string? newLayerPath = e.NewRhinoObject == null
            ? null
            : GetLayerPath(e.Document, e.NewRhinoObject.Attributes.LayerIndex);

        ScheduleRelevantTerrains(e.Document, e.OldRhinoObject.Id, oldLayerPath, newLayerPath);
        // Rhino has not assigned the replacement object's id yet. ReplaceRhinoObject is followed
        // synchronously by Delete + Add (or Delete + Undelete during undo/redo), so defer the id
        // remap until that final event provides the live replacement id.
        if (e.NewRhinoObject != null)
            QueuePendingObjectReplacement(e.Document, e.OldRhinoObject.Id);

        ScheduleSourceReferencePrune(e.Document);
    }

    private void OnUndeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.TheObject == null)
            return;

        CompletePendingObjectReplacement(e.TheObject.Document, e.ObjectId);
        ScheduleRelevantTerrains(e.TheObject.Document, e.ObjectId, GetLayerPath(e.TheObject.Document, e.TheObject.Attributes.LayerIndex));
        ScheduleSourceReferencePrune(e.TheObject.Document);
    }

    private void OnBeforeTransformObjects(object? sender, RhinoTransformObjectsEventArgs e)
    {
        if (_suppressDocEvents > 0 ||
            e.ObjectsWillBeCopied ||
            e.ObjectCount == 0 ||
            IsIdentityTransform(e.Transform) ||
            sender is not RhinoDoc doc)
        {
            return;
        }

        var transformedObjectIds = e.Objects
            .Where(obj => obj != null)
            .Select(obj => obj.Id)
            .Distinct()
            .ToList();
        if (transformedObjectIds.Count == 0)
            return;

        AdjustPlacementTransformsForUserTransform(doc, transformedObjectIds, e.Transform);

        foreach (var obj in e.Objects.Where(item => item != null))
            ScheduleRelevantTerrains(doc, obj.Id, GetLayerPath(doc, obj.Attributes.LayerIndex));
    }

    private void OnSelectObjects(object? sender, RhinoObjectSelectionEventArgs e)
    {
        if (_suppressDocEvents > 0 || !e.Selected || e.RhinoObjects.Length == 0)
            return;

        var blockedIds = e.RhinoObjects
            .Where(obj => obj != null && ShouldBlockSelection(e.Document, obj))
            .Select(obj => obj.Id)
            .Distinct()
            .ToList();

        if (blockedIds.Count == 0)
            return;

        using var _ = new EventSuppression(this);
        foreach (var objectId in blockedIds)
            e.Document.Objects.Select(objectId, false, true);

        e.Document.Views.Redraw();
    }

    private void OnModifyObjectAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.RhinoObject == null)
            return;

        if (TrySyncOwnedObjectLayer(e.Document, e.RhinoObject.Id, e.NewAttributes.LayerIndex))
            return;

        string? oldLayerPath = GetLayerPath(e.Document, e.OldAttributes.LayerIndex);
        string? newLayerPath = GetLayerPath(e.Document, e.NewAttributes.LayerIndex);
        if (string.Equals(oldLayerPath, newLayerPath, StringComparison.OrdinalIgnoreCase))
            return;

        ScheduleTerrainsForLayerChanges(e.Document, oldLayerPath, newLayerPath);
    }

    private void OnLayerTableEvent(object? sender, LayerTableEventArgs e)
    {
        if (_suppressDocEvents > 0)
            return;

        // Render materials and layer-derived colours are resolved when the provider is queried, so a
        // layer edit must invalidate render primitives even when it does not affect terrain inputs.
        InvalidateDocumentRenderMeshes(e.Document);

        ScheduleTerrainsForLayerChanges(
            e.Document,
            e.OldState?.FullPath,
            e.NewState?.FullPath);
    }

    private void OnCloseDocument(object? sender, DocumentEventArgs e)
    {
        ClearDocumentState(e.Document.RuntimeSerialNumber);
        RaiseStateChanged();
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        if (_pendingSourceReferencePrunes.Count > 0)
        {
            foreach (var docSerial in _pendingSourceReferencePrunes.ToList())
            {
                _pendingSourceReferencePrunes.Remove(docSerial);
                var pruneDoc = RhinoDoc.FromRuntimeSerialNumber(docSerial);
                if (pruneDoc != null)
                    PruneDeadSourceReferences(pruneDoc);
            }
        }

        if (_pendingDocumentSaves.Count > 0)
        {
            var saveNow = DateTime.UtcNow;
            foreach (uint docSerial in _pendingDocumentSaves
                         .Where(item => item.Value <= saveNow)
                         .Select(item => item.Key)
                         .ToList())
            {
                _pendingDocumentSaves.Remove(docSerial);
                var saveDoc = RhinoDoc.FromRuntimeSerialNumber(docSerial);
                if (saveDoc == null || !_states.TryGetValue(docSerial, out var saveState))
                    continue;

                Save(saveDoc, saveState);
            }
        }

        ProcessPendingBlockAttributeKeyRepairs();
        PruneCompletedRetiredWorkers();
        ProcessBuildProgressUpdates();

        if (TryCompleteFinishedBuild())
            return;

        TryDispatchPendingBuild();
    }

    /// <summary>
    /// Picks the oldest due rebuild request and starts it. Called from <see cref="OnIdle"/>, and also
    /// posted directly when an edit is due immediately - otherwise an undelayed request still waits for
    /// whenever Rhino next raises Idle, measured at 430 ms on an edit whose debounce had resolved to
    /// zero. Posting rather than dispatching inline keeps this out of the middle of the Rhino document
    /// event that produced the edit.
    /// </summary>
    private void TryDispatchPendingBuild()
    {
        if (_pendingRebuilds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        if (TerrainLatencyTrace.IsEnabled)
            MarkDueRequestsForLatencyTrace(now);

        var nextItem = _pendingRebuilds
            .Where(item => item.Value.DueAtUtc <= now)
            .OrderBy(item => item.Value.DueAtUtc)
            .ThenBy(item => item.Key.mode == TerrainBuildMode.Preview ? 0 : 1)
            .Select(item => ((uint docSerial, Guid terrainId, TerrainBuildMode mode)?)item.Key)
            .FirstOrDefault();

        if (!nextItem.HasValue)
            return;

        var key = nextItem.Value;
        if (ShouldDeferBuildForSculpt(key.terrainId))
        {
            RecordDispatchBlocked(key, "sculpt stroke active");
            return; // an active sculpt stroke owns the preview mesh; dispatch between strokes instead
        }

        var doc = RhinoDoc.FromRuntimeSerialNumber(key.docSerial);
        if (doc == null)
        {
            RemoveRebuildState(key.docSerial, key.terrainId);
            return;
        }

        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == key.terrainId);
        if (terrain == null)
        {
            RemoveRebuildState(key.docSerial, key.terrainId);
            return;
        }

        var rebuildState = GetRebuildState(key.docSerial, key.terrainId);
        if (rebuildState.IsBuilding)
        {
            // Head-of-queue blocking: this due request belongs to a terrain that is still building, and
            // the dispatcher returns without considering any other eligible terrain (plan R06).
            RecordDispatchBlocked(key, $"terrain busy with #{rebuildState.RunningVersion:N0}");
            return;
        }

        long skippedVersion = key.mode == TerrainBuildMode.Preview
            ? rebuildState.SkippedPreviewVersion
            : rebuildState.SkippedFinalVersion;
        if (_pendingRebuilds.TryGetValue(key, out var request) && skippedVersion >= request.Version)
        {
            _pendingRebuilds.Remove(key);
            return;
        }

        _pendingRebuilds.Remove(key);
        rebuildState.LastDispatchUtc = DateTime.UtcNow;
        _latencyDueMarked.Remove((key.docSerial, key.terrainId, key.mode, rebuildState.RequestedVersion));
        _latencyBlockedReasons.RemoveWhere(item =>
            item.docSerial == key.docSerial && item.terrainId == key.terrainId && item.mode == key.mode);
        TerrainLatencyTrace.Record(
            key.docSerial,
            key.terrainId,
            rebuildState.RequestedVersion,
            rebuildState.BuildGeneration,
            key.mode,
            TerrainLatencyPhase.Dispatch,
            $"{_pendingRebuilds.Count:N0} still pending");
        StartBackgroundBuild(doc, state, terrain, key.mode, rebuildState.RequestedVersion);
    }

    /// <summary>
    /// Records the moment a pending request stops waiting out its debounce, so the report can separate
    /// the configured debounce from time spent due-but-undispatched.
    /// </summary>
    private void MarkDueRequestsForLatencyTrace(DateTime now)
    {
        foreach (var entry in _pendingRebuilds)
        {
            if (entry.Value.DueAtUtc > now)
                continue;

            var marker = (entry.Key.docSerial, entry.Key.terrainId, entry.Key.mode, entry.Value.Version);
            if (!_latencyDueMarked.Add(marker))
                continue;

            TerrainLatencyTrace.Record(
                entry.Key.docSerial,
                entry.Key.terrainId,
                entry.Value.Version,
                GetRebuildState(entry.Key.docSerial, entry.Key.terrainId).BuildGeneration,
                entry.Key.mode,
                TerrainLatencyPhase.Due);
        }
    }

    private void RecordDispatchBlocked((uint docSerial, Guid terrainId, TerrainBuildMode mode) key, string reason)
    {
        if (!TerrainLatencyTrace.IsEnabled || !_pendingRebuilds.TryGetValue(key, out var request))
            return;

        // Idle fires continuously while a request is blocked. Record the reason once per request, or
        // the ring buffer fills with thousands of identical events during a single slow build.
        if (!_latencyBlockedReasons.Add((key.docSerial, key.terrainId, key.mode, request.Version, reason)))
            return;

        TerrainLatencyTrace.Record(
            key.docSerial,
            key.terrainId,
            request.Version,
            GetRebuildState(key.docSerial, key.terrainId).BuildGeneration,
            key.mode,
            TerrainLatencyPhase.DispatchBlocked,
            reason);
    }

    private void OnUnitsChangedWithScaling(object? sender, UnitsChangedWithScalingEventArgs e)
    {
        RhinoDoc? doc = e.Document;
        if (doc == null || _suppressDocEvents > 0 ||
            !double.IsFinite(e.Scale) || e.Scale <= 0.0 || Math.Abs(e.Scale - 1.0) <= 1e-15)
        {
            return;
        }

        DocumentState state = GetState(doc);
        if (state.LoadFailed || state.Terrains.Count == 0)
            return;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Scale MoleHill Terrain Units");
        TerrainUnitScaler.Scale(state.Terrains, e.Scale);
        ClearRuntimeCaches(doc.RuntimeSerialNumber);
        ClearRebuildStates(doc.RuntimeSerialNumber);
        Save(doc, state, raiseStateChanged: false);

        foreach (TerrainDefinition terrain in state.Terrains.Where(item => item.LiveUpdateEnabled))
            ScheduleRebuild(doc, terrain.TerrainId, notify: false);

        RhinoApp.WriteLine(
            $"[MoleHill] Scaled {state.Terrains.Count:N0} terrain definition(s) by {e.Scale:G12} with the document units.");
        RaiseStateChanged();
    }

    private void OnDocumentPropertiesChanged(object? sender, DocumentEventArgs e)
    {
        if (_suppressDocEvents == 0)
            RaiseStateChanged();
    }

    private void ProcessBuildProgressUpdates()
    {
        bool changed = false;
        foreach (var entry in _rebuildStates)
        {
            TerrainRebuildState rebuildState = entry.Value;
            QueuedBuildProgress? latest = null;
            while (rebuildState.ProgressUpdates.TryDequeue(out QueuedBuildProgress? update))
            {
                if (update.Generation != rebuildState.BuildGeneration ||
                    update.Version != rebuildState.RunningVersion)
                {
                    continue;
                }

                latest = update;
            }

            if (latest == null)
                continue;

            RhinoDoc? doc = RhinoDoc.FromRuntimeSerialNumber(entry.Key.docSerial);
            TerrainDefinition? terrain = doc == null
                ? null
                : GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == entry.Key.terrainId);
            if (terrain == null)
                continue;

            terrain.LastBuildMessage = $"{latest.Mode} #{latest.Version:N0}: {latest.Progress.Format()}";
            changed = true;
        }

        if (changed)
            RaiseStateChanged();
    }

    private void ProcessPendingBlockAttributeKeyRepairs()
    {
        if (_pendingBlockAttributeKeyRepairs.Count == 0)
            return;

        foreach (uint docSerial in _pendingBlockAttributeKeyRepairs.Keys.ToList())
        {
            if (RhinoDoc.FromRuntimeSerialNumber(docSerial) == null)
                _pendingBlockAttributeKeyRepairs.Remove(docSerial);
        }

        RhinoDoc? activeDoc = RhinoDoc.ActiveDoc;
        if (activeDoc == null ||
            !_pendingBlockAttributeKeyRepairs.TryGetValue(activeDoc.RuntimeSerialNumber, out var pendingIds) ||
            pendingIds.Count == 0)
        {
            return;
        }

        var targets = pendingIds
            .Where(id => id != Guid.Empty && activeDoc.Objects.FindId(id) is InstanceObject)
            .Distinct()
            .ToList();
        _pendingBlockAttributeKeyRepairs.Remove(activeDoc.RuntimeSerialNumber);

        if (targets.Count == 0)
            return;

        var missingBeforeRepair = targets
            .Where(id => HasMissingBlockAttributeKeys(activeDoc, new[] { id }))
            .ToList();
        if (missingBeforeRepair.Count == 0)
            return;

        EmulateAddMissingBlockAttributeKeys(activeDoc, missingBeforeRepair);

        var stillMissing = missingBeforeRepair
            .Where(id => HasMissingBlockAttributeKeys(activeDoc, new[] { id }))
            .ToList();
        if (stillMissing.Count > 0)
            RhinoApp.WriteLine($"MoleHill: block attribute key repair left {stillMissing.Count} annotation block(s) with missing blank keys.");
    }

    /// <summary>
    /// Publishes a finished build without waiting for the next <see cref="RhinoApp.Idle"/>. Posted from
    /// the worker continuation; the idle poll remains the backstop, so this is an accelerator and never
    /// the only route. Re-entrancy is guarded because the completion path saves the document and
    /// redraws, either of which may pump messages.
    /// </summary>
    private void PumpFinishedBuilds()
    {
        if (_isPumpingFinishedBuilds)
            return;

        _isPumpingFinishedBuilds = true;
        try
        {
            ProcessBuildProgressUpdates();
            if (TryCompleteFinishedBuild())
            {
                // A build finishing is exactly when the next one becomes dispatchable, and during a
                // gesture there is always a newer sample waiting. Leaving it for the next Idle put a
                // poll interval between every frame.
                TryDispatchPendingBuild();
            }
        }
        finally
        {
            _isPumpingFinishedBuilds = false;
        }
    }

    private bool TryCompleteFinishedBuild()
    {
        foreach (var entry in _rebuildStates.ToList())
        {
            var rebuildState = entry.Value;
            Task<BackgroundBuildResult>? workerTask = rebuildState.WorkerTask;
            if (workerTask == null || !workerTask.IsCompleted)
                continue;

            rebuildState.WorkerTask = null;
            rebuildState.WorkerCancellation?.Dispose();
            rebuildState.WorkerCancellation = null;

            var doc = RhinoDoc.FromRuntimeSerialNumber(entry.Key.docSerial);
            if (doc == null)
            {
                RemoveRebuildState(entry.Key.docSerial, entry.Key.terrainId);
                return true;
            }

            var state = GetState(doc);
            var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == entry.Key.terrainId);
            if (terrain == null)
            {
                RemoveRebuildState(entry.Key.docSerial, entry.Key.terrainId);
                return true;
            }

            BackgroundBuildResult finished = workerTask.GetAwaiter().GetResult();
            TerrainLatencyTrace.Record(
                entry.Key.docSerial,
                entry.Key.terrainId,
                finished.Version,
                finished.Generation,
                finished.Mode,
                TerrainLatencyPhase.CompletionDispatch);
            CompleteBackgroundBuild(doc, state, terrain, rebuildState, finished);
            return true;
        }

        return false;
    }

    private void ScheduleRelevantTerrains(RhinoDoc doc, Guid objectId, params string?[] layerPaths)
    {
        var state = GetState(doc);
        foreach (var terrain in state.Terrains)
        {
            if (!terrain.LiveUpdateEnabled)
                continue;

            if (terrain.OutputObjectIds.Contains(objectId) ||
                terrain.ZoneObjectIds.Contains(objectId) ||
                terrain.AuxiliaryObjectIds.Contains(objectId) ||
                terrain.MarkerObjectIds.Contains(objectId))
            {
                continue;
            }

            bool objectMatch = terrain.EnumerateSourceSets().Any(source => source.ObjectIds.Contains(objectId));
            bool layerMatch = layerPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Any(path => terrain.EnumerateSourceSets().Any(source => source.LayerPaths.Contains(path!, StringComparer.OrdinalIgnoreCase)));

            if (objectMatch || layerMatch)
                ScheduleRebuild(doc, terrain.TerrainId);
        }
    }

    private void ScheduleTerrainsForLayerChanges(RhinoDoc doc, params string?[] layerPaths)
    {
        var relevantLayerPaths = layerPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (relevantLayerPaths.Count == 0)
            return;

        var state = GetState(doc);
        foreach (var terrain in state.Terrains)
        {
            if (!terrain.LiveUpdateEnabled)
                continue;

            bool layerMatch = relevantLayerPaths
                .Any(path => terrain.EnumerateSourceSets().Any(source => source.LayerPaths.Contains(path!, StringComparer.OrdinalIgnoreCase)));
            if (layerMatch)
                ScheduleRebuild(doc, terrain.TerrainId);
        }
    }

    private static string? GetLayerPath(RhinoDoc doc, int layerIndex)
    {
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return null;

        return doc.Layers[layerIndex].FullPath;
    }

    private IReadOnlyList<Guid> GetSelectedObjectIds(RhinoDoc doc, ObjectType objectFilter)
    {
        return doc.Objects
            .GetSelectedObjects(false, false)
            .Where(obj => MatchesObjectFilter(obj, objectFilter))
            .Select(obj => obj.Id)
            .Distinct()
            .ToList();
    }

    private void SelectSourceObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        using var _ = new EventSuppression(this);
        doc.Objects.UnselectAll();
        foreach (var objectId in objectIds)
            doc.Objects.Select(objectId, true, true);

        doc.Views.Redraw();
    }

    private void PruneDeadSourceReferences(RhinoDoc doc)
    {
        var state = GetState(doc);
        bool changed = false;
        foreach (var terrain in state.Terrains)
        {
            foreach (var sourceSet in terrain.EnumerateSourceSets())
                changed |= sourceSet.RetainObjects(id => IsLiveSourceObject(doc, id));

            foreach (var definition in terrain.Objects)
                changed |= definition.PlacementStates.RemoveAll(state => !IsLiveSourceObject(doc, state.ObjectId)) > 0;
        }

        if (changed)
            Save(doc, state);
    }

    private void ScheduleSourceReferencePrune(RhinoDoc doc)
    {
        _pendingSourceReferencePrunes.Add(doc.RuntimeSerialNumber);
    }

    private void ReplaceSourceObjectReferences(RhinoDoc doc, Guid oldObjectId, Guid newObjectId)
    {
        if (oldObjectId == Guid.Empty || newObjectId == Guid.Empty || oldObjectId == newObjectId)
            return;

        var state = GetState(doc);
        bool changed = false;
        foreach (var terrain in state.Terrains)
        {
            foreach (var sourceSet in terrain.EnumerateSourceSets())
                changed |= sourceSet.ReplaceObject(oldObjectId, newObjectId);

            changed |= ReplaceTrackedObjectId(terrain.OutputObjectIds, oldObjectId, newObjectId);
            changed |= ReplaceTrackedObjectId(terrain.ZoneObjectIds, oldObjectId, newObjectId);
            changed |= ReplaceTrackedObjectId(terrain.AuxiliaryObjectIds, oldObjectId, newObjectId);
            changed |= ReplaceTrackedObjectId(terrain.MarkerObjectIds, oldObjectId, newObjectId);
            changed |= ReplaceTrackedObjectId(terrain.BakedObjectIds, oldObjectId, newObjectId);

            foreach (var definition in terrain.Objects)
            {
                foreach (var placementState in definition.PlacementStates)
                    changed |= placementState.ReplaceObject(oldObjectId, newObjectId);
            }
        }

        if (changed)
            Save(doc, state);
    }

    private void QueuePendingObjectReplacement(RhinoDoc doc, Guid oldObjectId)
    {
        if (oldObjectId == Guid.Empty)
            return;

        uint docSerial = doc.RuntimeSerialNumber;
        if (!_pendingObjectReplacements.TryGetValue(docSerial, out Queue<Guid>? pending))
        {
            pending = new Queue<Guid>();
            _pendingObjectReplacements[docSerial] = pending;
        }

        pending.Enqueue(oldObjectId);
    }

    private void CompletePendingObjectReplacement(RhinoDoc doc, Guid newObjectId)
    {
        if (newObjectId == Guid.Empty ||
            !_pendingObjectReplacements.TryGetValue(doc.RuntimeSerialNumber, out Queue<Guid>? pending) ||
            pending.Count == 0)
        {
            return;
        }

        Guid oldObjectId = pending.Dequeue();
        if (pending.Count == 0)
            _pendingObjectReplacements.Remove(doc.RuntimeSerialNumber);

        ReplaceSourceObjectReferences(doc, oldObjectId, newObjectId);
    }

    private static bool ReplaceTrackedObjectId(List<Guid> objectIds, Guid oldObjectId, Guid newObjectId)
    {
        int index = objectIds.IndexOf(oldObjectId);
        if (index < 0)
            return false;

        if (objectIds.Contains(newObjectId))
            objectIds.RemoveAt(index);
        else
            objectIds[index] = newObjectId;

        return true;
    }

    private bool AdjustPlacementTransformsForUserTransform(RhinoDoc doc, IEnumerable<Guid> objectIds, Transform userTransform)
    {
        if (!TryGetInverse(userTransform, out Transform inverseUserTransform))
            return false;

        var transformedSet = objectIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (transformedSet.Count == 0)
            return false;

        var state = GetState(doc);
        bool changed = false;
        foreach (var terrain in state.Terrains)
        {
            foreach (var definition in terrain.Objects)
            {
                foreach (var placementState in definition.PlacementStates)
                {
                    if (!transformedSet.Contains(placementState.ObjectId))
                        continue;

                    Transform appliedTransform = placementState.GetLastAppliedTransform();
                    if (IsIdentityTransform(appliedTransform))
                        continue;

                    Transform updatedAppliedTransform = userTransform * appliedTransform * inverseUserTransform;
                    placementState.SetLastAppliedTransform(updatedAppliedTransform);
                    changed = true;
                }
            }
        }

        if (changed)
            Save(doc, state, raiseStateChanged: false);

        return changed;
    }

    private static bool MatchesObjectFilter(RhinoObject obj, ObjectType objectFilter)
    {
        return objectFilter == 0 || (obj.ObjectType & objectFilter) != 0;
    }

    private static bool IsLiveSourceObject(RhinoDoc doc, Guid objectId, ObjectType objectFilter = 0)
    {
        if (objectId == Guid.Empty)
            return false;

        var obj = doc.Objects.FindId(objectId);
        if (obj == null || obj.IsDeleted)
            return false;

        return objectFilter == 0 || MatchesObjectFilter(obj, objectFilter);
    }

    // Modifier creation is driven by the type registry (one descriptor per modifier) so adding a
    // modifier no longer needs a case here. See MoleHill.Rhino/Registry.

    private sealed class EventSuppression : IDisposable
    {
        private readonly TerrainController _controller;

        public EventSuppression(TerrainController controller)
        {
            _controller = controller;
            _controller._suppressDocEvents++;
        }

        public void Dispose()
        {
            _controller._suppressDocEvents--;
        }
    }
}
