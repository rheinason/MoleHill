using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainController
{
    private const string OutputOwnerKey = "MoleHillTerrainId";
    private const string OutputBaseMaterialNameKey = "MoleHillBaseMaterialName";
    private const string OutputKindKey = "MoleHillOutputKind";
    private const string OutputDisplayMaterialPrefix = "__MoleHillDisplay__";
    private const int StageTimingDiagnosticThresholdMs = 250;
    private const int MinorTimingDiagnosticThresholdMs = 100;
    private const int TotalTimingDiagnosticThresholdMs = 750;
    private readonly TerrainDocumentStore _documentStore = new();
    private readonly TerrainBuildService _buildService = new();
    private readonly TerrainDisplayConduit _displayConduit = new();
    private readonly Dictionary<uint, DocumentState> _states = new();
    private readonly Dictionary<(uint docSerial, Guid terrainId), DateTime> _pendingRebuilds = new();
    private readonly HashSet<uint> _pendingSourceReferencePrunes = new();
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

    private sealed class TerrainRebuildState
    {
        public long RequestedVersion { get; set; }

        public long AppliedVersion { get; set; }

        public long RunningVersion { get; set; }

        public bool IsBuilding { get; set; }

        public bool CancelRequested { get; set; }
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
            TerrainLayerPath = doc.Layers.CurrentLayer?.FullPath,
            AuxiliaryLayerPath = "MoleHill::Auxiliary"
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

        DeleteOwnedObjects(doc, terrain);
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

            var analysis = terrain.Analyses[index];
            if (analysis is EarthworkAnalysisDefinition)
                return;

            var clone = CloneAnalysis(analysis);
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

    public void RemoveMarker(RhinoDoc doc, Guid terrainId, Guid markerId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            terrain.Markers.RemoveAll(marker => marker.Id == markerId);
        });
    }

    public void MutateTerrain(RhinoDoc doc, Guid terrainId, Action<TerrainDefinition> mutator, bool scheduleRebuild = true)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        mutator(terrain);
        terrain.EnsureBaseModifier();
        Save(doc, state);

        if (scheduleRebuild && terrain.LiveUpdateEnabled)
            ScheduleRebuild(doc, terrain.TerrainId);
        else
            RaiseStateChanged();
    }

    public void RebuildTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        _pendingRebuilds.Remove((doc.RuntimeSerialNumber, terrainId));
        long buildVersion = RequestRebuild(doc.RuntimeSerialNumber, terrainId, isImmediate: true);
        var rebuildState = GetRebuildState(doc.RuntimeSerialNumber, terrainId);
        if (rebuildState.IsBuilding)
        {
            terrain.LastBuildMessage = $"Queued rebuild #{buildVersion:N0}; current build will stop at the next safe checkpoint.";
            RaiseStateChanged();
            return;
        }

        RebuildTerrain(doc, state, terrain, buildVersion);
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
        clone.LastBuildMessage = null;
        clone.LastBuildUtc = null;

        foreach (var modifier in clone.Modifiers)
            modifier.Id = Guid.NewGuid();
        foreach (var marker in clone.Markers)
            marker.Id = Guid.NewGuid();
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
            RebuildTerrain(doc, state, terrain);
            runtimeCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId);
        }

        var displayState = runtimeCache.DisplayState;
        if (displayState == null || !terrain.IsVisible)
            return;

        using var _ = new EventSuppression(this);
        if (terrain.ShowTerrainMesh)
        {
            Mesh? terrainMesh = displayState.ActiveAnalysisId.HasValue
                ? displayState.PreviewTerrainMesh
                : displayState.TerrainMesh;
            if (terrainMesh != null)
            {
                AddBakedObject(doc, terrain, new GeneratedRhinoObject
                {
                    Geometry = terrainMesh.DuplicateMesh(),
                    Name = terrain.Name,
                    LayerPath = terrain.TerrainLayerPath,
                    ColorArgb = terrain.TerrainColorArgb
                });
            }
        }

        if (terrain.ShowZoneMeshes)
        {
            foreach (var zoneObject in displayState.ZoneObjects)
                AddBakedObject(doc, terrain, TerrainRuntimeCacheCloner.CloneGeneratedObject(zoneObject));
        }

        foreach (var auxiliary in displayState.AuxiliaryObjects)
            AddBakedObject(doc, terrain, TerrainRuntimeCacheCloner.CloneGeneratedObject(auxiliary));

        foreach (var marker in displayState.MarkerObjects)
            AddBakedObject(doc, terrain, TerrainRuntimeCacheCloner.CloneGeneratedObject(marker));

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
            GeometryFilter = objectFilter,
            GroupSelect = true,
            SubObjectSelect = false,
            DeselectAllBeforePostSelect = false
        };
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

    private void Save(RhinoDoc doc, DocumentState state)
    {
        _documentStore.Save(doc, state.Terrains);
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

    private void ScheduleRebuild(RhinoDoc doc, Guid terrainId)
    {
        long requestedVersion = RequestRebuild(doc.RuntimeSerialNumber, terrainId, isImmediate: false);
        _pendingRebuilds[(doc.RuntimeSerialNumber, terrainId)] = DateTime.UtcNow.AddMilliseconds(250);
        var terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain != null)
            terrain.LastBuildMessage = GetRebuildState(doc.RuntimeSerialNumber, terrainId).IsBuilding
                ? $"Queued rebuild #{requestedVersion:N0}; current build will stop at the next safe checkpoint."
                : $"Scheduled rebuild #{requestedVersion:N0}...";
        RaiseStateChanged();
    }

    private void RebuildTerrain(RhinoDoc doc, DocumentState state, TerrainDefinition terrain, long? requestedBuildVersion = null)
    {
        var rebuildState = GetRebuildState(doc.RuntimeSerialNumber, terrain.TerrainId);
        long buildVersion = requestedBuildVersion ?? Math.Max(rebuildState.RequestedVersion, rebuildState.AppliedVersion + 1);
        rebuildState.RequestedVersion = Math.Max(rebuildState.RequestedVersion, buildVersion);
        rebuildState.RunningVersion = buildVersion;
        rebuildState.CancelRequested = false;
        rebuildState.IsBuilding = true;

        terrain.LastBuildMessage = $"Building terrain #{buildVersion:N0}...";
        RaiseStateChanged();

        try
        {
            var rebuildTimer = Stopwatch.StartNew();

            var build = _buildService.Build(
                doc,
                terrain,
                GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId),
                () => ShouldCancelRebuild(doc.RuntimeSerialNumber, terrain.TerrainId, buildVersion));
            if (ShouldCancelRebuild(doc.RuntimeSerialNumber, terrain.TerrainId, buildVersion))
                throw new OperationCanceledException("A newer terrain rebuild request superseded this build.");
            var buildTiming = build.Timings.LastOrDefault(timing => string.Equals(timing.Stage, "Build pipeline", StringComparison.Ordinal));
            TimeSpan buildElapsed = buildTiming?.Elapsed ?? TimeSpan.Zero;

            terrain.LastBuildUtc = DateTimeOffset.UtcNow;
            terrain.LastAnalysis = build.Analysis;
            ClearLegacyOutputState(doc, terrain);

            var displayTimer = Stopwatch.StartNew();
            UpdateDisplayState(doc, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), build);
            displayTimer.Stop();
            build.RecordTiming("Display refresh", displayTimer.Elapsed, DescribeDisplayState(GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId).DisplayState), StageTimingDiagnosticThresholdMs);

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

            rebuildTimer.Stop();
            build.RecordTiming(
                "Rebuild total",
                rebuildTimer.Elapsed,
                $"build {FormatElapsed(buildElapsed)}, display {FormatElapsed(displayTimer.Elapsed)}, save {FormatElapsed(saveTimer.Elapsed)}, redraw {FormatElapsed(redrawTimer.Elapsed)}; {DescribeDisplayState(GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId).DisplayState)}",
                TotalTimingDiagnosticThresholdMs);

            terrain.LastBuildMessage = build.Diagnostics.Count == 0
                ? "Build succeeded."
                : string.Join(System.Environment.NewLine, build.Diagnostics.Take(8));
            rebuildState.AppliedVersion = buildVersion;
            RaiseStateChanged();
        }
        catch (OperationCanceledException)
        {
            terrain.LastBuildUtc = DateTimeOffset.UtcNow;
            terrain.LastBuildMessage = rebuildState.RequestedVersion > buildVersion
                ? $"Rebuild #{buildVersion:N0} cancelled; newer request queued."
                : $"Rebuild #{buildVersion:N0} cancelled.";
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            terrain.LastBuildUtc = DateTimeOffset.UtcNow;
            terrain.LastBuildMessage = $"Build failed: {ex.Message}";
            RhinoApp.WriteLine($"[MoleHill] Rebuild failed for '{terrain.Name}': {ex}");
            Save(doc, state);
        }
        finally
        {
            rebuildState.IsBuilding = false;
            rebuildState.RunningVersion = 0;

            if (rebuildState.RequestedVersion > rebuildState.AppliedVersion)
                _pendingRebuilds[(doc.RuntimeSerialNumber, terrain.TerrainId)] = DateTime.UtcNow.AddMilliseconds(75);
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

        using var _ = new EventSuppression(this);

        if (build.PrimaryMesh != null)
        {
            Guid id = AddGeneratedObject(doc, terrain, new GeneratedRhinoObject
            {
                Geometry = build.PrimaryMesh,
                Name = terrain.Name,
                LayerPath = terrain.TerrainLayerPath
            });

            if (id != Guid.Empty)
                outputIds.Add(id);
        }

        foreach (var zoneObject in build.ZoneObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, zoneObject);
            if (id != Guid.Empty)
                zoneIds.Add(id);
        }

        foreach (var auxiliary in build.AuxiliaryObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, new GeneratedRhinoObject
            {
                Geometry = auxiliary.Geometry,
                Name = auxiliary.Name,
                ColorArgb = auxiliary.ColorArgb,
                LayerPath = auxiliary.LayerPath ?? terrain.AuxiliaryLayerPath,
                SourceLayerPath = auxiliary.SourceLayerPath,
                MaterialName = auxiliary.MaterialName,
                InstanceDefinitionName = auxiliary.InstanceDefinitionName,
                MarkerBlockTemplate = auxiliary.MarkerBlockTemplate,
                InstanceTransform = auxiliary.InstanceTransform
            });
            if (id != Guid.Empty)
                auxiliaryIds.Add(id);
        }

        foreach (var marker in build.MarkerObjects)
        {
            Guid id = AddGeneratedObject(doc, terrain, marker);
            if (id != Guid.Empty)
                markerIds.Add(id);
        }

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

    private Guid AddGeneratedObject(RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        var attributes = CreateAttributes(doc, terrain, generated, trackOwnership: true);

        if (!string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
        {
            int definitionIndex = EnsureBlockDefinition(doc, generated.InstanceDefinitionName!, generated.MarkerBlockTemplate);
            if (definitionIndex >= 0)
                return doc.Objects.AddInstanceObject(definitionIndex, generated.InstanceTransform, attributes);
        }

        return generated.Geometry switch
        {
            Mesh mesh => doc.Objects.AddMesh(mesh, attributes),
            Brep brep => doc.Objects.AddBrep(brep, attributes),
            TextDot textDot => doc.Objects.AddTextDot(textDot, attributes),
            _ => Guid.Empty
        };
    }

    private static int EnsureBlockDefinition(RhinoDoc doc, string definitionName, MarkerBlockTemplate template)
    {
        var existingDefinition = doc.InstanceDefinitions.Find(definitionName);
        if (existingDefinition != null)
            return existingDefinition.Index;

        var geometry = CreateMarkerBlockGeometry(template);
        if (geometry.Count == 0)
            return -1;

        var attributes = geometry.Select(_ => new ObjectAttributes()).ToList();
        return doc.InstanceDefinitions.Add(definitionName, string.Empty, Point3d.Origin, geometry, attributes);
    }

    private static List<GeometryBase> CreateMarkerBlockGeometry(MarkerBlockTemplate template)
    {
        var geometry = new List<GeometryBase>();

        switch (template)
        {
            case MarkerBlockTemplate.Elevation:
                geometry.Add(new Circle(Plane.WorldXY, 0.8).ToNurbsCurve());
                geometry.Add(new LineCurve(new Point3d(-0.8, 0.0, 0.0), new Point3d(0.8, 0.0, 0.0)));
                geometry.Add(new LineCurve(new Point3d(0.0, -0.8, 0.0), new Point3d(0.0, 0.8, 0.0)));
                break;
            case MarkerBlockTemplate.Slope:
                geometry.Add(new PolylineCurve(new[]
                {
                    new Point3d(-0.8, -0.2, 0.0),
                    new Point3d(0.4, -0.2, 0.0),
                    new Point3d(0.4, -0.6, 0.0),
                    new Point3d(0.9, 0.0, 0.0),
                    new Point3d(0.4, 0.6, 0.0),
                    new Point3d(0.4, 0.2, 0.0),
                    new Point3d(-0.8, 0.2, 0.0)
                }));
                geometry.Add(new LineCurve(new Point3d(-0.8, 0.0, 0.0), new Point3d(0.9, 0.0, 0.0)));
                break;
        }

        return geometry;
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
        }
        else
        {
            attributes.DeleteUserString(OutputOwnerKey);
            attributes.DeleteUserString(OutputBaseMaterialNameKey);
            attributes.DeleteUserString(OutputKindKey);
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

    private Guid AddBakedObject(RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        var attributes = CreateAttributes(doc, terrain, generated, trackOwnership: false);

        if (!string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
        {
            int definitionIndex = EnsureBlockDefinition(doc, generated.InstanceDefinitionName!, generated.MarkerBlockTemplate);
            if (definitionIndex >= 0)
                return doc.Objects.AddInstanceObject(definitionIndex, generated.InstanceTransform, attributes);
        }

        return generated.Geometry switch
        {
            Mesh mesh => doc.Objects.AddMesh(mesh, attributes),
            Brep brep => doc.Objects.AddBrep(brep, attributes),
            TextDot textDot => doc.Objects.AddTextDot(textDot, attributes),
            _ => Guid.Empty
        };
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

        if (_pendingRebuilds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var nextItem = _pendingRebuilds
            .Where(item => item.Value <= now)
            .OrderBy(item => item.Value)
            .Select(item => ((uint docSerial, Guid terrainId)?)item.Key)
            .FirstOrDefault();

        if (!nextItem.HasValue)
            return;

        var key = nextItem.Value;
        _pendingRebuilds.Remove(key);
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

        RebuildTerrain(doc, state, terrain, rebuildState.RequestedVersion);
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
        }

        if (changed)
            Save(doc, state);
    }

    private static bool MatchesObjectFilter(RhinoObject obj, ObjectType objectFilter)
    {
        return (obj.ObjectType & objectFilter) != 0;
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
            rebuildState.CancelRequested = true;
        return rebuildState.RequestedVersion;
    }

    private bool ShouldCancelRebuild(uint docSerial, Guid terrainId, long buildVersion)
    {
        if (!_rebuildStates.TryGetValue((docSerial, terrainId), out var state))
            return true;

        return state.CancelRequested || state.RequestedVersion > buildVersion;
    }

    private void RemoveRebuildState(uint docSerial, Guid terrainId)
    {
        _rebuildStates.Remove((docSerial, terrainId));
        _pendingRebuilds.Remove((docSerial, terrainId));
    }

    private void ClearRebuildStates(uint docSerial)
    {
        foreach (var key in _rebuildStates.Keys.Where(key => key.docSerial == docSerial).ToList())
            _rebuildStates.Remove(key);

        foreach (var key in _pendingRebuilds.Keys.Where(key => key.docSerial == docSerial).ToList())
            _pendingRebuilds.Remove(key);
    }

    private void UpdateDisplayState(RhinoDoc doc, TerrainDefinition terrain, TerrainRuntimeCache runtimeCache, TerrainBuildResult build)
    {
        var displayState = new TerrainDisplayState
        {
            TerrainMesh = TerrainRuntimeCacheCloner.CloneMesh(build.PrimaryMesh),
            BaseTerrainMesh = TerrainRuntimeCacheCloner.CloneMesh(build.BaseMesh),
            Summary = TerrainRuntimeCacheCloner.CloneAnalysis(build.Analysis)
        };
        displayState.ZoneObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.ZoneObjects));
        displayState.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.AuxiliaryObjects));
        displayState.MarkerObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.MarkerObjects));
        runtimeCache.DisplayState = displayState;
        UpdateRuntimePreview(doc, terrain, runtimeCache);
    }

    private void UpdateRuntimePreview(RhinoDoc doc, TerrainDefinition terrain, TerrainRuntimeCache runtimeCache)
    {
        if (runtimeCache.DisplayState == null)
            return;

        TerrainAnalysisPreviewBuilder.UpdatePreviewMesh(doc, terrain, runtimeCache.DisplayState);
        terrain.LastAnalysis = TerrainRuntimeCacheCloner.CloneAnalysis(runtimeCache.DisplayState.Summary);
    }

    private static string DescribeDisplayState(TerrainDisplayState? displayState)
    {
        if (displayState == null)
            return "no preview";

        int outputCount = (displayState.PreviewTerrainMesh != null ? 1 : 0) +
                          displayState.ZoneObjects.Count +
                          displayState.AuxiliaryObjects.Count +
                          displayState.MarkerObjects.Count;
        return $"{outputCount:N0} preview outputs";
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

    private void ApplyVisibilityAndLock(RhinoDoc doc, TerrainDefinition terrain)
    {
        if (terrain.IsVisible && !terrain.IsLocked && terrain.ShowTerrainMesh && terrain.ShowZoneMeshes && terrain.ShowSlopePreview)
            return;

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
            bool shouldHide = !terrain.IsVisible || (!terrain.ShowSlopePreview && IsSlopePreviewObject(obj));
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

    private static GeneratedObjectKind GetGeneratedObjectKind(ObjectAttributes? attributes)
    {
        if (attributes == null)
            return GeneratedObjectKind.Default;

        string? kind = attributes.GetUserString(OutputKindKey);
        return Enum.TryParse(kind, ignoreCase: true, out GeneratedObjectKind parsed)
            ? parsed
            : GeneratedObjectKind.Default;
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
