using System.Collections.Concurrent;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Per-terrain rebuild state: requesting, forcing and resetting rebuilds, cancelling the running worker,
// and retiring superseded workers without disposing meshes they may still read.
internal sealed partial class TerrainController
{
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

        /// <summary>
        /// When this terrain last dispatched a build. Drives the leading-edge debounce: an edit arriving
        /// after a quiet interval dispatches immediately, and only a gesture is rate-limited.
        /// </summary>
        public DateTime? LastDispatchUtc { get; set; }

        public Task<BackgroundBuildResult>? WorkerTask { get; set; }

        public CancellationTokenSource? WorkerCancellation { get; set; }

        public List<Task> RetiredWorkers { get; } = new();

        public ConcurrentQueue<QueuedBuildProgress> ProgressUpdates { get; } = new();
    }

    public void RebuildTerrain(RhinoDoc doc, Guid terrainId)
    {
        if (!ModelUnitGuard.TryGet(doc, out _))
            return;

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
            // An explicit Rebuild is a user asking for a fresh build now, so it still pre-empts.
            // A sampled edit does not: cancelling a build cheap enough to finish is what made a
            // gesture show nothing at all. See TerrainSupersededBuildPolicy.
            bool shouldCancel = isImmediate ||
                TerrainSupersededBuildPolicy.ShouldCancelRunningBuild(
                    GetRuntimeCache(docSerial, terrainId).LastFinalDuration);

            TerrainLatencyTrace.Record(
                docSerial,
                terrainId,
                rebuildState.RunningVersion,
                rebuildState.BuildGeneration,
                rebuildState.RunningMode,
                shouldCancel ? TerrainLatencyPhase.CancelRequested : TerrainLatencyPhase.SupersededAllowedToFinish,
                $"superseded by #{rebuildState.RequestedVersion:N0}");

            if (shouldCancel)
            {
                rebuildState.CancelRequested = true;
                rebuildState.WorkerCancellation?.Cancel();
            }
        }

        // A debounced request records its own edit in ScheduleRebuild, where the resolved delay is known.
        if (isImmediate)
        {
            TerrainLatencyTrace.Record(
                docSerial,
                terrainId,
                rebuildState.RequestedVersion,
                rebuildState.BuildGeneration,
                TerrainBuildMode.Final,
                TerrainLatencyPhase.Edit,
                "immediate");
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

        // Nothing will ever read a retired worker's result, so the stage meshes it made die with it,
        // whether it is still running or finished before anyone picked it up.
        _ = workerTask?.ContinueWith(
            static task => task.Result.WorkerCache.DiscardOwnedMeshOutputs(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

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
}
