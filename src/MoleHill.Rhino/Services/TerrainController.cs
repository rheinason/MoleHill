using System.Security.Cryptography;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.UI;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.Runtime;
using Rhino.UI;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainController
{
    private const string OutputOwnerKey = "MoleHillTerrainId";
    private const string OutputBaseMaterialNameKey = "MoleHillBaseMaterialName";
    private const string OutputKindKey = "MoleHillOutputKind";
    private const string OutputAnalysisIdKey = "MoleHillAnalysisId";
    private const string OutputDisplayMaterialPrefix = "__MoleHillDisplay__";
    private const int StageTimingDiagnosticThresholdMs = 250;
    private const int MinorTimingDiagnosticThresholdMs = 100;
    private const int TotalTimingDiagnosticThresholdMs = 750;
    private const int FinalDebounceMs = 500;
    private const int LiveEditSaveDebounceMs = 400;
    private const double PreviewWarningThresholdSeconds = 1.5;
    private const double FinalWarningThresholdSeconds = 5.0;
    private const int PreviewWarningFaceThreshold = 20_000;
    private const int FinalWarningFaceThreshold = 40_000;
    private static readonly TimeSpan ShutdownWorkerDrainTimeout = TimeSpan.FromSeconds(2);
    private const string AddMissingBlockAttributeKeysCommand = "_AddMissingBlockAttributeKeys _Enter";
    private const string AddMissingBlockAttributeKeysAllInstancesCommand = "_AddMissingBlockAttributeKeys _AllBlockInstances=_Yes _Enter";
    private static readonly BindingFlags InternalUserStringSetterFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo? InternalSetUserStringMethod = typeof(CommonObject).GetMethod("_SetUserString", InternalUserStringSetterFlags);
    private readonly TerrainDocumentStore _documentStore = new();
    private readonly TerrainBuildService _buildService = new();
    private readonly TerrainDisplayConduit _displayConduit = new();
    private readonly Dictionary<uint, DocumentState> _states = new();
    private readonly Dictionary<(uint docSerial, Guid terrainId, TerrainBuildMode mode), PendingBuildRequest> _pendingRebuilds = new();
    private readonly Dictionary<uint, DateTime> _pendingDocumentSaves = new();
    private readonly HashSet<uint> _pendingSourceReferencePrunes = new();
    private readonly Dictionary<uint, Queue<Guid>> _pendingObjectReplacements = new();
    private readonly Dictionary<uint, HashSet<Guid>> _pendingBlockAttributeKeyRepairs = new();
    private readonly Dictionary<(uint docSerial, Guid terrainId), TerrainRuntimeCache> _runtimeCaches = new();
    private readonly Dictionary<(uint docSerial, Guid terrainId), TerrainRebuildState> _rebuildStates = new();
    private bool _initialized;
    private int _suppressDocEvents;

    private readonly record struct OutputSyncMetrics(
        int DeletedObjectCount,
        int OutputCount,
        int ZoneCount,
        int AuxiliaryCount,
        int MarkerCount)
    {
        public int AddedObjectCount => OutputCount + ZoneCount + AuxiliaryCount + MarkerCount;

        public string ToDetail()
        {
            return $"{DeletedObjectCount:N0} old -> {AddedObjectCount:N0} new objects " +
                   $"({OutputCount:N0} terrain, {ZoneCount:N0} zones, {AuxiliaryCount:N0} auxiliary, {MarkerCount:N0} markers)";
        }
    }

    public readonly record struct BakedLayerEnsureResult(int CreatedCount, int RefreshedCount, int SkippedCount)
    {
        public int TotalChanged => CreatedCount + RefreshedCount;
    }

    private readonly record struct PendingBuildRequest(DateTime DueAtUtc, long Version);

    private readonly record struct BackgroundBuildResult(
        long Version,
        long Generation,
        TerrainBuildMode Mode,
        TerrainDefinition SnapshotTerrain,
        TerrainBuildResult? Build,
        TerrainRuntimeCache WorkerCache,
        TimeSpan SnapshotElapsed,
        TimeSpan WorkerCacheCloneElapsed,
        TimeSpan BuildElapsed,
        bool WasCanceled,
        Exception? Error);

    private sealed class TerrainRebuildState
    {
        public long RequestedVersion { get; set; }

        public long AppliedVersion { get; set; }

        public long AppliedPreviewVersion { get; set; }

        public long RunningVersion { get; set; }

        public long BuildGeneration { get; set; }

        public TerrainBuildMode RunningMode { get; set; } = TerrainBuildMode.Final;

        public bool IsBuilding { get; set; }

        public bool CancelRequested { get; set; }

        public long SkippedPreviewVersion { get; set; }

        public long SkippedFinalVersion { get; set; }

        public Task<BackgroundBuildResult>? WorkerTask { get; set; }

        public CancellationTokenSource? WorkerCancellation { get; set; }

        public List<Task> RetiredWorkers { get; } = new();

        public ConcurrentQueue<QueuedBuildProgress> ProgressUpdates { get; } = new();
    }

    private sealed record QueuedBuildProgress(
        long Version,
        long Generation,
        TerrainBuildMode Mode,
        TerrainBuildProgress Progress);

    public static TerrainController Instance { get; } = new();

    public event EventHandler? StateChanged;

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        RhinoDoc.AddRhinoObject += OnAddRhinoObject;
        RhinoDoc.DeleteRhinoObject += OnDeleteRhinoObject;
        RhinoDoc.ReplaceRhinoObject += OnReplaceRhinoObject;
        RhinoDoc.UndeleteRhinoObject += OnUndeleteRhinoObject;
        RhinoDoc.BeforeTransformObjects += OnBeforeTransformObjects;
        RhinoDoc.SelectObjects += OnSelectObjects;
        RhinoDoc.ModifyObjectAttributes += OnModifyObjectAttributes;
        RhinoDoc.LayerTableEvent += OnLayerTableEvent;
        RhinoDoc.CloseDocument += OnCloseDocument;
        RhinoApp.Idle += OnIdle;
        _displayConduit.Enabled = true;
    }

    internal void Shutdown()
    {
        if (!_initialized)
            return;

        _initialized = false;
        RhinoDoc.AddRhinoObject -= OnAddRhinoObject;
        RhinoDoc.DeleteRhinoObject -= OnDeleteRhinoObject;
        RhinoDoc.ReplaceRhinoObject -= OnReplaceRhinoObject;
        RhinoDoc.UndeleteRhinoObject -= OnUndeleteRhinoObject;
        RhinoDoc.BeforeTransformObjects -= OnBeforeTransformObjects;
        RhinoDoc.SelectObjects -= OnSelectObjects;
        RhinoDoc.ModifyObjectAttributes -= OnModifyObjectAttributes;
        RhinoDoc.LayerTableEvent -= OnLayerTableEvent;
        RhinoDoc.CloseDocument -= OnCloseDocument;
        RhinoApp.Idle -= OnIdle;
        _displayConduit.Enabled = false;

        var workers = new List<(CancellationTokenSource? Cancellation, Task? Task)>();
        foreach (TerrainRebuildState state in _rebuildStates.Values)
        {
            workers.Add((state.WorkerCancellation, state.WorkerTask));
            foreach (Task retiredWorker in state.RetiredWorkers)
                workers.Add((null, retiredWorker));
        }

        bool workersStopped = TerrainWorkerCancellation.CancelAndWaitForWorkers(
            workers,
            ShutdownWorkerDrainTimeout);

        if (workersStopped)
        {
            foreach (TerrainRuntimeCache cache in _runtimeCaches.Values)
                cache.Clear();
        }

        _states.Clear();
        _pendingRebuilds.Clear();
        _pendingDocumentSaves.Clear();
        _pendingSourceReferencePrunes.Clear();
        _pendingBlockAttributeKeyRepairs.Clear();
        _runtimeCaches.Clear();
        _rebuildStates.Clear();
    }

    public IReadOnlyList<TerrainDefinition> GetTerrains(RhinoDoc doc) => GetState(doc).Terrains;

    /// <summary>
    /// True when this document's stored terrain JSON could not be read. The panel should surface a
    /// "Reset terrain data" action while this is set, since <see cref="GetTerrains"/> will otherwise
    /// silently look like "no terrains" and any save is refused (see <see cref="ResetTerrainDataAfterFailedLoad"/>).
    /// </summary>
    public bool IsTerrainDataUnreadable(RhinoDoc doc) => GetState(doc).LoadFailed;

    public void ReloadDocumentState(RhinoDoc doc)
    {
        ClearDocumentState(doc.RuntimeSerialNumber);
        doc.Views.Redraw();
        RaiseStateChanged();
    }

    internal IReadOnlyList<TerrainPreviewView> GetPreviewViews(RhinoDoc doc)
    {
        var views = new List<TerrainPreviewView>();
        foreach (var terrain in GetState(doc).Terrains)
        {
            var runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId);
            if (runtimeCache.DisplayState == null)
                continue;

            views.Add(new TerrainPreviewView
            {
                Terrain = terrain,
                DisplayState = runtimeCache.DisplayState
            });
        }

        return views;
    }

    public Guid? GetSelectedTerrainId(RhinoDoc doc) => GetState(doc).SelectedTerrainId;

    public void SetSelectedTerrain(RhinoDoc doc, Guid? terrainId)
    {
        var state = GetState(doc);
        state.SelectedTerrainId = terrainId;
        RaiseStateChanged();
    }

    public TerrainDefinition? GetSelectedTerrain(RhinoDoc doc)
    {
        var state = GetState(doc);
        Guid? selectedId = state.SelectedTerrainId ?? state.Terrains.FirstOrDefault()?.TerrainId;
        return selectedId == null ? null : state.Terrains.FirstOrDefault(terrain => terrain.TerrainId == selectedId.Value);
    }

    public bool TryExportTerrainCaseBundle(
        RhinoDoc doc,
        Guid terrainId,
        out string? archivePath,
        out string? coreTestCode,
        out string? errorMessage)
    {
        archivePath = null;
        coreTestCode = null;
        errorMessage = null;

        TerrainDefinition? terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
        {
            errorMessage = "Select a terrain before exporting a case bundle.";
            return false;
        }

        try
        {
            TerrainBuildSnapshot snapshot = TerrainBuildSnapshotBuilder.Create(doc, terrain);
            TerrainDisplayState? displayState = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId).DisplayState?.Clone();
            TerrainCaseBundleExportResult export = TerrainCaseBundleExporter.Export(doc, terrain, snapshot, displayState);
            archivePath = export.ArchivePath;
            coreTestCode = export.CoreTestCode;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Could not export the case bundle: {ex.Message}";
            return false;
        }
    }

    public TerrainDefinition CreateTerrain(RhinoDoc doc, bool seedFromSelection)
    {
        var state = GetState(doc);
        var terrain = new TerrainDefinition
        {
            Name = NextTerrainName(state.Terrains),
            GlobalTolerance = TerrainTolerancePolicy.DefaultDetailSize(doc.ModelUnitSystem),
            TerrainLayerPath = TerrainDefinition.DefaultTerrainLayerPath,
            AuxiliaryLayerPath = TerrainDefinition.DefaultAuxiliaryLayerPath,
            AnnotationLayerPath = TerrainDefinition.DefaultAnnotationLayerPath
        };
        terrain.EnsureBaseModifier();

        if (seedFromSelection && terrain.Modifiers[0] is TriangulateModifierDefinition triangulate)
        {
            triangulate.Points.ReplaceObjects(GetSelectedPointObjectIds(doc));
            triangulate.Breaklines.ReplaceObjects(GetSelectedCurveObjectIds(doc));
        }

        state.Terrains.Add(terrain);
        state.SelectedTerrainId = terrain.TerrainId;
        Save(doc, state);

        if (terrain.LiveUpdateEnabled)
            ScheduleRebuild(doc, terrain.TerrainId);
        else
            RaiseStateChanged();

        return terrain;
    }

    public void DeleteTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        RestoreTerrainObjectPlacements(doc, terrain);
        DeleteOwnedObjects(doc, terrain);
        PurgeOrphanedOwnedObjects(doc, terrain);
        RemoveRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        RemoveRebuildState(doc.RuntimeSerialNumber, terrainId);
        state.Terrains.Remove(terrain);
        if (state.SelectedTerrainId == terrainId)
            state.SelectedTerrainId = state.Terrains.FirstOrDefault()?.TerrainId;

        Save(doc, state);
        doc.Views.Redraw();
    }

    public void ConvertToRhino(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        uint undoRecord = doc.BeginUndoRecord("Detach MoleHill Terrain");
        doc.AddCustomUndoEvent("Detach MoleHill Terrain", OnRestoreStateUndo, CaptureUndoState(state));

        BakeTerrain(doc, terrainId);
        RestoreTerrainObjectPlacements(doc, terrain);
        RemoveRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        RemoveRebuildState(doc.RuntimeSerialNumber, terrainId);
        state.Terrains.Remove(terrain);
        if (state.SelectedTerrainId == terrainId)
            state.SelectedTerrainId = state.Terrains.FirstOrDefault()?.TerrainId;

        Save(doc, state);
        doc.Views.Redraw();
        if (undoRecord > 0)
            doc.EndUndoRecord(undoRecord);
    }

    public void AddModifier(RhinoDoc doc, Guid terrainId, string modifierKind)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            var modifier = CreateModifier(modifierKind, doc.ModelUnitSystem);
            if (modifier != null)
                terrain.Modifiers.Add(modifier);
        });
    }

    public void RemoveModifier(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            var modifier = terrain.Modifiers.FirstOrDefault(item => item.Id == modifierId);
            if (modifier == null)
                return;

            if (IsPinnedBaseTriangulate(terrain, modifier))
                return;

            terrain.Modifiers.Remove(modifier);
            terrain.EnsureBaseModifier();
        });
    }

    public void MoveModifier(RhinoDoc doc, Guid terrainId, Guid modifierId, int direction)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Modifiers.FindIndex(item => item.Id == modifierId);
            if (index < 0)
                return;

            var modifier = terrain.Modifiers[index];
            if (IsPinnedBaseTriangulate(terrain, modifier))
                return;

            int minimumIndex = terrain.Modifiers.Count > 0 && terrain.Modifiers[0] is TriangulateModifierDefinition ? 1 : 0;
            int targetIndex = Math.Clamp(index + direction, minimumIndex, terrain.Modifiers.Count - 1);
            if (targetIndex == index)
                return;

            terrain.Modifiers.RemoveAt(index);
            terrain.Modifiers.Insert(targetIndex, modifier);
        });
    }

    public void DuplicateModifier(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Modifiers.FindIndex(item => item.Id == modifierId);
            if (index < 0)
                return;

            var modifier = terrain.Modifiers[index];
            if (IsPinnedBaseTriangulate(terrain, modifier))
                return;

            var clone = CloneModifier(modifier);
            if (clone == null)
                return;

            clone.Id = Guid.NewGuid();
            clone.Label += " Copy";
            terrain.Modifiers.Insert(index + 1, clone);
        });
    }

    public void DuplicateAnalysis(RhinoDoc doc, Guid terrainId, Guid analysisId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Analyses.FindIndex(item => item.Id == analysisId);
            if (index < 0)
                return;

            var clone = CloneAnalysis(terrain.Analyses[index]);
            if (clone == null)
                return;

            clone.Id = Guid.NewGuid();
            clone.Label += " Copy";
            terrain.Analyses.Insert(index + 1, clone);
        }, scheduleRebuild: false);
    }

    public void AddMarker(RhinoDoc doc, Guid terrainId, string markerKind)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            MarkerDefinition marker = Registry.MarkerTypeRegistry.Create(markerKind)
                ?? throw new InvalidOperationException($"Unknown marker kind '{markerKind}'.");

            terrain.Markers.Add(marker);
        });
    }

    public void AddObjectDefinition(RhinoDoc doc, Guid terrainId, string objectKind)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            TerrainObjectDefinition definition = Registry.ObjectTypeRegistry.Create(objectKind)
                ?? throw new InvalidOperationException($"Unknown object definition kind '{objectKind}'.");

            terrain.Objects.Insert(0, definition);
        });
    }

    public void DuplicateObjectDefinition(RhinoDoc doc, Guid terrainId, Guid objectDefinitionId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Objects.FindIndex(item => item.Id == objectDefinitionId);
            if (index < 0)
                return;

            var definition = terrain.Objects[index];
            var clone = CloneTerrainObjectDefinition(definition);
            if (clone == null)
                return;

            clone.Id = Guid.NewGuid();
            clone.Name += " Copy";
            clone.PlacementStates = new List<TerrainObjectPlacementState>();
            terrain.Objects.Insert(index + 1, clone);
        });
    }

    public void RemoveObjectDefinition(RhinoDoc doc, Guid terrainId, Guid objectDefinitionId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        var definition = terrain.Objects.FirstOrDefault(item => item.Id == objectDefinitionId);
        if (definition == null)
            return;

        RestorePlacementStates(doc, definition.PlacementStates);
        terrain.Objects.Remove(definition);
        terrain.EnsureBaseModifier();

        bool shouldScheduleRebuild = terrain.LiveUpdateEnabled;
        Save(doc, state, raiseStateChanged: !shouldScheduleRebuild);
        if (shouldScheduleRebuild)
            ScheduleRebuild(doc, terrain.TerrainId);
    }

    public void RemoveMarker(RhinoDoc doc, Guid terrainId, Guid markerId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            terrain.Markers.RemoveAll(marker => marker.Id == markerId);
        });
    }

    public void MutateTerrain(
        RhinoDoc doc,
        Guid terrainId,
        Action<TerrainDefinition> mutator,
        bool scheduleRebuild = true,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        mutator(terrain);
        terrain.EnsureBaseModifier();
        bool shouldScheduleRebuild = scheduleRebuild && terrain.LiveUpdateEnabled;
        if (deferDocumentSave)
            QueuePendingDocumentSave(doc.RuntimeSerialNumber, LiveEditSaveDebounceMs);
        else
            Save(doc, state, raiseStateChanged: !shouldScheduleRebuild && !suppressImmediateUiRefresh);

        if (shouldScheduleRebuild)
            ScheduleRebuild(doc, terrain.TerrainId, notify: !suppressImmediateUiRefresh);
        else if (deferDocumentSave)
        {
            if (!suppressImmediateUiRefresh)
                RaiseStateChanged();
        }
    }

    /// <summary>
    /// Prompts the user to drag a rectangle and sets it as the work-area boundary on the given
    /// geometry-input modifier (limiting terrain computation to that area). Returns false if cancelled.
    /// </summary>
    public bool SetModifierBoundaryRectangle(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        var rc = global::Rhino.Input.RhinoGet.GetRectangle(out global::Rhino.Geometry.Point3d[] corners);
        if (rc != global::Rhino.Commands.Result.Success || corners == null || corners.Length < 4)
            return false;

        var polyline = new global::Rhino.Geometry.Polyline(new[] { corners[0], corners[1], corners[2], corners[3], corners[0] });
        var curve = new global::Rhino.Geometry.PolylineCurve(polyline);
        Guid id = doc.Objects.AddCurve(curve);
        if (id == Guid.Empty)
            return false;

        MutateTerrain(doc, terrainId, terrain =>
        {
            if (FindGeometryInputModifier(terrain, modifierId) is { } modifier)
                modifier.Boundary.ReplaceObjects(new[] { id });
        });
        doc.Views.Redraw();
        return true;
    }

    /// <summary>Clears the work-area boundary on a geometry-input modifier, restoring the full terrain.</summary>
    public void ClearModifierBoundary(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            if (FindGeometryInputModifier(terrain, modifierId) is { } modifier)
                modifier.Boundary.ReplaceObjects(Array.Empty<Guid>());
        });
    }

    private static GeometryInputModifierDefinition? FindGeometryInputModifier(TerrainDefinition terrain, Guid modifierId)
        => terrain.Modifiers.FirstOrDefault(modifier => modifier.Id == modifierId) as GeometryInputModifierDefinition;

    public void RebuildTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        RemovePendingBuild(doc.RuntimeSerialNumber, terrainId, TerrainBuildMode.Preview);
        RemovePendingBuild(doc.RuntimeSerialNumber, terrainId, TerrainBuildMode.Final);
        long buildVersion = RequestRebuild(doc.RuntimeSerialNumber, terrainId, isImmediate: true);
        var rebuildState = GetRebuildState(doc.RuntimeSerialNumber, terrainId);
        if (rebuildState.IsBuilding)
        {
            QueuePendingBuild(doc.RuntimeSerialNumber, terrainId, TerrainBuildMode.Final, buildVersion, 0);
            terrain.LastBuildMessage = $"Queued rebuild #{buildVersion:N0}; current build will stop at the next safe checkpoint.";
            terrain.LastStructuredDiagnostics.Clear();
            RaiseStateChanged();
            return;
        }

        StartBackgroundBuild(doc, state, terrain, TerrainBuildMode.Final, buildVersion);
    }

    public void ForceResetTerrainBuild(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        RemovePendingBuild(doc.RuntimeSerialNumber, terrainId, TerrainBuildMode.Preview);
        RemovePendingBuild(doc.RuntimeSerialNumber, terrainId, TerrainBuildMode.Final);
        CancelRunningBuild(doc.RuntimeSerialNumber, terrainId);
        RemoveRuntimeCache(doc.RuntimeSerialNumber, terrainId);

        if (_rebuildStates.TryGetValue((doc.RuntimeSerialNumber, terrainId), out var rebuildState))
        {
            rebuildState.RequestedVersion = 0;
            rebuildState.AppliedVersion = 0;
            rebuildState.AppliedPreviewVersion = 0;
            rebuildState.RunningVersion = 0;
            rebuildState.SkippedPreviewVersion = 0;
            rebuildState.SkippedFinalVersion = 0;
            rebuildState.CancelRequested = false;
        }

        PurgeOrphanedOwnedObjects(doc, terrain);
        terrain.LastBuildMessage = "Build reset. Rebuild to resume terrain outputs.";
        terrain.LastStructuredDiagnostics.Clear();
        Save(doc, state);
        doc.Views.Redraw();
    }

    public TerrainDefinition DuplicateTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return null!;

        var json = System.Text.Json.JsonSerializer.Serialize(terrain, TerrainSerializer.SharedOptions);
        var clone = System.Text.Json.JsonSerializer.Deserialize<TerrainDefinition>(json, TerrainSerializer.SharedOptions)!;
        clone.TerrainId = Guid.NewGuid();
        clone.Name = terrain.Name + " Copy";
        clone.OutputObjectIds.Clear();
        clone.ZoneObjectIds.Clear();
        clone.AuxiliaryObjectIds.Clear();
        clone.MarkerObjectIds.Clear();
        clone.BakedObjectIds.Clear();
        clone.LastBuildMessage = null;
        clone.LastStructuredDiagnostics.Clear();
        clone.LastBuildUtc = null;

        foreach (var modifier in clone.Modifiers)
            modifier.Id = Guid.NewGuid();
        foreach (var marker in clone.Markers)
            marker.Id = Guid.NewGuid();
        foreach (var obj in clone.Objects)
        {
            obj.Id = Guid.NewGuid();
            obj.PlacementStates.Clear();
        }
        foreach (var zone in clone.Zones)
            zone.ZoneId = Guid.NewGuid();
        foreach (var analysis in clone.Analyses)
            analysis.Id = Guid.NewGuid();

        state.Terrains.Add(clone);
        state.SelectedTerrainId = clone.TerrainId;
        Save(doc, state);

        if (clone.LiveUpdateEnabled)
            ScheduleRebuild(doc, clone.TerrainId);

        return clone;
    }

    public void BakeTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        var runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId);
        if (runtimeCache.DisplayState?.TerrainMesh == null ||
            runtimeCache.DisplayState.IsPreview ||
            runtimeCache.DisplayState.HasDeferredOutputs)
        {
            if (!BuildTerrainSynchronously(doc, state, terrain, TerrainBuildMode.Final))
                return;
            runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId);
        }

        var displayState = runtimeCache.DisplayState;
        if (displayState == null || !terrain.IsVisible)
            return;

        using var _ = new EventSuppression(this);
        if (terrain.ReplacePreviouslyBaked && terrain.BakedObjectIds.Count > 0)
        {
            DeleteObjects(doc, terrain.BakedObjectIds);
            terrain.BakedObjectIds.Clear();
        }

        var blockAttributeRefreshIds = new List<Guid>();
        var bakedIds = new List<Guid>();
        if (terrain.ShowTerrainMesh)
        {
            Mesh? terrainMesh = displayState.ActiveAnalysisId.HasValue
                ? displayState.PreviewTerrainMesh
                : displayState.TerrainMesh;
            if (terrainMesh != null)
            {
                Guid id = AddBakedObject(doc, terrain, new GeneratedRhinoObject
                {
                    Geometry = terrainMesh.DuplicateMesh(),
                    Name = terrain.Name,
                    LayerPath = TerrainDefinition.ResolveTerrainLayerPath(terrain.TerrainLayerPath),
                    ColorArgb = terrain.TerrainColorArgb
                }, blockAttributeRefreshIds);
                if (id != Guid.Empty)
                    bakedIds.Add(id);
            }
        }

        if (terrain.ShowZoneMeshes)
        {
            foreach (var zoneObject in displayState.ZoneObjects)
            {
                Guid id = AddBakedObject(doc, terrain, TerrainRuntimeCacheCloner.CloneGeneratedObject(zoneObject), blockAttributeRefreshIds);
                if (id != Guid.Empty)
                    bakedIds.Add(id);
            }
        }

        foreach (var auxiliary in displayState.AuxiliaryObjects)
        {
            if (!TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(terrain, auxiliary))
                continue;

            Guid id = AddBakedObject(doc, terrain, TerrainRuntimeCacheCloner.CloneGeneratedObject(auxiliary), blockAttributeRefreshIds);
            if (id != Guid.Empty)
                bakedIds.Add(id);
        }

        foreach (var marker in displayState.MarkerObjects)
        {
            Guid id = AddBakedObject(doc, terrain, TerrainRuntimeCacheCloner.CloneGeneratedObject(marker), blockAttributeRefreshIds);
            if (id != Guid.Empty)
                bakedIds.Add(id);
        }

        foreach (var scatter in displayState.ScatterObjects)
        {
            Guid id = AddBakedObject(doc, terrain, TerrainRuntimeCacheCloner.CloneGeneratedObject(scatter), blockAttributeRefreshIds);
            if (id != Guid.Empty)
                bakedIds.Add(id);
        }

        terrain.BakedObjectIds.AddRange(bakedIds);
        QueuePendingBlockAttributeKeyRepair(doc, blockAttributeRefreshIds);
        Save(doc, state, raiseStateChanged: false);

        doc.Views.Redraw();
    }

    public void SetTerrainVisible(RhinoDoc doc, Guid terrainId, bool visible)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        terrain.IsVisible = visible;
        Save(doc, state);
        doc.Views.Redraw();
    }

    public void SetTerrainLocked(RhinoDoc doc, Guid terrainId, bool locked)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        terrain.IsLocked = locked;
        Save(doc, state);
        doc.Views.Redraw();
    }

    public void UntrackSelectedBakedObjects(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null || terrain.BakedObjectIds.Count == 0)
            return;

        var selectedIds = doc.Objects
            .GetSelectedObjects(false, false)
            .Select(obj => obj.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (selectedIds.Count == 0)
            return;

        int beforeCount = terrain.BakedObjectIds.Count;
        terrain.BakedObjectIds = terrain.BakedObjectIds
            .Where(id => id != Guid.Empty && doc.Objects.FindId(id) != null && !selectedIds.Contains(id))
            .Distinct()
            .ToList();
        if (terrain.BakedObjectIds.Count == beforeCount)
            return;

        Save(doc, state);
    }

    public void UntrackAllBakedObjects(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null || terrain.BakedObjectIds.Count == 0)
            return;

        terrain.BakedObjectIds.Clear();
        Save(doc, state);
    }

    public void RefreshTerrainDisplay(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        UpdateRuntimePreview(doc, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrainId));
        ApplyDisplayState(doc, terrain);
        doc.Views.Redraw();
    }

    public IReadOnlyList<Guid> GetSelectedPointObjectIds(RhinoDoc doc)
    {
        return GetSelectedObjectIds(doc, ObjectType.Point | ObjectType.PointSet);
    }

    public IReadOnlyList<Guid> GetSelectedCurveObjectIds(RhinoDoc doc)
    {
        return GetSelectedObjectIds(doc, ObjectType.Curve);
    }

    public IReadOnlyList<Guid> GetSelectedZoneObjectIds(RhinoDoc doc)
    {
        return GetSelectedObjectIds(doc, ObjectType.Curve | ObjectType.Brep | ObjectType.Extrusion);
    }

    public IReadOnlyList<Guid> GetSelectedMeshObjectIds(RhinoDoc doc)
    {
        return GetSelectedObjectIds(doc, ObjectType.Mesh | ObjectType.Brep | ObjectType.Extrusion);
    }

    public IReadOnlyList<Guid>? EditSourceObjectIds(RhinoDoc doc, IEnumerable<Guid> seedIds, ObjectType objectFilter, string prompt)
    {
        var selectedIds = seedIds
            .Where(id => IsLiveSourceObject(doc, id, objectFilter))
            .Distinct()
            .ToList();
        if (selectedIds.Count == 0)
        {
            selectedIds = GetSelectedObjectIds(doc, objectFilter)
                .Where(id => IsLiveSourceObject(doc, id, objectFilter))
                .Distinct()
                .ToList();
        }

        var activeIds = new HashSet<Guid>(selectedIds);

        SelectSourceObjects(doc, activeIds);

        using var picker = new GetObject
        {
            GroupSelect = true,
            SubObjectSelect = false,
            DeselectAllBeforePostSelect = false
        };
        if (objectFilter != 0)
            picker.GeometryFilter = objectFilter;
        picker.SetCommandPrompt(prompt);
        picker.AcceptNothing(true);
        picker.EnablePostSelect(true);
        picker.EnableUnselectObjectsOnExit(false);
        picker.AlreadySelectedObjectSelect = true;
        // Force Rhino into post-select mode while preserving the seeded highlight.
        picker.EnablePreSelect(false, true);

        RhinoApp.SetFocusToMainWindow(doc);
        while (true)
        {
            var result = picker.GetMultiple(1, -1);
            if (result == global::Rhino.Input.GetResult.Cancel)
            {
                SelectSourceObjects(doc, selectedIds);
                return null;
            }

            if (result == global::Rhino.Input.GetResult.Nothing)
                return activeIds.ToList();

            if (result != global::Rhino.Input.GetResult.Object)
                return null;

            var pickedIds = Enumerable.Range(0, picker.ObjectCount)
                .Select(index => picker.Object(index)?.ObjectId ?? Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (pickedIds.Count == 0)
                continue;

            using var _ = new EventSuppression(this);
            foreach (var objectId in pickedIds)
            {
                if (!IsLiveSourceObject(doc, objectId, objectFilter))
                    continue;

                bool shouldSelect = !activeIds.Contains(objectId);
                doc.Objects.Select(objectId, shouldSelect, syncHighlight: true);
                if (shouldSelect)
                    activeIds.Add(objectId);
                else
                    activeIds.Remove(objectId);
            }

            doc.Views.Redraw();
        }
    }

    public IReadOnlyList<string> GetSelectedLayerPaths(RhinoDoc doc)
    {
        if (doc.Layers.GetSelected(out var selectedLayerIndices) && selectedLayerIndices.Count > 0)
        {
            return selectedLayerIndices
                .Where(index => index >= 0 && index < doc.Layers.Count)
                .Select(index => doc.Layers[index].FullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return doc.Objects
            .GetSelectedObjects(false, false)
            .Select(obj => GetLayerPath(doc, obj.Attributes.LayerIndex))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public BakedLayerEnsureResult EnsureTerrainOutputLayers(RhinoDoc doc, Guid terrainId)
    {
        var terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return default;

        int created = 0;
        int refreshed = 0;
        int skipped = 0;

        var outputLayerPaths = new[]
        {
            TerrainDefinition.ResolveTerrainLayerPath(terrain.TerrainLayerPath),
            TerrainDefinition.ResolveAuxiliaryLayerPath(terrain.AuxiliaryLayerPath),
            TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)
        };

        foreach (string outputLayerPath in outputLayerPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(outputLayerPath))
            {
                skipped++;
                continue;
            }

            bool existed = doc.Layers.FindByFullPath(outputLayerPath, -1) >= 0;
            int layerIndex = EnsureLayer(doc, outputLayerPath);
            if (layerIndex < 0)
            {
                skipped++;
                continue;
            }

            if (existed)
                refreshed++;
            else
                created++;
        }

        if (created > 0 || refreshed > 0)
            doc.Views.Redraw();

        return new BakedLayerEnsureResult(created, refreshed, skipped);
    }

    public string? GetModifierMeshQualityWarning(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        var terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        int modifierIndex = terrain?.Modifiers.FindIndex(item => item.Id == modifierId) ?? -1;
        if (terrain == null || modifierIndex < 0 ||
            terrain.Modifiers[modifierIndex] is not (SmoothModifierDefinition or SculptModifierDefinition))
        {
            return null;
        }

        bool hasPriorRemesh = terrain.Modifiers
            .Take(modifierIndex)
            .Any(item => item.IsEnabled && item is RemeshModifierDefinition);
        if (hasPriorRemesh)
            return null;

        var runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        Mesh? incomingMesh = FindIncomingModifierMesh(runtimeCache, terrain, modifierIndex);
        if (incomingMesh == null)
            return null;

        ExtractedMeshData meshData = RhinoGeometryConversions.GetNormalizedMeshData(incomingMesh);
        MeshRegularitySummary summary = MeshRegularityAnalyzer.Analyze(meshData.Vertices, meshData.Faces);
        bool isSparse = MeshRegularityAnalyzer.IsVerySparse(summary);
        bool hasSkinnyTriangles = MeshRegularityAnalyzer.HasVerySkinnyTriangles(summary);
        if (!isSparse && !hasSkinnyTriangles)
            return null;

        var reasons = new List<string>();
        if (isSparse)
            reasons.Add($"only {summary.FaceCount:N0} faces / coarse spacing");
        if (hasSkinnyTriangles)
            reasons.Add($"{summary.SkinnyFraction:P0} very skinny sampled triangles");

        return $"Incoming mesh has {string.Join(" and ", reasons)}. Add an enabled Remesh modifier below this card for a more even surface before applying {terrain.Modifiers[modifierIndex].Label}.";
    }

    private static Mesh? FindIncomingModifierMesh(
        TerrainRuntimeCache runtimeCache,
        TerrainDefinition terrain,
        int modifierIndex)
    {
        TerrainBuildMode preferredMode = runtimeCache.DisplayState?.IsPreview == true
            ? TerrainBuildMode.Preview
            : TerrainBuildMode.Final;
        foreach (TerrainBuildMode mode in new[] { preferredMode, preferredMode == TerrainBuildMode.Final ? TerrainBuildMode.Preview : TerrainBuildMode.Final })
        {
            for (int index = modifierIndex - 1; index >= 0; index--)
            {
                ModifierDefinition previous = terrain.Modifiers[index];
                if (!previous.IsEnabled)
                    continue;

                string stageKey = TerrainStageKey.ForMode(mode, TerrainStageKey.CreateModifier(index, previous));
                if (runtimeCache.StageEntries.TryGetValue(stageKey, out StageCacheEntry? entry) && entry.MeshOutput != null)
                    return entry.MeshOutput;
            }
        }

        return runtimeCache.DisplayState?.BaseTerrainMesh;
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

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return $"{elapsed.TotalSeconds:0.##} s";
    }

    private UndoState CaptureUndoState(DocumentState state)
    {
        return new UndoState
        {
            Json = TerrainSerializer.Serialize(state.Terrains),
            SelectedTerrainId = state.SelectedTerrainId
        };
    }

    private void RestoreUndoState(RhinoDoc doc, UndoState snapshot)
    {
        var restoredTerrains = TerrainSerializer.Deserialize(snapshot.Json, doc.ModelUnitSystem);
        var restoredState = new DocumentState
        {
            Terrains = restoredTerrains,
            SelectedTerrainId = snapshot.SelectedTerrainId ?? restoredTerrains.FirstOrDefault()?.TerrainId
        };

        _states[doc.RuntimeSerialNumber] = restoredState;
        ClearRebuildStates(doc.RuntimeSerialNumber);
        ClearRuntimeCaches(doc.RuntimeSerialNumber);
        Save(doc, restoredState);
        doc.Views.Redraw();
    }

    public void RebuildContourAnalysis(RhinoDoc doc, Guid terrainId, Guid analysisId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        var analysis = terrain.Analyses.OfType<ContourAnalysisDefinition>().FirstOrDefault(a => a.Id == analysisId);
        if (analysis == null)
            return;

        var runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        var mesh = runtimeCache.DisplayState?.TerrainMesh;
        if (mesh == null)
        {
            ScheduleRebuild(doc, terrainId);
            return;
        }

        string terrainIdString = terrainId.ToString();
        string analysisIdString = analysisId.ToString();
        var toDelete = doc.Objects
            .GetObjectList(new ObjectEnumeratorSettings
            {
                ActiveObjects = true,
                DeletedObjects = false,
                HiddenObjects = true,
                LockedObjects = true,
                NormalObjects = true,
                ReferenceObjects = false
            })
            .Where(obj =>
                obj?.Attributes?.GetUserString(OutputOwnerKey) == terrainIdString &&
                obj?.Attributes?.GetUserString(OutputAnalysisIdKey) == analysisIdString)
            .Select(obj => obj.Id)
            .ToList();

        using var _ = new EventSuppression(this);
        DeleteObjects(doc, toDelete);
        if (toDelete.Count > 0)
            terrain.AuxiliaryObjectIds.RemoveAll(toDelete.Contains);

        var (newObjects, summary) = TerrainBuildService.BuildContourObjects(mesh, analysis, doc.ModelAbsoluteTolerance);

        var existing = terrain.LastAnalysisResults.FirstOrDefault(r => r.AnalysisId == analysisId);
        if (existing != null)
            terrain.LastAnalysisResults.Remove(existing);
        terrain.LastAnalysisResults.Add(summary);

        var displayState = runtimeCache.DisplayState;
        if (displayState != null)
        {
            displayState.AuxiliaryObjects.RemoveAll(o => o.AnalysisId == analysisId);
            displayState.AuxiliaryObjects.AddRange(newObjects);
            displayState.AnalysisResults.RemoveAll(r => r.AnalysisId == analysisId);
            displayState.AnalysisResults.Add(summary);
        }

        Save(doc, state, raiseStateChanged: true);
        doc.Views.Redraw();
    }

    public void RefreshContourColor(RhinoDoc doc, Guid terrainId, Guid analysisId, int? colorArgb)
    {
        string terrainIdString = terrainId.ToString();
        string analysisIdString = analysisId.ToString();

        var runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        if (runtimeCache.DisplayState != null)
        {
            for (int index = 0; index < runtimeCache.DisplayState.AuxiliaryObjects.Count; index++)
            {
                GeneratedRhinoObject generated = runtimeCache.DisplayState.AuxiliaryObjects[index];
                if (generated.AnalysisId != analysisId)
                    continue;

                runtimeCache.DisplayState.AuxiliaryObjects[index] = new GeneratedRhinoObject
                {
                    Geometry = generated.Geometry,
                    Name = generated.Name,
                    Kind = generated.Kind,
                    AnalysisId = generated.AnalysisId,
                    ColorArgb = colorArgb,
                    LayerPath = generated.LayerPath,
                    SourceLayerPath = generated.SourceLayerPath,
                    MaterialName = generated.MaterialName,
                    InstanceDefinitionName = generated.InstanceDefinitionName,
                    MarkerBlockTemplate = generated.MarkerBlockTemplate,
                    InstanceUserStrings = generated.InstanceUserStrings,
                    InstanceTransform = generated.InstanceTransform
                };
            }
        }

        using var _ = new EventSuppression(this);
        foreach (var obj in doc.Objects
            .GetObjectList(new ObjectEnumeratorSettings
            {
                ActiveObjects = true,
                DeletedObjects = false,
                HiddenObjects = true,
                LockedObjects = true,
                NormalObjects = true,
                ReferenceObjects = false
            })
            .Where(obj =>
                obj?.Attributes?.GetUserString(OutputOwnerKey) == terrainIdString &&
                obj?.Attributes?.GetUserString(OutputAnalysisIdKey) == analysisIdString))
        {
            var attributes = obj.Attributes.Duplicate();
            if (colorArgb.HasValue)
            {
                attributes.ColorSource = ObjectColorSource.ColorFromObject;
                attributes.ObjectColor = GetOpaqueColor(System.Drawing.Color.FromArgb(colorArgb.Value));
            }
            else
            {
                attributes.ColorSource = ObjectColorSource.ColorFromLayer;
            }
            doc.Objects.ModifyAttributes(obj, attributes, quiet: true);
        }

        doc.Views.Redraw();
    }

    private static ModifierDefinition? CreateModifier(string modifierKind, UnitSystem unitSystem) =>
        Registry.TerrainTypeRegistry.CreateModifier(modifierKind, unitSystem);

    private static bool IsPinnedBaseTriangulate(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        return terrain.Modifiers.Count > 0 &&
               ReferenceEquals(terrain.Modifiers[0], modifier) &&
               modifier is TriangulateModifierDefinition;
    }

    private static ModifierDefinition? CloneModifier(ModifierDefinition modifier)
    {
        string json = JsonSerializer.Serialize(modifier, modifier.GetType());
        return JsonSerializer.Deserialize(json, modifier.GetType()) as ModifierDefinition;
    }

    private static TerrainObjectDefinition? CloneTerrainObjectDefinition(TerrainObjectDefinition definition)
    {
        string json = JsonSerializer.Serialize(definition, definition.GetType());
        return JsonSerializer.Deserialize(json, definition.GetType()) as TerrainObjectDefinition;
    }

    private static AnalysisDefinition? CloneAnalysis(AnalysisDefinition analysis)
    {
        string json = JsonSerializer.Serialize(analysis, analysis.GetType());
        return JsonSerializer.Deserialize(json, analysis.GetType()) as AnalysisDefinition;
    }

    private static string NextTerrainName(IEnumerable<TerrainDefinition> terrains)
    {
        int index = 1;
        var names = terrains.Select(terrain => terrain.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains($"Terrain {index}"))
            index++;

        return $"Terrain {index}";
    }

    private TerrainRuntimeCache GetRuntimeCache(uint docSerial, Guid terrainId)
    {
        if (_runtimeCaches.TryGetValue((docSerial, terrainId), out var cache))
            return cache;

        cache = new TerrainRuntimeCache();
        _runtimeCaches[(docSerial, terrainId)] = cache;
        return cache;
    }

    private void RemoveRuntimeCache(uint docSerial, Guid terrainId)
    {
        TerrainRebuildState? rebuildState = null;
        if (_rebuildStates.TryGetValue((docSerial, terrainId), out var existingRebuildState))
        {
            rebuildState = existingRebuildState;
            RetireRunningWorker(rebuildState, invalidateGeneration: true);
        }

        if (_runtimeCaches.TryGetValue((docSerial, terrainId), out var cache))
        {
            List<Mesh> detachedMeshes = cache.DetachMeshOutputs();
            if (rebuildState != null)
                DisposeDisplacedCacheMeshesWhenSafe(detachedMeshes, rebuildState);
            else
                DisposeMeshes(detachedMeshes);
        }

        _runtimeCaches.Remove((docSerial, terrainId));
    }

    private void ClearRuntimeCaches(uint docSerial)
    {
        foreach (var key in _runtimeCaches.Keys.Where(key => key.docSerial == docSerial).ToList())
            RemoveRuntimeCache(key.docSerial, key.terrainId);
    }

    private void ClearDocumentState(uint docSerial)
    {
        _states.Remove(docSerial);
        _pendingSourceReferencePrunes.Remove(docSerial);
        _pendingObjectReplacements.Remove(docSerial);
        ClearRuntimeCaches(docSerial);
        ClearRebuildStates(docSerial);
        RemovePendingDocumentSave(docSerial);
    }

    private TerrainRebuildState GetRebuildState(uint docSerial, Guid terrainId)
    {
        if (_rebuildStates.TryGetValue((docSerial, terrainId), out var state))
            return state;

        state = new TerrainRebuildState();
        _rebuildStates[(docSerial, terrainId)] = state;
        return state;
    }

    private long RequestRebuild(uint docSerial, Guid terrainId, bool isImmediate)
    {
        var rebuildState = GetRebuildState(docSerial, terrainId);
        rebuildState.RequestedVersion++;
        if (rebuildState.IsBuilding)
        {
            rebuildState.CancelRequested = true;
            rebuildState.WorkerCancellation?.Cancel();
        }
        return rebuildState.RequestedVersion;
    }

    private void RemoveRebuildState(uint docSerial, Guid terrainId)
    {
        CancelRunningBuild(docSerial, terrainId);
        _rebuildStates.Remove((docSerial, terrainId));
        RemovePendingBuild(docSerial, terrainId, TerrainBuildMode.Preview);
        RemovePendingBuild(docSerial, terrainId, TerrainBuildMode.Final);
    }

    private void ClearRebuildStates(uint docSerial)
    {
        foreach (var key in _rebuildStates.Keys.Where(key => key.docSerial == docSerial).ToList())
        {
            CancelRunningBuild(key.docSerial, key.terrainId);
            _rebuildStates.Remove(key);
        }

        foreach (var key in _pendingRebuilds.Keys.Where(key => key.docSerial == docSerial).ToList())
            _pendingRebuilds.Remove(key);

        ClearPendingBlockAttributeKeyRepairs(docSerial);
    }

    private void ClearPendingBlockAttributeKeyRepairs(uint docSerial)
    {
        _pendingBlockAttributeKeyRepairs.Remove(docSerial);
    }

    private void CancelRunningBuild(uint docSerial, Guid terrainId)
    {
        if (!_rebuildStates.TryGetValue((docSerial, terrainId), out var rebuildState))
            return;

        RetireRunningWorker(rebuildState, invalidateGeneration: true);
    }

    private static void RetireRunningWorker(TerrainRebuildState rebuildState, bool invalidateGeneration)
    {
        PruneCompletedRetiredWorkers(rebuildState);

        Task<BackgroundBuildResult>? workerTask = rebuildState.WorkerTask;
        CancellationTokenSource? cancellation = rebuildState.WorkerCancellation;

        cancellation?.Cancel();
        if (workerTask != null && !workerTask.IsCompleted)
            rebuildState.RetiredWorkers.Add(workerTask);

        if (workerTask != null && !workerTask.IsCompleted && cancellation != null)
        {
            _ = workerTask.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                cancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            cancellation?.Dispose();
        }

        rebuildState.WorkerTask = null;
        rebuildState.WorkerCancellation = null;
        rebuildState.IsBuilding = false;
        rebuildState.RunningVersion = 0;
        rebuildState.CancelRequested = false;

        if (invalidateGeneration)
            rebuildState.BuildGeneration++;
    }

    private void PruneCompletedRetiredWorkers()
    {
        foreach (var rebuildState in _rebuildStates.Values)
            PruneCompletedRetiredWorkers(rebuildState);
    }

    private static void PruneCompletedRetiredWorkers(TerrainRebuildState rebuildState)
    {
        for (int i = rebuildState.RetiredWorkers.Count - 1; i >= 0; i--)
        {
            if (rebuildState.RetiredWorkers[i].IsCompleted)
                rebuildState.RetiredWorkers.RemoveAt(i);
        }
    }

    private static void DisposeDisplacedCacheMeshesWhenSafe(List<Mesh> meshes, TerrainRebuildState rebuildState)
    {
        if (meshes.Count == 0)
            return;

        PruneCompletedRetiredWorkers(rebuildState);
        if (rebuildState.RetiredWorkers.Count == 0)
        {
            DisposeMeshes(meshes);
            return;
        }

        Task[] retiredWorkers = rebuildState.RetiredWorkers.ToArray();
        _ = Task.WhenAll(retiredWorkers).ContinueWith(
            static (_, state) => DisposeMeshes((List<Mesh>)state!),
            meshes,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void DisposeMeshes(IEnumerable<Mesh> meshes)
    {
        foreach (Mesh mesh in meshes)
            mesh.Dispose();
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

    private sealed class UndoState
    {
        public string Json { get; init; } = string.Empty;
        public Guid? SelectedTerrainId { get; init; }
    }

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
