using System.Security.Cryptography;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Eto.Forms;
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

internal sealed class TerrainController
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

    private readonly record struct PendingBuildRequest(DateTime DueAtUtc, long Version);

    private readonly record struct BackgroundBuildResult(
        long Version,
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

        public TerrainBuildMode RunningMode { get; set; } = TerrainBuildMode.Final;

        public bool IsBuilding { get; set; }

        public bool CancelRequested { get; set; }

        public long SkippedPreviewVersion { get; set; }

        public long SkippedFinalVersion { get; set; }

        public Task<BackgroundBuildResult>? WorkerTask { get; set; }

        public CancellationTokenSource? WorkerCancellation { get; set; }
    }

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
        RhinoApp.Idle += OnIdle;
        _displayConduit.Enabled = true;
    }

    public IReadOnlyList<TerrainDefinition> GetTerrains(RhinoDoc doc) => GetState(doc).Terrains;

    public void ReloadDocumentState(RhinoDoc doc)
    {
        _states.Remove(doc.RuntimeSerialNumber);
        ClearRuntimeCaches(doc.RuntimeSerialNumber);
        ClearRebuildStates(doc.RuntimeSerialNumber);
        ClearPendingBlockAttributeKeyRepairs(doc.RuntimeSerialNumber);
        RemovePendingDocumentSave(doc.RuntimeSerialNumber);
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

    public TerrainDefinition CreateTerrain(RhinoDoc doc, bool seedFromSelection)
    {
        var state = GetState(doc);
        var terrain = new TerrainDefinition
        {
            Name = NextTerrainName(state.Terrains),
            TerrainLayerPath = TerrainDefinition.DefaultTerrainLayerPath,
            AuxiliaryLayerPath = TerrainDefinition.DefaultAuxiliaryLayerPath
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
            var modifier = CreateModifier(modifierKind);
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
            MarkerDefinition marker = markerKind switch
            {
                "elevation" => new ElevationMarkerDefinition(),
                "slope" => new SlopeMarkerDefinition(),
                _ => throw new InvalidOperationException($"Unknown marker kind '{markerKind}'.")
            };

            terrain.Markers.Add(marker);
        });
    }

    public void AddObjectDefinition(RhinoDoc doc, Guid terrainId, string objectKind)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            TerrainObjectDefinition definition = objectKind switch
            {
                "lowest-point" => new LowestPointObjectDefinition(),
                "surface-oriented" => new SurfaceOrientedObjectDefinition(),
                _ => throw new InvalidOperationException($"Unknown object definition kind '{objectKind}'.")
            };

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
            terrain.LastBuildMessage = $"Queued rebuild #{buildVersion:N0}; current build will stop at the next safe checkpoint.";
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
        Save(doc, state);
        doc.Views.Redraw();
    }

    public TerrainDefinition DuplicateTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return null!;

        var json = System.Text.Json.JsonSerializer.Serialize(terrain);
        var clone = System.Text.Json.JsonSerializer.Deserialize<TerrainDefinition>(json)!;
        clone.TerrainId = Guid.NewGuid();
        clone.Name = terrain.Name + " Copy";
        clone.OutputObjectIds.Clear();
        clone.ZoneObjectIds.Clear();
        clone.AuxiliaryObjectIds.Clear();
        clone.MarkerObjectIds.Clear();
        clone.BakedObjectIds.Clear();
        clone.LastBuildMessage = null;
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
        if (runtimeCache.DisplayState?.TerrainMesh == null)
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

    private DocumentState GetState(RhinoDoc doc)
    {
        if (_states.TryGetValue(doc.RuntimeSerialNumber, out var state))
            return state;

        state = new DocumentState
        {
            Terrains = _documentStore.Load(doc)
        };
        state.SelectedTerrainId = state.Terrains.FirstOrDefault()?.TerrainId;
        _states[doc.RuntimeSerialNumber] = state;
        return state;
    }

    private void Save(RhinoDoc doc, DocumentState state, bool raiseStateChanged = true)
    {
        RemovePendingDocumentSave(doc.RuntimeSerialNumber);
        _documentStore.Save(doc, state.Terrains);
        if (raiseStateChanged)
            RaiseStateChanged();
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
        var restoredTerrains = TerrainSerializer.Deserialize(snapshot.Json);
        var restoredState = new DocumentState
        {
            Terrains = restoredTerrains,
            SelectedTerrainId = snapshot.SelectedTerrainId ?? restoredTerrains.FirstOrDefault()?.TerrainId
        };

        _states[doc.RuntimeSerialNumber] = restoredState;
        ClearRuntimeCaches(doc.RuntimeSerialNumber);
        ClearRebuildStates(doc.RuntimeSerialNumber);
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

        var (newObjects, summary) = TerrainBuildService.BuildContourObjects(mesh, analysis);
        foreach (var obj in newObjects)
            AddGeneratedObject(doc, terrain, obj);

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

    private void ScheduleRebuild(RhinoDoc doc, Guid terrainId, bool notify = true)
    {
        long requestedVersion = RequestRebuild(doc.RuntimeSerialNumber, terrainId, isImmediate: false);
        QueuePendingBuild(doc.RuntimeSerialNumber, terrainId, TerrainBuildMode.Final, requestedVersion, FinalDebounceMs);
        var terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain != null)
            terrain.LastBuildMessage = GetRebuildState(doc.RuntimeSerialNumber, terrainId).IsBuilding
                ? $"Queued rebuild #{requestedVersion:N0}; current build will stop at the next safe checkpoint."
                : $"Scheduled rebuild #{requestedVersion:N0}.";
        if (notify)
            RaiseStateChanged();
    }

    private void QueuePendingBuild(uint docSerial, Guid terrainId, TerrainBuildMode mode, long version, int delayMs)
    {
        _pendingRebuilds[(docSerial, terrainId, mode)] = new PendingBuildRequest(DateTime.UtcNow.AddMilliseconds(delayMs), version);
    }

    private void QueuePendingDocumentSave(uint docSerial, int delayMs)
    {
        _pendingDocumentSaves[docSerial] = DateTime.UtcNow.AddMilliseconds(delayMs);
    }

    private void RemovePendingBuild(uint docSerial, Guid terrainId, TerrainBuildMode mode)
    {
        _pendingRebuilds.Remove((docSerial, terrainId, mode));
    }

    private void RemovePendingDocumentSave(uint docSerial)
    {
        _pendingDocumentSaves.Remove(docSerial);
    }

    private bool ConfirmLongRunningBuild(
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainRuntimeCache runtimeCache,
        TerrainRebuildState rebuildState,
        TerrainBuildMode mode,
        long buildVersion)
    {
        if (!ShouldWarnAboutLongBuild(terrain, runtimeCache, mode, out string? warning))
            return true;

        long skippedVersion = mode == TerrainBuildMode.Preview
            ? rebuildState.SkippedPreviewVersion
            : rebuildState.SkippedFinalVersion;
        if (skippedVersion >= buildVersion)
        {
            terrain.LastBuildMessage = mode == TerrainBuildMode.Preview
                ? $"Skipped preview #{buildVersion:N0}; waiting for a smaller change or manual rebuild."
                : $"Skipped final rebuild #{buildVersion:N0}; manual rebuild required.";
            RaiseStateChanged();
            return false;
        }

        SlowBuildWarningDialogResult result = SlowBuildWarningDialog.Show(
            doc,
            warning ?? "This rebuild is likely to take a while.",
            mode == TerrainBuildMode.Preview ? "Slow Preview Warning" : "Slow Build Warning");

        if (result.DisableFutureWarnings && terrain.ShowSlowBuildWarning)
        {
            terrain.ShowSlowBuildWarning = false;
            Save(doc, GetState(doc));
        }

        if (result.ContinueBuild)
            return true;

        if (mode == TerrainBuildMode.Preview)
            rebuildState.SkippedPreviewVersion = buildVersion;
        else
            rebuildState.SkippedFinalVersion = buildVersion;

        terrain.LastBuildMessage = mode == TerrainBuildMode.Preview
            ? $"Skipped preview #{buildVersion:N0} after long-run warning."
            : $"Skipped final rebuild #{buildVersion:N0} after long-run warning.";
        RaiseStateChanged();
        return false;
    }

    private static bool ShouldWarnAboutLongBuild(
        TerrainDefinition terrain,
        TerrainRuntimeCache runtimeCache,
        TerrainBuildMode mode,
        out string? warning)
    {
        warning = null;
        if (!terrain.ShowSlowBuildWarning)
            return false;

        TimeSpan? priorDuration = mode == TerrainBuildMode.Preview
            ? runtimeCache.LastPreviewDuration
            : runtimeCache.LastFinalDuration;
        double thresholdSeconds = mode == TerrainBuildMode.Preview
            ? PreviewWarningThresholdSeconds
            : FinalWarningThresholdSeconds;
        if (priorDuration.HasValue && priorDuration.Value.TotalSeconds >= thresholdSeconds)
        {
            warning = mode == TerrainBuildMode.Preview
                ? $"The last preview for this terrain took {priorDuration.Value.TotalSeconds:0.##} s. Continue with another live preview?"
                : $"The last exact rebuild for this terrain took {priorDuration.Value.TotalSeconds:0.##} s. Continue with another full rebuild?";
            return true;
        }

        Mesh? mesh = runtimeCache.DisplayState?.TerrainMesh;
        if (mesh == null)
            return false;

        bool hasExpensiveModifier = terrain.Modifiers.Any(static modifier =>
            modifier.IsEnabled &&
            modifier is RemeshModifierDefinition or GradePadModifierDefinition or GradePathModifierDefinition or RetainingWallModifierDefinition or InSituStairModifierDefinition);
        int faceThreshold = mode == TerrainBuildMode.Preview ? PreviewWarningFaceThreshold : FinalWarningFaceThreshold;
        if (!hasExpensiveModifier || mesh.Faces.Count < faceThreshold)
            return false;

        warning = mode == TerrainBuildMode.Preview
            ? $"This terrain currently has {mesh.Vertices.Count:N0} verts and {mesh.Faces.Count:N0} faces with expensive live modifiers enabled. Preview may take a while. Continue?"
            : $"This terrain currently has {mesh.Vertices.Count:N0} verts and {mesh.Faces.Count:N0} faces with expensive modifiers enabled. The exact rebuild may take a while. Continue?";
        return true;
    }

    private void StartBackgroundBuild(
        RhinoDoc doc,
        DocumentState state,
        TerrainDefinition terrain,
        TerrainBuildMode mode,
        long? requestedBuildVersion = null)
    {
        var rebuildState = GetRebuildState(doc.RuntimeSerialNumber, terrain.TerrainId);
        long buildVersion = requestedBuildVersion ?? Math.Max(rebuildState.RequestedVersion, rebuildState.AppliedVersion + 1);
        rebuildState.RequestedVersion = Math.Max(rebuildState.RequestedVersion, buildVersion);

        if (!ConfirmLongRunningBuild(doc, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), rebuildState, mode, buildVersion))
            return;

        var snapshotTimer = Stopwatch.StartNew();
        TerrainBuildSnapshot snapshot = TerrainBuildSnapshotBuilder.Create(doc, terrain);
        snapshotTimer.Stop();

        var workerCacheTimer = Stopwatch.StartNew();
        TerrainRuntimeCache workerCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId).CreateWorkerCopy();
        workerCacheTimer.Stop();
        var cancellation = new CancellationTokenSource();

        rebuildState.RunningVersion = buildVersion;
        rebuildState.RunningMode = mode;
        rebuildState.CancelRequested = false;
        rebuildState.IsBuilding = true;
        rebuildState.WorkerCancellation?.Dispose();
        rebuildState.WorkerCancellation = cancellation;
        rebuildState.WorkerTask = Task.Run(() => ExecuteBackgroundBuild(
            snapshot,
            workerCache,
            mode,
            buildVersion,
            snapshotTimer.Elapsed,
            workerCacheTimer.Elapsed,
            cancellation.Token));

        terrain.LastBuildMessage = mode == TerrainBuildMode.Preview
            ? $"Previewing terrain #{buildVersion:N0}..."
            : $"Building terrain #{buildVersion:N0}...";
        RaiseStateChanged();
    }

    private bool BuildTerrainSynchronously(
        RhinoDoc doc,
        DocumentState state,
        TerrainDefinition terrain,
        TerrainBuildMode mode,
        long? requestedBuildVersion = null)
    {
        var rebuildState = GetRebuildState(doc.RuntimeSerialNumber, terrain.TerrainId);
        long buildVersion = requestedBuildVersion ?? Math.Max(rebuildState.RequestedVersion, rebuildState.AppliedVersion + 1);
        rebuildState.RequestedVersion = Math.Max(rebuildState.RequestedVersion, buildVersion);
        rebuildState.RunningVersion = buildVersion;
        rebuildState.RunningMode = mode;
        rebuildState.CancelRequested = false;
        rebuildState.IsBuilding = true;

        if (!ConfirmLongRunningBuild(doc, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), rebuildState, mode, buildVersion))
        {
            rebuildState.IsBuilding = false;
            rebuildState.RunningVersion = 0;
            return false;
        }

        terrain.LastBuildMessage = mode == TerrainBuildMode.Preview
            ? $"Previewing terrain #{buildVersion:N0}..."
            : $"Building terrain #{buildVersion:N0}...";
        RaiseStateChanged();

        try
        {
            var snapshotTimer = Stopwatch.StartNew();
            TerrainBuildSnapshot snapshot = TerrainBuildSnapshotBuilder.Create(doc, terrain);
            snapshotTimer.Stop();

            var workerCacheTimer = Stopwatch.StartNew();
            TerrainRuntimeCache workerCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId).CreateWorkerCopy();
            workerCacheTimer.Stop();

            BackgroundBuildResult result = ExecuteBackgroundBuild(
                snapshot,
                workerCache,
                mode,
                buildVersion,
                snapshotTimer.Elapsed,
                workerCacheTimer.Elapsed,
                CancellationToken.None);
            if (result.WasCanceled || rebuildState.RequestedVersion > buildVersion)
            {
                terrain.LastBuildMessage = rebuildState.RequestedVersion > buildVersion
                    ? $"{mode} #{buildVersion:N0} cancelled; newer request queued."
                    : $"{mode} #{buildVersion:N0} cancelled.";
                RaiseStateChanged();
                return false;
            }

            if (result.Error != null || result.Build == null)
            {
                if (mode == TerrainBuildMode.Final)
                    terrain.LastBuildUtc = DateTimeOffset.UtcNow;

                terrain.LastBuildMessage = $"{mode} failed: {result.Error?.Message ?? "Unknown build error."}";
                if (result.Error != null)
                    RhinoApp.WriteLine($"[MoleHill] Rebuild failed for '{terrain.Name}': {result.Error}");
                if (mode == TerrainBuildMode.Final)
                    Save(doc, state);
                return false;
            }

            ApplySuccessfulBuild(doc, state, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), rebuildState, result);
            return true;
        }
        finally
        {
            rebuildState.IsBuilding = false;
            rebuildState.RunningVersion = 0;
            rebuildState.CancelRequested = false;
        }
    }

    private BackgroundBuildResult ExecuteBackgroundBuild(
        TerrainBuildSnapshot snapshot,
        TerrainRuntimeCache workerCache,
        TerrainBuildMode mode,
        long buildVersion,
        TimeSpan snapshotElapsed,
        TimeSpan workerCacheCloneElapsed,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            TerrainBuildResult build = _buildService.Build(
                snapshot,
                workerCache,
                mode,
                () => cancellationToken.IsCancellationRequested);
            timer.Stop();
            return new BackgroundBuildResult(
                buildVersion,
                mode,
                snapshot.Terrain,
                build,
                workerCache,
                snapshotElapsed,
                workerCacheCloneElapsed,
                timer.Elapsed,
                WasCanceled: false,
                Error: null);
        }
        catch (OperationCanceledException)
        {
            timer.Stop();
            return new BackgroundBuildResult(
                buildVersion,
                mode,
                snapshot.Terrain,
                Build: null,
                WorkerCache: workerCache,
                SnapshotElapsed: snapshotElapsed,
                WorkerCacheCloneElapsed: workerCacheCloneElapsed,
                BuildElapsed: timer.Elapsed,
                WasCanceled: true,
                Error: null);
        }
        catch (Exception ex)
        {
            timer.Stop();
            return new BackgroundBuildResult(
                buildVersion,
                mode,
                snapshot.Terrain,
                Build: null,
                WorkerCache: workerCache,
                SnapshotElapsed: snapshotElapsed,
                WorkerCacheCloneElapsed: workerCacheCloneElapsed,
                BuildElapsed: timer.Elapsed,
                WasCanceled: false,
                Error: ex);
        }
    }

    private void CompleteBackgroundBuild(
        RhinoDoc doc,
        DocumentState state,
        TerrainDefinition terrain,
        TerrainRebuildState rebuildState,
        BackgroundBuildResult result)
    {
        rebuildState.IsBuilding = false;
        rebuildState.RunningVersion = 0;
        rebuildState.CancelRequested = false;

        if (rebuildState.RequestedVersion > result.Version)
        {
            terrain.LastBuildMessage = $"{result.Mode} #{result.Version:N0} cancelled; newer request queued.";
            RaiseStateChanged();
            return;
        }

        if (result.WasCanceled)
        {
            if (result.Mode == TerrainBuildMode.Final)
                terrain.LastBuildUtc = DateTimeOffset.UtcNow;

            terrain.LastBuildMessage = $"{result.Mode} #{result.Version:N0} cancelled.";
            RaiseStateChanged();
            return;
        }

        if (result.Error != null || result.Build == null)
        {
            if (result.Mode == TerrainBuildMode.Final)
                terrain.LastBuildUtc = DateTimeOffset.UtcNow;

            terrain.LastBuildMessage = $"{result.Mode} failed: {result.Error?.Message ?? "Unknown build error."}";
            if (result.Error != null)
                RhinoApp.WriteLine($"[MoleHill] Rebuild failed for '{terrain.Name}': {result.Error}");
            if (result.Mode == TerrainBuildMode.Final)
                Save(doc, state);
            return;
        }

        ApplySuccessfulBuild(doc, state, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), rebuildState, result);
    }

    private void ApplySuccessfulBuild(
        RhinoDoc doc,
        DocumentState state,
        TerrainDefinition terrain,
        TerrainRuntimeCache runtimeCache,
        TerrainRebuildState rebuildState,
        BackgroundBuildResult result)
    {
        TerrainBuildResult build = result.Build!;
        build.RecordTiming("Snapshot build", result.SnapshotElapsed, $"{result.SnapshotTerrain.Modifiers.Count:N0} modifiers", MinorTimingDiagnosticThresholdMs);
        build.RecordTiming("Worker cache clone", result.WorkerCacheCloneElapsed, null, MinorTimingDiagnosticThresholdMs);

        var cacheMergeTimer = Stopwatch.StartNew();
        runtimeCache.ReplaceBuildCachesFrom(result.WorkerCache);
        cacheMergeTimer.Stop();
        build.RecordTiming("Worker cache merge", cacheMergeTimer.Elapsed, null, MinorTimingDiagnosticThresholdMs);

        SyncComputedModifierState(terrain, result.SnapshotTerrain);

        TimeSpan buildElapsed = result.BuildElapsed;
        if (result.Mode == TerrainBuildMode.Final)
        {
            terrain.LastBuildUtc = DateTimeOffset.UtcNow;
            terrain.LastAnalysisResults = TerrainRuntimeCacheCloner.CloneAnalyses(build.AnalysisResults);
            ClearLegacyOutputState(doc, terrain);
            runtimeCache.LastFinalDuration = buildElapsed;
            rebuildState.AppliedVersion = result.Version;
        }
        else
        {
            runtimeCache.LastPreviewDuration = buildElapsed;
            rebuildState.AppliedPreviewVersion = result.Version;
        }

        var displayTimer = Stopwatch.StartNew();
        UpdateDisplayState(doc, terrain, runtimeCache, build);
        displayTimer.Stop();
        build.RecordTiming("Display refresh", displayTimer.Elapsed, DescribeDisplayState(runtimeCache.DisplayState), StageTimingDiagnosticThresholdMs);

        if (result.Mode == TerrainBuildMode.Final)
        {
            SyncTerrainObjects(doc, terrain, build);
            terrain.LastBuildMessage = build.Diagnostics.Count == 0
                ? "Build succeeded."
                : string.Join(System.Environment.NewLine, build.Diagnostics.Take(8));

            var saveTimer = Stopwatch.StartNew();
            Save(doc, state);
            saveTimer.Stop();
            build.RecordTiming("Document save", saveTimer.Elapsed, $"{state.Terrains.Count:N0} terrains", MinorTimingDiagnosticThresholdMs);

            var redrawTimer = Stopwatch.StartNew();
            doc.Views.Redraw();
            redrawTimer.Stop();
            build.RecordTiming("Viewport redraw", redrawTimer.Elapsed, null, MinorTimingDiagnosticThresholdMs);

            var totalTimingDetail = $"snapshot {FormatElapsed(result.SnapshotElapsed)}, clone {FormatElapsed(result.WorkerCacheCloneElapsed)}, build {FormatElapsed(buildElapsed)}, merge {FormatElapsed(cacheMergeTimer.Elapsed)}, display {FormatElapsed(displayTimer.Elapsed)}, save {FormatElapsed(saveTimer.Elapsed)}, redraw {FormatElapsed(redrawTimer.Elapsed)}; {DescribeDisplayState(runtimeCache.DisplayState)}";
            build.RecordTiming(
                "Rebuild total",
                result.SnapshotElapsed + result.WorkerCacheCloneElapsed + buildElapsed + cacheMergeTimer.Elapsed + displayTimer.Elapsed + saveTimer.Elapsed + redrawTimer.Elapsed,
                totalTimingDetail,
                TotalTimingDiagnosticThresholdMs);

            var stageTimings = string.Join(", ", build.Timings
                .Where(t => t.Stage != "Rebuild total")
                .Select(t => $"{t.Stage}: {FormatElapsed(t.Elapsed)}"));
            terrain.LastBuildMessage = string.Join(System.Environment.NewLine,
                new[] { build.Diagnostics.Count == 0 ? "Build succeeded." : string.Join(System.Environment.NewLine, build.Diagnostics.Take(8)) }
                .Append($"[{totalTimingDetail}]")
                .Append($"[stages: {stageTimings}]"));
        }
        else
        {
            build.RecordTiming(
                "Preview total",
                result.SnapshotElapsed + result.WorkerCacheCloneElapsed + buildElapsed + cacheMergeTimer.Elapsed + displayTimer.Elapsed,
                $"snapshot {FormatElapsed(result.SnapshotElapsed)}, clone {FormatElapsed(result.WorkerCacheCloneElapsed)}, build {FormatElapsed(buildElapsed)}, merge {FormatElapsed(cacheMergeTimer.Elapsed)}, display {FormatElapsed(displayTimer.Elapsed)}; {DescribeDisplayState(runtimeCache.DisplayState)}",
                TotalTimingDiagnosticThresholdMs);

            terrain.LastBuildMessage = build.Diagnostics.Count == 0
                ? "Preview updated; final rebuild queued."
                : $"Preview updated.{System.Environment.NewLine}{string.Join(System.Environment.NewLine, build.Diagnostics.Take(6))}";

            var redrawTimer = Stopwatch.StartNew();
            doc.Views.Redraw();
            redrawTimer.Stop();
            build.RecordTiming("Viewport redraw", redrawTimer.Elapsed, null, MinorTimingDiagnosticThresholdMs);
        }

        RaiseStateChanged();
    }

    private static void SyncComputedModifierState(TerrainDefinition targetTerrain, TerrainDefinition sourceTerrain)
    {
        foreach (var sourceModifier in sourceTerrain.Modifiers.OfType<InSituStairModifierDefinition>())
        {
            if (targetTerrain.Modifiers.FirstOrDefault(modifier => modifier.Id == sourceModifier.Id) is not InSituStairModifierDefinition targetModifier)
                continue;

            targetModifier.ComputedSurfaceCount = sourceModifier.ComputedSurfaceCount;
            targetModifier.ComputedTreadDepthSummary = sourceModifier.ComputedTreadDepthSummary;
            targetModifier.ComputedStepCountSummary = sourceModifier.ComputedStepCountSummary;
        }
    }

    private OutputSyncMetrics SyncOutputs(RhinoDoc doc, TerrainDefinition terrain, TerrainBuildResult build)
    {
        int deletedObjectCount = AllOwnedIds(terrain).Distinct().Count();
        DeleteOwnedObjects(doc, terrain);

        var outputIds = new List<Guid>();
        var zoneIds = new List<Guid>();
        var auxiliaryIds = new List<Guid>();
        var markerIds = new List<Guid>();
        var blockAttributeRefreshIds = new List<Guid>();

        using var _ = new EventSuppression(this);

        if (build.PrimaryMesh != null)
        {
            Guid id = AddGeneratedObject(doc, terrain, new GeneratedRhinoObject
            {
                Geometry = build.PrimaryMesh,
                Name = terrain.Name,
                LayerPath = TerrainDefinition.ResolveTerrainLayerPath(terrain.TerrainLayerPath)
            }, blockAttributeRefreshIds);

            if (id != Guid.Empty)
                outputIds.Add(id);
        }

        foreach (var zoneObject in build.ZoneObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, zoneObject, blockAttributeRefreshIds);
            if (id != Guid.Empty)
                zoneIds.Add(id);
        }

        foreach (var auxiliary in build.AuxiliaryObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, new GeneratedRhinoObject
            {
                Geometry = auxiliary.Geometry,
                Name = auxiliary.Name,
                Kind = auxiliary.Kind,
                AnalysisId = auxiliary.AnalysisId,
                ColorArgb = auxiliary.ColorArgb,
                LayerPath = auxiliary.LayerPath ?? terrain.AuxiliaryLayerPath,
                SourceLayerPath = auxiliary.SourceLayerPath,
                MaterialName = auxiliary.MaterialName,
                InstanceDefinitionName = auxiliary.InstanceDefinitionName,
                MarkerBlockTemplate = auxiliary.MarkerBlockTemplate,
                InstanceUserStrings = auxiliary.InstanceUserStrings,
                InstanceTransform = auxiliary.InstanceTransform
            }, blockAttributeRefreshIds);
            if (id != Guid.Empty)
                auxiliaryIds.Add(id);
        }

        foreach (var marker in build.MarkerObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, marker, blockAttributeRefreshIds);
            if (id != Guid.Empty)
                markerIds.Add(id);
        }

        QueuePendingBlockAttributeKeyRepair(doc, blockAttributeRefreshIds);

        terrain.OutputObjectIds = outputIds;
        terrain.ZoneObjectIds = zoneIds;
        terrain.AuxiliaryObjectIds = auxiliaryIds;
        terrain.MarkerObjectIds = markerIds;
        return new OutputSyncMetrics(
            deletedObjectCount,
            outputIds.Count,
            zoneIds.Count,
            auxiliaryIds.Count,
            markerIds.Count);
    }

    private Guid AddGeneratedObject(
        RhinoDoc doc,
        TerrainDefinition terrain,
        GeneratedRhinoObject generated,
        ICollection<Guid>? blockAttributeRefreshIds = null)
    {
        var attributes = CreateAttributes(doc, terrain, generated, trackOwnership: true);
        ApplyInstanceUserStrings(attributes, generated.InstanceUserStrings);

        if (!string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
        {
            int definitionIndex = EnsureBlockDefinition(doc, generated.InstanceDefinitionName!, generated.MarkerBlockTemplate);
            if (definitionIndex >= 0)
            {
                var definition = doc.InstanceDefinitions[definitionIndex];
                if (definition != null)
                {
                    var instanceGeometry = CreateInstanceReferenceGeometry(definition.Id, generated.InstanceTransform, generated.InstanceUserStrings);
                    Guid id = doc.Objects.Add(instanceGeometry, attributes);
                    if (id != Guid.Empty && !EnsureBlockInstanceAttributeKeys(doc, id, definition))
                        blockAttributeRefreshIds?.Add(id);

                    return id;
                }
            }
        }

        return generated.Geometry switch
        {
            Mesh mesh => doc.Objects.AddMesh(mesh, attributes),
            Brep brep => doc.Objects.AddBrep(brep, attributes),
            Curve curve => doc.Objects.AddCurve(curve, attributes),
            TextDot textDot => doc.Objects.AddTextDot(textDot, attributes),
            _ => Guid.Empty
        };
    }

    private static int EnsureBlockDefinition(RhinoDoc doc, string definitionName, MarkerBlockTemplate template)
    {
        var existingDefinition = doc.InstanceDefinitions.Find(definitionName);
        if (existingDefinition != null)
        {
            if (ShouldRefreshManagedBlockDefinition(definitionName, template))
            {
                var refreshedGeometry = GeneratedBlockCatalog.CreateBlockGeometry(template);
                var refreshedAttributes = refreshedGeometry.Select(_ => CreateBlockDefinitionAttributes()).ToList();
                doc.InstanceDefinitions.ModifyGeometry(existingDefinition.Index, refreshedGeometry, refreshedAttributes);
            }

            return existingDefinition.Index;
        }

        var geometry = GeneratedBlockCatalog.CreateBlockGeometry(template);
        if (geometry.Count == 0)
            return -1;

        var attributes = geometry.Select(_ => CreateBlockDefinitionAttributes()).ToList();
        return doc.InstanceDefinitions.Add(definitionName, string.Empty, Point3d.Origin, geometry, attributes);
    }

    private static bool ShouldRefreshManagedBlockDefinition(string definitionName, MarkerBlockTemplate template)
    {
        return string.Equals(
            definitionName,
            GeneratedBlockCatalog.GetDefaultDefinitionName(template),
            StringComparison.Ordinal);
    }

    private static ObjectAttributes CreateBlockDefinitionAttributes()
    {
        return new ObjectAttributes
        {
            ColorSource = ObjectColorSource.ColorFromParent,
            PlotColorSource = ObjectPlotColorSource.PlotColorFromParent,
            MaterialSource = ObjectMaterialSource.MaterialFromParent,
            LinetypeSource = ObjectLinetypeSource.LinetypeFromParent
        };
    }

    private ObjectAttributes CreateAttributes(RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        return CreateAttributes(doc, terrain, generated, trackOwnership: true);
    }

    private ObjectAttributes CreateAttributes(RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated, bool trackOwnership)
    {
        var attributes = new ObjectAttributes
        {
            Name = $"{terrain.Name} :: {generated.Name}"
        };

        if (trackOwnership)
        {
            attributes.SetUserString(OutputOwnerKey, terrain.TerrainId.ToString());
            if (!string.IsNullOrWhiteSpace(generated.MaterialName))
                attributes.SetUserString(OutputBaseMaterialNameKey, generated.MaterialName!);
            else
                attributes.DeleteUserString(OutputBaseMaterialNameKey);

            if (generated.Kind != GeneratedObjectKind.Default)
                attributes.SetUserString(OutputKindKey, generated.Kind.ToString());
            else
                attributes.DeleteUserString(OutputKindKey);

            if (generated.AnalysisId.HasValue)
                attributes.SetUserString(OutputAnalysisIdKey, generated.AnalysisId.Value.ToString());
            else
                attributes.DeleteUserString(OutputAnalysisIdKey);
        }
        else
        {
            attributes.DeleteUserString(OutputOwnerKey);
            attributes.DeleteUserString(OutputBaseMaterialNameKey);
            attributes.DeleteUserString(OutputKindKey);
            attributes.DeleteUserString(OutputAnalysisIdKey);
        }

        if (generated.ColorArgb.HasValue)
        {
            attributes.ColorSource = ObjectColorSource.ColorFromObject;
            attributes.ObjectColor = GetOpaqueColor(System.Drawing.Color.FromArgb(generated.ColorArgb.Value));
            if (!trackOwnership)
                ApplyBakedColorTransparency(doc, generated.ColorArgb.Value, attributes);
        }

        if (!string.IsNullOrWhiteSpace(generated.LayerPath))
            attributes.LayerIndex = EnsureLayer(doc, generated.LayerPath!, generated.SourceLayerPath);

        ApplyOutputWireAttributes(terrain, attributes);
        if (trackOwnership)
            ApplyOutputRenderAttributes(doc, terrain, attributes);
        return attributes;
    }

    private static void ApplyInstanceUserStrings(ObjectAttributes attributes, IReadOnlyDictionary<string, string>? userStrings)
    {
        if (userStrings == null)
            return;

        foreach (var pair in userStrings)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            SetUserStringPreservingEmpty(attributes, pair.Key, pair.Value ?? string.Empty);
        }
    }

    private static InstanceReferenceGeometry CreateInstanceReferenceGeometry(
        Guid definitionId,
        Transform transform,
        IReadOnlyDictionary<string, string>? userStrings)
    {
        var geometry = new InstanceReferenceGeometry(definitionId, transform);
        if (userStrings == null)
            return geometry;

        foreach (var pair in userStrings)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            SetUserStringPreservingEmpty(geometry, pair.Key, pair.Value ?? string.Empty);
        }

        return geometry;
    }

    private bool EnsureBlockInstanceAttributeKeys(RhinoDoc doc, Guid objectId, InstanceDefinition definition)
    {
        var fields = TextFields.GetInstanceAttributeFields(definition);
        if (fields.Length == 0)
            return true;

        if (doc.Objects.FindId(objectId) is not InstanceObject instanceObject)
            return true;

        var currentStrings = instanceObject.Attributes.GetUserStrings();
        if (HasAllBlockInstanceAttributeKeys(currentStrings, fields))
            return true;

        var attributes = instanceObject.Attributes.Duplicate();
        bool changed = false;
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key) || attributes.GetUserString(field.Key) != null)
                continue;

            string value = field.DefaultValue ?? string.Empty;
            changed |= SetUserStringPreservingEmpty(attributes, field.Key, value);
        }

        if (!changed)
            return true;

        instanceObject.Attributes = attributes;
        instanceObject.CommitChanges();
        currentStrings = instanceObject.Attributes.GetUserStrings();
        if (HasAllBlockInstanceAttributeKeys(currentStrings, fields))
            return true;

        bool objectChanged = false;
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key) || instanceObject.Attributes.GetUserString(field.Key) != null)
                continue;

            string value = field.DefaultValue ?? string.Empty;
            objectChanged |= SetUserStringPreservingEmpty(instanceObject, field.Key, value);
        }

        if (objectChanged)
            instanceObject.CommitChanges();

        return HasAllBlockInstanceAttributeKeys(instanceObject.Attributes.GetUserStrings(), fields);
    }

    private static bool HasAllBlockInstanceAttributeKeys(
        System.Collections.Specialized.NameValueCollection? strings,
        IReadOnlyList<TextFields.InstanceAttributeField> fields)
    {
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
                continue;

            if (strings?[field.Key] == null)
                return false;
        }

        return true;
    }

    private void EmulateAddMissingBlockAttributeKeys(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        var targets = objectIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
            return;

        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != doc.RuntimeSerialNumber)
        {
            RhinoApp.WriteLine("MoleHill: skipped AddMissingBlockAttributeKeys emulation because the document is not active.");
            return;
        }

        var previousSelection = doc.Objects
            .GetSelectedObjects(false, false)
            .Select(item => item.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        using var _ = new EventSuppression(this);
        try
        {
            doc.Objects.UnselectAll();
            foreach (Guid objectId in targets)
                doc.Objects.Select(objectId, true, true);

            bool ran = RhinoApp.RunScript(AddMissingBlockAttributeKeysCommand, false);
            if ((!ran || HasMissingBlockAttributeKeys(doc, targets)) &&
                RhinoApp.RunScript(AddMissingBlockAttributeKeysAllInstancesCommand, false))
            {
                // Rhino only exposes this repair via command execution.
            }
        }
        finally
        {
            doc.Objects.UnselectAll();
            foreach (Guid objectId in previousSelection)
                doc.Objects.Select(objectId, true, true);
        }
    }

    private static bool SetUserStringPreservingEmpty(CommonObject target, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (TryInvokeInternalSetUserString(target, key, value))
            return true;

        return target switch
        {
            ObjectAttributes attributes => attributes.SetUserString(key, value),
            GeometryBase geometry => geometry.SetUserString(key, value),
            InstanceDefinition definition => definition.SetUserString(key, value),
            _ => false
        };
    }

    private static bool TryInvokeInternalSetUserString(CommonObject target, string key, string value)
    {
        if (InternalSetUserStringMethod == null)
            return false;

        try
        {
            object? result = InternalSetUserStringMethod.Invoke(target, new object?[] { key, value });
            return result is not bool success || success;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasMissingBlockAttributeKeys(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        foreach (Guid objectId in objectIds)
        {
            if (doc.Objects.FindId(objectId) is not InstanceObject instanceObject)
                continue;

            var definition = instanceObject.InstanceDefinition;
            if (definition == null)
                continue;

            if (!HasAllBlockInstanceAttributeKeys(
                    instanceObject.Attributes.GetUserStrings(),
                    TextFields.GetInstanceAttributeFields(definition)))
            {
                return true;
            }
        }

        return false;
    }

    private void QueuePendingBlockAttributeKeyRepair(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        var targets = objectIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
            return;

        if (!_pendingBlockAttributeKeyRepairs.TryGetValue(doc.RuntimeSerialNumber, out var pendingIds))
        {
            pendingIds = new HashSet<Guid>();
            _pendingBlockAttributeKeyRepairs[doc.RuntimeSerialNumber] = pendingIds;
        }

        foreach (Guid objectId in targets)
            pendingIds.Add(objectId);
    }

    private Guid AddBakedObject(
        RhinoDoc doc,
        TerrainDefinition terrain,
        GeneratedRhinoObject generated,
        ICollection<Guid>? blockAttributeRefreshIds = null)
    {
        var attributes = CreateAttributes(doc, terrain, generated, trackOwnership: false);
        ApplyInstanceUserStrings(attributes, generated.InstanceUserStrings);

        if (!string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
        {
            int definitionIndex = EnsureBlockDefinition(doc, generated.InstanceDefinitionName!, generated.MarkerBlockTemplate);
            if (definitionIndex >= 0)
            {
                var definition = doc.InstanceDefinitions[definitionIndex];
                if (definition != null)
                {
                    var instanceGeometry = CreateInstanceReferenceGeometry(definition.Id, generated.InstanceTransform, generated.InstanceUserStrings);
                    Guid id = doc.Objects.Add(instanceGeometry, attributes);
                    if (id != Guid.Empty && !EnsureBlockInstanceAttributeKeys(doc, id, definition))
                        blockAttributeRefreshIds?.Add(id);

                    return id;
                }
            }
        }

        return generated.Geometry switch
        {
            Mesh mesh => doc.Objects.AddMesh(mesh, attributes),
            Brep brep => doc.Objects.AddBrep(PrepareBrepForBake(doc, brep), attributes),
            Curve curve => doc.Objects.AddCurve(curve, attributes),
            TextDot textDot => doc.Objects.AddTextDot(textDot, attributes),
            _ => Guid.Empty
        };
    }

    private static Brep PrepareBrepForBake(RhinoDoc doc, Brep brep)
    {
        Brep baked = brep.DuplicateBrep();
        baked.MergeCoplanarFaces(doc.ModelAbsoluteTolerance, doc.ModelAngleToleranceRadians);
        return baked;
    }

    private static void ApplyBakedColorTransparency(RhinoDoc doc, int colorArgb, ObjectAttributes attributes)
    {
        var color = System.Drawing.Color.FromArgb(colorArgb);
        double transparency = 1.0 - (color.A / 255.0);
        if (transparency <= 0.0)
            return;

        var material = new Material
        {
            DiffuseColor = GetOpaqueColor(color),
            Transparency = transparency,
            Name = $"__MoleHillBakedColor__{colorArgb:X8}"
        };

        int materialIndex = doc.Materials.Find(material.Name, true);
        if (materialIndex >= 0)
            doc.Materials.Modify(material, materialIndex, quiet: true);
        else
            materialIndex = doc.Materials.Add(material);

        if (materialIndex >= 0)
        {
            attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
            attributes.MaterialIndex = materialIndex;
        }
    }

    private int EnsureLayer(RhinoDoc doc, string fullPath, string? sourceLayerPath = null)
    {
        global::Rhino.DocObjects.Layer? sourceLayer = TryGetSourceLayer(doc, sourceLayerPath);
        int existingIndex = doc.Layers.FindByFullPath(fullPath, -1);
        if (existingIndex >= 0)
        {
            SyncLayerAppearance(doc, existingIndex, sourceLayer);
            return existingIndex;
        }

        int parentIndex = -1;
        string currentPath = string.Empty;

        foreach (var segment in fullPath.Split(new[] { "::" }, StringSplitOptions.None))
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? segment : $"{currentPath}::{segment}";
            int index = doc.Layers.FindByFullPath(currentPath, -1);
            if (index >= 0)
            {
                parentIndex = index;
                continue;
            }

            var layer = new Layer { Name = segment };
            if (parentIndex >= 0)
                layer.ParentLayerId = doc.Layers[parentIndex].Id;

            if (sourceLayer != null && string.Equals(currentPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                layer.Color = sourceLayer.Color;
                layer.PlotColor = sourceLayer.PlotColor;
            }

            parentIndex = doc.Layers.Add(layer);
        }

        return parentIndex >= 0 ? parentIndex : doc.Layers.CurrentLayerIndex;
    }

    private static global::Rhino.DocObjects.Layer? TryGetSourceLayer(RhinoDoc doc, string? sourceLayerPath)
    {
        if (string.IsNullOrWhiteSpace(sourceLayerPath))
            return null;

        int sourceIndex = doc.Layers.FindByFullPath(sourceLayerPath, -1);
        return sourceIndex >= 0 ? doc.Layers[sourceIndex] : null;
    }

    private static void SyncLayerAppearance(RhinoDoc doc, int layerIndex, global::Rhino.DocObjects.Layer? sourceLayer)
    {
        if (sourceLayer == null || layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return;

        var existing = doc.Layers[layerIndex];
        if (existing == null ||
            existing.Color == sourceLayer.Color &&
            existing.PlotColor == sourceLayer.PlotColor)
            return;

        var updated = existing;
        updated.Color = sourceLayer.Color;
        updated.PlotColor = sourceLayer.PlotColor;
        doc.Layers.Modify(updated, layerIndex, quiet: true);
    }

    private void DeleteOwnedObjects(RhinoDoc doc, TerrainDefinition terrain)
    {
        using var _ = new EventSuppression(this);
        DeleteObjects(doc, terrain.OutputObjectIds);
        DeleteObjects(doc, terrain.ZoneObjectIds);
        DeleteObjects(doc, terrain.AuxiliaryObjectIds);
        DeleteObjects(doc, terrain.MarkerObjectIds);
        terrain.OutputObjectIds.Clear();
        terrain.ZoneObjectIds.Clear();
        terrain.AuxiliaryObjectIds.Clear();
        terrain.MarkerObjectIds.Clear();
    }

    private void RestoreTerrainObjectPlacements(RhinoDoc doc, TerrainDefinition terrain)
    {
        foreach (var definition in terrain.Objects)
            RestorePlacementStates(doc, definition.PlacementStates);
    }

    private void RestorePlacementStates(RhinoDoc doc, IList<TerrainObjectPlacementState> placementStates)
    {
        if (placementStates.Count == 0)
            return;

        using var _ = new EventSuppression(this);
        foreach (var state in placementStates.ToList())
        {
            Transform appliedTransform = state.GetLastAppliedTransform();
            if (IsIdentityTransform(appliedTransform))
                continue;

            if (!TryGetInverse(appliedTransform, out Transform inverse))
                continue;

            if (!TryTransformSourceObject(doc, state.ObjectId, inverse, out Guid newObjectId))
                continue;

            if (newObjectId != state.ObjectId)
                ReplaceSourceObjectReferences(doc, state.ObjectId, newObjectId);

            state.SetLastAppliedTransform(Transform.Identity);
        }
    }

    private void PurgeOrphanedOwnedObjects(RhinoDoc doc, TerrainDefinition terrain)
    {
        var protectedIds = new HashSet<Guid>(AllOwnedIds(terrain).Concat(terrain.BakedObjectIds));
        string terrainIdString = terrain.TerrainId.ToString();

        var orphanedIds = doc.Objects
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
                !protectedIds.Contains(obj.Id))
            .Select(obj => obj.Id)
            .ToList();

        if (orphanedIds.Count > 0)
        {
            using var _ = new EventSuppression(this);
            DeleteObjects(doc, orphanedIds);
        }
    }

    private static void DeleteObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        var ids = objectIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return;

        foreach (var id in ids)
        {
            doc.Objects.Show(id, ignoreLayerMode: true);
            doc.Objects.Unlock(id, ignoreLayerMode: true);
        }

        doc.Objects.Delete(ids, quiet: true);
    }

    private void ClearOwnership(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        using var _ = new EventSuppression(this);
        foreach (var objectId in objectIds)
        {
            var obj = doc.Objects.FindId(objectId);
            if (obj?.Attributes == null)
                continue;

            var attributes = obj.Attributes.Duplicate();
            attributes.DeleteUserString(OutputOwnerKey);
            attributes.DeleteUserString(OutputBaseMaterialNameKey);
            doc.Objects.ModifyAttributes(objectId, attributes, quiet: true);
        }
    }

    private void OnAddRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.TheObject == null)
            return;

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
        if (e.NewRhinoObject != null)
            ReplaceSourceObjectReferences(e.Document, e.OldRhinoObject.Id, e.NewRhinoObject.Id);

        ScheduleSourceReferencePrune(e.Document);
    }

    private void OnUndeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        if (_suppressDocEvents > 0 || e.TheObject == null)
            return;

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

        ScheduleTerrainsForLayerChanges(
            e.Document,
            e.OldState?.FullPath,
            e.NewState?.FullPath);
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

        if (TryCompleteFinishedBuild())
            return;

        if (_pendingRebuilds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var nextItem = _pendingRebuilds
            .Where(item => item.Value.DueAtUtc <= now)
            .OrderBy(item => item.Value.DueAtUtc)
            .ThenBy(item => item.Key.mode == TerrainBuildMode.Preview ? 0 : 1)
            .Select(item => ((uint docSerial, Guid terrainId, TerrainBuildMode mode)?)item.Key)
            .FirstOrDefault();

        if (!nextItem.HasValue)
            return;

        var key = nextItem.Value;
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
            return;

        long skippedVersion = key.mode == TerrainBuildMode.Preview
            ? rebuildState.SkippedPreviewVersion
            : rebuildState.SkippedFinalVersion;
        if (_pendingRebuilds.TryGetValue(key, out var request) && skippedVersion >= request.Version)
        {
            _pendingRebuilds.Remove(key);
            return;
        }

        _pendingRebuilds.Remove(key);
        StartBackgroundBuild(doc, state, terrain, key.mode, rebuildState.RequestedVersion);
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

            CompleteBackgroundBuild(doc, state, terrain, rebuildState, workerTask.Result);
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

            foreach (var definition in terrain.Objects)
            {
                foreach (var placementState in definition.PlacementStates)
                    changed |= placementState.ReplaceObject(oldObjectId, newObjectId);
            }
        }

        if (changed)
            Save(doc, state);
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

    private static ModifierDefinition? CreateModifier(string modifierKind) => modifierKind switch
    {
        "triangulate" => new TriangulateModifierDefinition(),
        "add-geometry" => new AddGeometryModifierDefinition(),
        "remesh" => new RemeshModifierDefinition(),
        "smooth" => new SmoothModifierDefinition(),
        "retaining-wall" => new RetainingWallModifierDefinition(),
        "grade-pad" => new GradePadModifierDefinition(),
        "grade-path" => new GradePathModifierDefinition(),
        "in-situ-stair" => new InSituStairModifierDefinition(),
        _ => null
    };

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
        if (_runtimeCaches.TryGetValue((docSerial, terrainId), out var cache))
            cache.Clear();
        _runtimeCaches.Remove((docSerial, terrainId));
    }

    private void ClearRuntimeCaches(uint docSerial)
    {
        foreach (var key in _runtimeCaches.Keys.Where(key => key.docSerial == docSerial).ToList())
            _runtimeCaches.Remove(key);
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

        rebuildState.WorkerCancellation?.Cancel();
        rebuildState.WorkerCancellation?.Dispose();
        rebuildState.WorkerCancellation = null;
        rebuildState.WorkerTask = null;
        rebuildState.IsBuilding = false;
        rebuildState.RunningVersion = 0;
        rebuildState.CancelRequested = false;
    }

    private void UpdateDisplayState(RhinoDoc doc, TerrainDefinition terrain, TerrainRuntimeCache runtimeCache, TerrainBuildResult build)
    {
        var displayState = new TerrainDisplayState
        {
            IsPreview = build.Mode == TerrainBuildMode.Preview,
            HasDeferredOutputs = build.HasDeferredOutputs,
            TerrainMesh = build.PrimaryMesh,
            BaseTerrainMesh = build.BaseMesh
        };
        if (build.Mode == TerrainBuildMode.Final)
            displayState.AnalysisResults.AddRange(build.AnalysisResults);
        if (build.Mode == TerrainBuildMode.Final)
        {
            displayState.ZoneObjects.AddRange(build.ZoneObjects);
            displayState.AuxiliaryObjects.AddRange(build.AuxiliaryObjects);
            displayState.MarkerObjects.AddRange(build.MarkerObjects);
        }
        runtimeCache.DisplayState = displayState;
        UpdateRuntimePreview(doc, terrain, runtimeCache);
    }

    private void UpdateRuntimePreview(RhinoDoc doc, TerrainDefinition terrain, TerrainRuntimeCache runtimeCache)
    {
        if (runtimeCache.DisplayState == null)
            return;

        if (runtimeCache.DisplayState.IsPreview)
        {
            runtimeCache.DisplayState.PreviewTerrainMesh = runtimeCache.DisplayState.TerrainMesh;
            runtimeCache.DisplayState.ActiveAnalysisId = null;
            runtimeCache.DisplayState.ActiveAnalysisLabel = null;
            terrain.LastAnalysisResults.Clear();
            return;
        }

        TerrainAnalysisPreviewBuilder.UpdatePreviewMesh(doc, terrain, runtimeCache.DisplayState);
        terrain.LastAnalysisResults = TerrainRuntimeCacheCloner.CloneAnalyses(runtimeCache.DisplayState.AnalysisResults);
    }

    private static string DescribeDisplayState(TerrainDisplayState? displayState)
    {
        if (displayState == null)
            return "no preview";

        int outputCount = (displayState.PreviewTerrainMesh != null ? 1 : 0) +
                          displayState.ZoneObjects.Count +
                          displayState.AuxiliaryObjects.Count +
                          displayState.MarkerObjects.Count;
        return displayState.IsPreview
            ? $"{outputCount:N0} preview outputs; deferred exact outputs"
            : $"{outputCount:N0} preview outputs";
    }

    private void ClearLegacyOutputState(RhinoDoc doc, TerrainDefinition terrain)
    {
        if (AllOwnedIds(terrain).Any())
            DeleteOwnedObjects(doc, terrain);

        terrain.OutputObjectIds.Clear();
        terrain.ZoneObjectIds.Clear();
        terrain.AuxiliaryObjectIds.Clear();
        terrain.MarkerObjectIds.Clear();
    }

    private void SyncTerrainObjects(RhinoDoc doc, TerrainDefinition terrain, TerrainBuildResult build)
    {
        if (terrain.Objects.Count == 0)
            return;

        var placementsByDefinition = build.ObjectPlacements.ToDictionary(group => group.DefinitionId);
        var crossTerrainConflicts = CollectCrossTerrainObjectConflicts(
            doc,
            terrain.TerrainId,
            build.ObjectPlacements.SelectMany(group => group.Placements).Select(placement => placement.ObjectId));

        foreach (var objectId in crossTerrainConflicts.OrderBy(id => id))
            build.Diagnostics.Add($"Objects skipped {FormatObjectRef(objectId)} because it is matched by another terrain.");

        using var _ = new EventSuppression(this);
        foreach (var definition in terrain.Objects)
        {
            var currentStates = definition.PlacementStates
                .Where(state => state.ObjectId != Guid.Empty)
                .ToDictionary(state => state.ObjectId, state => state);

            var targetPlacements = placementsByDefinition.TryGetValue(definition.Id, out var placementGroup)
                ? placementGroup.Placements
                    .Where(placement => !crossTerrainConflicts.Contains(placement.ObjectId))
                    .GroupBy(placement => placement.ObjectId)
                    .ToDictionary(group => group.Key, group => group.Last())
                : new Dictionary<Guid, TerrainObjectPlacement>();

            foreach (var state in definition.PlacementStates.ToList())
            {
                if (targetPlacements.ContainsKey(state.ObjectId))
                    continue;

                TryRestorePlacementState(doc, state);
                definition.PlacementStates.Remove(state);
            }

            foreach (var pair in targetPlacements)
            {
                var placement = pair.Value;
                currentStates.TryGetValue(placement.ObjectId, out var state);
                Transform previousTransform = state?.GetLastAppliedTransform() ?? Transform.Identity;
                if (!TryCreatePlacementDelta(previousTransform, placement.AppliedTransform, out Transform delta))
                {
                    build.Diagnostics.Add($"Objects skipped {FormatObjectRef(placement.ObjectId)} because its placement delta could not be computed.");
                    continue;
                }

                Guid liveObjectId = placement.ObjectId;
                if (!TryTransformSourceObject(doc, liveObjectId, delta, out Guid newObjectId))
                {
                    build.Diagnostics.Add($"Objects skipped {FormatObjectRef(placement.ObjectId)} because Rhino could not apply the placement transform.");
                    continue;
                }

                if (newObjectId != liveObjectId)
                    ReplaceSourceObjectReferences(doc, liveObjectId, newObjectId);

                if (state == null)
                {
                    state = new TerrainObjectPlacementState
                    {
                        ObjectId = newObjectId
                    };
                    definition.PlacementStates.Add(state);
                }
                else
                {
                    state.ObjectId = newObjectId;
                }

                if (IsIdentityTransform(placement.AppliedTransform))
                    definition.PlacementStates.Remove(state);
                else
                    state.SetLastAppliedTransform(placement.AppliedTransform);
            }
        }
    }

    private HashSet<Guid> CollectCrossTerrainObjectConflicts(RhinoDoc doc, Guid terrainId, IEnumerable<Guid> candidateObjectIds)
    {
        var candidates = candidateObjectIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();

        if (candidates.Count == 0)
            return new HashSet<Guid>();

        var conflicts = new HashSet<Guid>();
        foreach (var otherTerrain in GetState(doc).Terrains)
        {
            if (otherTerrain.TerrainId == terrainId)
                continue;

            foreach (var definition in otherTerrain.Objects.Where(item => item.IsEnabled))
            {
                foreach (var obj in RhinoSourceResolver.ResolveObjects(doc, definition.Sources))
                {
                    if (candidates.Contains(obj.Id))
                        conflicts.Add(obj.Id);
                }
            }
        }

        return conflicts;
    }

    private bool TryRestorePlacementState(RhinoDoc doc, TerrainObjectPlacementState state)
    {
        Transform appliedTransform = state.GetLastAppliedTransform();
        if (IsIdentityTransform(appliedTransform))
            return true;

        if (!TryGetInverse(appliedTransform, out Transform inverse))
            return false;

        Guid liveObjectId = state.ObjectId;
        if (!TryTransformSourceObject(doc, liveObjectId, inverse, out Guid newObjectId))
            return false;

        if (newObjectId != liveObjectId)
            ReplaceSourceObjectReferences(doc, liveObjectId, newObjectId);

        state.ObjectId = newObjectId;
        state.SetLastAppliedTransform(Transform.Identity);
        return true;
    }

    private static bool TryCreatePlacementDelta(Transform previousTransform, Transform targetTransform, out Transform delta)
    {
        delta = Transform.Identity;
        if (!TryGetInverse(previousTransform, out Transform inversePrevious))
            return false;

        delta = targetTransform * inversePrevious;
        return true;
    }

    private static bool TryTransformSourceObject(RhinoDoc doc, Guid objectId, Transform transform, out Guid newObjectId)
    {
        newObjectId = objectId;
        if (objectId == Guid.Empty || IsIdentityTransform(transform))
            return objectId != Guid.Empty;

        newObjectId = doc.Objects.Transform(objectId, transform, deleteOriginal: true);
        return newObjectId != Guid.Empty;
    }

    private static bool TryGetInverse(Transform transform, out Transform inverse)
    {
        if (IsIdentityTransform(transform))
        {
            inverse = Transform.Identity;
            return true;
        }

        return transform.TryGetInverse(out inverse);
    }

    private static bool IsIdentityTransform(Transform transform, double tolerance = 1e-9)
    {
        return Math.Abs(transform.M00 - 1.0) <= tolerance &&
               Math.Abs(transform.M01) <= tolerance &&
               Math.Abs(transform.M02) <= tolerance &&
               Math.Abs(transform.M03) <= tolerance &&
               Math.Abs(transform.M10) <= tolerance &&
               Math.Abs(transform.M11 - 1.0) <= tolerance &&
               Math.Abs(transform.M12) <= tolerance &&
               Math.Abs(transform.M13) <= tolerance &&
               Math.Abs(transform.M20) <= tolerance &&
               Math.Abs(transform.M21) <= tolerance &&
               Math.Abs(transform.M22 - 1.0) <= tolerance &&
               Math.Abs(transform.M23) <= tolerance &&
               Math.Abs(transform.M30) <= tolerance &&
               Math.Abs(transform.M31) <= tolerance &&
               Math.Abs(transform.M32) <= tolerance &&
               Math.Abs(transform.M33 - 1.0) <= tolerance;
    }

    private static string FormatObjectRef(Guid objectId)
    {
        return objectId == Guid.Empty
            ? "object"
            : objectId.ToString("N")[..8];
    }

    private void ApplyVisibilityAndLock(RhinoDoc doc, TerrainDefinition terrain)
    {
        foreach (var id in terrain.OutputObjectIds)
        {
            if (!terrain.IsVisible || !terrain.ShowTerrainMesh)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }

        foreach (var id in terrain.ZoneObjectIds)
        {
            if (!terrain.IsVisible || !terrain.ShowZoneMeshes)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }

        foreach (var id in terrain.AuxiliaryObjectIds)
        {
            var obj = doc.Objects.FindId(id);
            bool shouldHide = !terrain.IsVisible ||
                              (!terrain.ShowSlopePreview && IsSlopePreviewObject(obj)) ||
                              (!terrain.ShowAnalysisOutputs && IsSlopePreviewObject(obj)) ||
                              !ShouldDisplayOwnedAnalysisOutput(terrain, obj?.Attributes);
            if (shouldHide)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }

        foreach (var id in terrain.MarkerObjectIds)
        {
            if (!terrain.IsVisible)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }
    }

    private void ApplyDisplayState(RhinoDoc doc, TerrainDefinition terrain)
    {
        using var _ = new EventSuppression(this);
        ResetOwnedObjectModes(doc, terrain);
        ApplyOwnedDisplayMaterials(doc, terrain);
        ApplyVisibilityAndLock(doc, terrain);
        UnselectProtectedOwnedObjects(doc, terrain);
    }

    private void ResetOwnedObjectModes(RhinoDoc doc, TerrainDefinition terrain)
    {
        foreach (var id in AllOwnedIds(terrain))
        {
            doc.Objects.Show(id, ignoreLayerMode: true);
            doc.Objects.Unlock(id, ignoreLayerMode: true);
        }
    }

    private static IEnumerable<Guid> AllOwnedIds(TerrainDefinition terrain) =>
        terrain.OutputObjectIds
            .Concat(terrain.ZoneObjectIds)
            .Concat(terrain.AuxiliaryObjectIds)
            .Concat(terrain.MarkerObjectIds);

    private bool TrySyncOwnedObjectLayer(RhinoDoc doc, Guid objectId, int newLayerIndex)
    {
        string? newLayerPath = GetLayerPath(doc, newLayerIndex);
        if (string.IsNullOrWhiteSpace(newLayerPath))
            return false;

        var state = GetState(doc);
        foreach (var terrain in state.Terrains)
        {
            if (terrain.OutputObjectIds.Contains(objectId))
            {
                if (string.Equals(terrain.TerrainLayerPath, newLayerPath, StringComparison.OrdinalIgnoreCase))
                    return true;

                terrain.TerrainLayerPath = newLayerPath;
                Save(doc, state);
                ApplyDisplayState(doc, terrain);
                doc.Views.Redraw();
                return true;
            }

            if (terrain.AuxiliaryObjectIds.Contains(objectId))
            {
                if (string.Equals(terrain.AuxiliaryLayerPath, newLayerPath, StringComparison.OrdinalIgnoreCase))
                    return true;

                terrain.AuxiliaryLayerPath = newLayerPath;
                Save(doc, state);
                ApplyDisplayState(doc, terrain);
                doc.Views.Redraw();
                return true;
            }
        }

        return false;
    }

    private void OnRestoreStateUndo(object? sender, global::Rhino.Commands.CustomUndoEventArgs e)
    {
        if (e.Tag is not UndoState snapshot)
            return;

        RestoreUndoState(e.Document, snapshot);
        e.Document.AddCustomUndoEvent("Detach MoleHill Terrain", OnRestoreStateUndo, CaptureUndoState(GetState(e.Document)));
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void ApplyOwnedDisplayMaterials(RhinoDoc doc, TerrainDefinition terrain)
    {
        foreach (var objectId in AllOwnedIds(terrain).Distinct())
        {
            var obj = doc.Objects.FindId(objectId);
            if (obj?.Attributes == null)
                continue;

            var attributes = obj.Attributes.Duplicate();
            ApplyOutputRenderAttributes(doc, terrain, attributes);
            doc.Objects.ModifyAttributes(objectId, attributes, quiet: true);
        }
    }

    private void ApplyOutputRenderAttributes(RhinoDoc doc, TerrainDefinition terrain, ObjectAttributes attributes)
    {
        ApplyOutputWireAttributes(terrain, attributes);
        string? baseMaterialName = attributes.GetUserString(OutputBaseMaterialNameKey);
        if (string.IsNullOrWhiteSpace(baseMaterialName))
        {
            baseMaterialName = TryInferBaseMaterialName(doc, attributes);
            if (!string.IsNullOrWhiteSpace(baseMaterialName))
                attributes.SetUserString(OutputBaseMaterialNameKey, baseMaterialName);
        }

        double transparency = Math.Clamp(terrain.OutputTransparencyPercent, 0, 100) / 100.0;
        if (!string.IsNullOrWhiteSpace(baseMaterialName))
        {
            if (transparency <= 0)
            {
                int materialIndex = EnsureBaseMaterial(doc, baseMaterialName);
                if (materialIndex >= 0)
                {
                    attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                    attributes.MaterialIndex = materialIndex;
                }

                return;
            }

            int displayMaterialIndex = EnsureDisplayMaterial(
                doc,
                terrain.TerrainId,
                $"material:{baseMaterialName}",
                GetEffectiveDisplayColor(doc, attributes),
                transparency,
                baseMaterialName);
            if (displayMaterialIndex >= 0)
            {
                attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                attributes.MaterialIndex = displayMaterialIndex;
            }

            return;
        }

        attributes.DeleteUserString(OutputBaseMaterialNameKey);
        if (transparency <= 0)
        {
            attributes.MaterialSource = ObjectMaterialSource.MaterialFromLayer;
            return;
        }

        var effectiveColor = GetEffectiveDisplayColor(doc, attributes);
        int colorMaterialIndex = EnsureDisplayMaterial(
            doc,
            terrain.TerrainId,
            $"color:{effectiveColor.ToArgb():X8}",
            effectiveColor,
            transparency,
            null);
        if (colorMaterialIndex >= 0)
        {
            attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
            attributes.MaterialIndex = colorMaterialIndex;
        }
    }

    private static void ApplyOutputWireAttributes(TerrainDefinition terrain, ObjectAttributes attributes)
    {
        attributes.WireDensity = terrain.ShowMeshWires ? 1 : -1;
    }

    private static string? TryInferBaseMaterialName(RhinoDoc doc, ObjectAttributes attributes)
    {
        if (attributes.MaterialSource != ObjectMaterialSource.MaterialFromObject || attributes.MaterialIndex < 0)
            return null;

        if (attributes.MaterialIndex >= doc.Materials.Count)
            return null;

        var material = doc.Materials[attributes.MaterialIndex];
        if (material == null || string.IsNullOrWhiteSpace(material.Name) || IsOwnedDisplayMaterialName(material.Name))
            return null;

        return material.Name;
    }

    private static bool IsSlopePreviewObject(global::Rhino.DocObjects.RhinoObject? obj)
    {
        return GetGeneratedObjectKind(obj?.Attributes) == GeneratedObjectKind.SlopePreview;
    }

    private static bool ShouldDisplayOwnedAnalysisOutput(TerrainDefinition terrain, ObjectAttributes? attributes)
    {
        Guid? analysisId = GetGeneratedAnalysisId(attributes);
        if (!analysisId.HasValue)
            return true;

        if (!terrain.ShowAnalysisOutputs)
            return false;

        return terrain.Analyses.Any(analysis => analysis.Id == analysisId.Value && analysis.IsEnabled);
    }

    private static GeneratedObjectKind GetGeneratedObjectKind(ObjectAttributes? attributes)
    {
        if (attributes == null)
            return GeneratedObjectKind.Default;

        string? kind = attributes.GetUserString(OutputKindKey);
        return Enum.TryParse(kind, ignoreCase: true, out GeneratedObjectKind parsed)
            ? parsed
            : GeneratedObjectKind.Default;
    }

    private static Guid? GetGeneratedAnalysisId(ObjectAttributes? attributes)
    {
        if (attributes == null)
            return null;

        string? analysisId = attributes.GetUserString(OutputAnalysisIdKey);
        return Guid.TryParse(analysisId, out Guid parsed)
            ? parsed
            : null;
    }

    private int EnsureDisplayMaterial(
        RhinoDoc doc,
        Guid terrainId,
        string baseKey,
        System.Drawing.Color effectiveColor,
        double transparency,
        string? baseMaterialName)
    {
        string displayMaterialName = GetDisplayMaterialName(terrainId, baseKey);
        Material material = CreateDisplayMaterial(doc, effectiveColor, transparency, baseMaterialName);
        material.Name = displayMaterialName;

        int materialIndex = doc.Materials.Find(displayMaterialName, true);
        if (materialIndex >= 0)
        {
            doc.Materials.Modify(material, materialIndex, quiet: true);
            return materialIndex;
        }

        return doc.Materials.Add(material);
    }

    private Material CreateDisplayMaterial(
        RhinoDoc doc,
        System.Drawing.Color effectiveColor,
        double transparency,
        string? baseMaterialName)
    {
        Material material;
        if (!string.IsNullOrWhiteSpace(baseMaterialName))
        {
            int baseMaterialIndex = EnsureBaseMaterial(doc, baseMaterialName);
            material = baseMaterialIndex >= 0
                ? new Material(doc.Materials[baseMaterialIndex])
                : new Material();
            material.Transparency = Math.Max(material.Transparency, transparency);
            return material;
        }

        var opaqueColor = GetOpaqueColor(effectiveColor);
        material = new Material
        {
            DiffuseColor = opaqueColor,
            Transparency = transparency
        };
        return material;
    }

    private static System.Drawing.Color GetEffectiveDisplayColor(RhinoDoc doc, ObjectAttributes attributes)
    {
        if (attributes.ColorSource == ObjectColorSource.ColorFromObject)
            return GetOpaqueColor(attributes.ObjectColor);

        if (attributes.LayerIndex >= 0 && attributes.LayerIndex < doc.Layers.Count)
            return GetOpaqueColor(doc.Layers[attributes.LayerIndex].Color);

        return System.Drawing.Color.FromArgb(180, 180, 180);
    }

    private static System.Drawing.Color GetOpaqueColor(System.Drawing.Color color)
    {
        return System.Drawing.Color.FromArgb(color.R, color.G, color.B);
    }

    private int EnsureBaseMaterial(RhinoDoc doc, string materialName)
    {
        int materialIndex = doc.Materials.Find(materialName, true);
        if (materialIndex >= 0)
            return materialIndex;

        var material = new Material { Name = materialName };
        return doc.Materials.Add(material);
    }

    private void UnselectProtectedOwnedObjects(RhinoDoc doc, TerrainDefinition terrain)
    {
        if (!terrain.ProtectOutput)
            return;

        foreach (var objectId in AllOwnedIds(terrain).Distinct())
            doc.Objects.Select(objectId, false, true);
    }

    private bool ShouldBlockSelection(RhinoDoc doc, RhinoObject obj)
    {
        string? ownerValue = obj.Attributes.GetUserString(OutputOwnerKey);
        if (!Guid.TryParse(ownerValue, out var terrainId))
            return false;

        var terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        return terrain?.ProtectOutput == true;
    }

    private static bool IsOwnedDisplayMaterialName(string? materialName)
    {
        return !string.IsNullOrWhiteSpace(materialName) &&
            materialName.StartsWith(OutputDisplayMaterialPrefix, StringComparison.Ordinal);
    }

    private static string GetDisplayMaterialName(Guid terrainId, string baseKey)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseKey)));
        return $"{OutputDisplayMaterialPrefix}{terrainId:N}_{hash[..16]}";
    }

    private sealed class DocumentState
    {
        public List<TerrainDefinition> Terrains { get; set; } = new();
        public Guid? SelectedTerrainId { get; set; }
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
