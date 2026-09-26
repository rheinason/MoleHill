using System.Reflection;
using Eto.Forms;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime;

namespace MoleHill.Rhino.Services;

// The document-scoped terrain controller: its shared state (fields every partial reads), plug-in
// lifecycle, and the read API the panel and commands query. Each concern lives in its own partial,
// named for it: Terrains, Edits, Undo, Document, Selection, Build, RebuildState, Dependencies, Display,
// Output, Events, Contours, Diagnostics, Sculpt, Grasshopper and Interop. Add a new concern as a new
// partial rather than growing this file.
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
    // Latency trace only: remembers which pending requests have already had their "due" moment
    // recorded, so the debounce interval and the post-debounce dispatch wait stay separable.
    private readonly HashSet<(uint docSerial, Guid terrainId, TerrainBuildMode mode, long version)> _latencyDueMarked = new();
    private readonly HashSet<(uint docSerial, Guid terrainId, TerrainBuildMode mode, long version, string reason)> _latencyBlockedReasons = new();
    private bool _isPumpingFinishedBuilds;
    private bool _immediateDispatchPosted;
    /// <summary>Runs only while a build is in flight; see <see cref="EnsureBuildWakeTimer"/>.</summary>
    private UITimer? _buildWakeTimer;
    private readonly Dictionary<uint, PendingTerrainEdit> _pendingTerrainEdits = new();
    private readonly HashSet<(uint docSerial, uint undoSerial)> _terrainUndoRecords = new();
    private readonly HashSet<uint> _pendingSourceReferencePrunes = new();
    private readonly Dictionary<uint, Queue<Guid>> _pendingObjectReplacements = new();
    private readonly Dictionary<uint, HashSet<Guid>> _pendingBlockAttributeKeyRepairs = new();
    private readonly Dictionary<(uint docSerial, Guid terrainId), TerrainRuntimeCache> _runtimeCaches = new();
    private readonly Dictionary<(uint docSerial, Guid terrainId), TerrainRebuildState> _rebuildStates = new();
    private bool _initialized;
    private int _suppressDocEvents;

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
        RhinoDoc.UnitsChangedWithScaling += OnUnitsChangedWithScaling;
        RhinoDoc.DocumentPropertiesChanged += OnDocumentPropertiesChanged;
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
        RhinoDoc.UnitsChangedWithScaling -= OnUnitsChangedWithScaling;
        RhinoDoc.DocumentPropertiesChanged -= OnDocumentPropertiesChanged;
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
        _pendingTerrainEdits.Clear();
        _terrainUndoRecords.Clear();
        _pendingSourceReferencePrunes.Clear();
        _pendingBlockAttributeKeyRepairs.Clear();
        _runtimeCaches.Clear();
        _rebuildStates.Clear();
    }

    public IReadOnlyList<TerrainDefinition> GetTerrains(RhinoDoc doc) => GetState(doc).Terrains;

    public bool HasCompletedFinalTerrainMesh(RhinoDoc doc, Guid terrainId) =>
        _runtimeCaches.TryGetValue((doc.RuntimeSerialNumber, terrainId), out TerrainRuntimeCache? cache) &&
        cache.DisplayState is { IsPreview: false, TerrainMesh: not null };

    /// <summary>Another terrain's last completed final mesh, for cards that compare against a sibling
    /// terrain directly rather than requiring it to be baked to Rhino geometry first.</summary>
    internal Mesh? GetFinalTerrainMesh(RhinoDoc doc, Guid terrainId) =>
        _runtimeCaches.TryGetValue((doc.RuntimeSerialNumber, terrainId), out TerrainRuntimeCache? cache) &&
        cache.DisplayState is { IsPreview: false, TerrainMesh: { } mesh }
            ? mesh
            : null;

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

    public IReadOnlyList<ZoneAnalysisSummary> GetZoneAnalysisResults(RhinoDoc doc, Guid terrainId)
    {
        return GetRuntimeCache(doc.RuntimeSerialNumber, terrainId).DisplayState?.ZoneAnalysisResults
            ?? new List<ZoneAnalysisSummary>();
    }
}
