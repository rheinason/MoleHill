using System.Diagnostics;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.UI;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Build orchestration: scheduling, debounce, background build lifecycle, apply-result, long-build warnings.
internal sealed partial class TerrainController
{
    /// <summary>
    /// How often to give the message loop something to process while a build is in flight. 15 ms is
    /// roughly the resolution a Windows timer offers anyway, and it bounds the completion wake at about
    /// one frame instead of the measured 320 ms median.
    /// </summary>
    private const double BuildWakeIntervalSeconds = 0.015;

    /// <param name="statusOnly">The definition did not change, only that a rebuild is coming: tell listeners
    /// through <see cref="StatusChanged"/>, so the panel does not relay its cards out for it.</param>
    private void ScheduleRebuild(RhinoDoc doc, Guid terrainId, bool notify = true, bool statusOnly = false)
    {
        if (!ModelUnitGuard.TryGet(doc, out _, report: false))
            return;

        long requestedVersion = RequestRebuild(doc.RuntimeSerialNumber, terrainId, isImmediate: false);
        int debounceMs = ResolveFinalDebounceMs(doc.RuntimeSerialNumber, terrainId);
        // Mark the edit before queueing: a zero-delay request can be dispatched before this method
        // returns, and an edit stamped afterwards would sort after its own dispatch.
        TerrainLatencyTrace.Record(
            doc.RuntimeSerialNumber,
            terrainId,
            requestedVersion,
            GetRebuildState(doc.RuntimeSerialNumber, terrainId).BuildGeneration,
            TerrainBuildMode.Final,
            TerrainLatencyPhase.Edit,
            $"debounce {debounceMs} ms");
        QueuePendingBuild(doc.RuntimeSerialNumber, terrainId, TerrainBuildMode.Final, requestedVersion, debounceMs);
        if (debounceMs == 0)
            RequestImmediateDispatch();
        var terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain != null)
            terrain.LastBuildMessage = GetRebuildState(doc.RuntimeSerialNumber, terrainId).IsBuilding
                ? $"Queued rebuild #{requestedVersion:N0}; current build will stop at the next safe checkpoint."
                : $"Scheduled rebuild #{requestedVersion:N0}.";
        if (notify && statusOnly)
            RaiseStatusChanged();
        else if (notify)
            RaiseStateChanged();
    }

    /// <summary>
    /// Gives the message loop something to process while a build is in flight, and counts how often it
    /// gets to run - see <see cref="_buildWakeTicks"/>.
    ///
    /// **This did not fix the wake, and is currently kept as instrumentation.** The cost it was aimed
    /// at is real and dominant: measured in an interactive Rhino on 2026-09-19,
    /// <c>worker-end -&gt; wake-posted</c> is 0.0 ms while <c>wake-posted -&gt; wake-ran</c> is a median
    /// ~320 ms - 71% of edit-to-visible - against 8 ms of redraw and 74 ms of geometry, and a headless
    /// slot reports the same ~320 ms, so it is the host's scheduling rather than a measurement artifact.
    /// The reasoning was that posting a callback gives the host no reason to look at its queue once an
    /// edit settles and no input is arriving, and that a timer tick, being a real Windows message, would
    /// wake a loop that is otherwise waiting.
    ///
    /// Re-measured with the timer running, the wake was **unchanged** at a ~299 ms median. That is the
    /// second failed fix aimed at a starved queue (the first moved the post to Eto's invoke queue and
    /// measured three times worse), which is strong evidence the queue is not what is slow. The tick
    /// counter is there to say which of the two remaining explanations is right before anything else is
    /// tried.
    ///
    /// It runs only while a build is in flight or a debounced request is waiting, and stops itself as
    /// soon as neither is, so an idle Rhino is left alone. The second case is not instrumentation: the
    /// tick is what dispatches a request that falls due after the message queue has gone quiet.
    /// </summary>
    private void EnsureBuildWakeTimer()
    {
        if (_buildWakeTimer == null)
        {
            _buildWakeTimer = new UITimer { Interval = BuildWakeIntervalSeconds };
            _buildWakeTimer.Elapsed += (_, _) =>
            {
                if (_isWakeTicking)
                    return;

                _isWakeTicking = true;
                try
                {
                    Interlocked.Increment(ref _buildWakeTicks);
                    PumpFinishedBuilds();
                    // A debounced request that falls due while nothing else is happening has no other way
                    // to start: Rhino raises Idle once when its queue empties and not again until a new
                    // message arrives, so it waited for the next mouse move. Found in a headless slot,
                    // where nothing ever moves the mouse and a due Final build sat undispatched for 30 s+.
                    if (!HasRunningBuild())
                        TryDispatchPendingBuild();
                    if (!HasRunningBuild() && _pendingRebuilds.Count == 0)
                        _buildWakeTimer?.Stop();
                }
                finally
                {
                    _isWakeTicking = false;
                }
            };
        }

        if (!_buildWakeTimer.Started)
            _buildWakeTimer.Start();
    }

    /// <summary>
    /// Ticks of <see cref="_buildWakeTimer"/>, so the wake can report how many times the UI thread ran
    /// a queued timer message while a finished build was waiting for it.
    ///
    /// This distinguishes the only two explanations left for the ~300 ms wake, which no amount of
    /// reasoning about queues can separate: **many ticks** means the thread was free and running our
    /// code all along, so the completion is being deprioritized behind something rather than starved;
    /// **no ticks** means the thread was busy or the message was not delivered, and the wait is Rhino
    /// doing its own work after an edit. Two fixes aimed at the "starved queue" reading - Eto's invoke
    /// queue, then this timer - both failed to move the number, which is why the next step is a
    /// measurement rather than a third fix.
    /// </summary>
    private int _buildWakeTicks;

    /// <summary>Guards the wake tick: dispatch can open the slow-build dialog, whose modal loop keeps
    /// delivering timer messages.</summary>
    private bool _isWakeTicking;

    private bool HasRunningBuild()
    {
        foreach (TerrainRebuildState rebuildState in _rebuildStates.Values)
        {
            if (rebuildState.WorkerTask != null)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Asks the dispatcher to run as soon as the current Rhino event finishes, instead of waiting for
    /// the next Idle. Coalesced: many edits in one gesture post at most one pending dispatch.
    /// </summary>
    private void RequestImmediateDispatch()
    {
        if (_immediateDispatchPosted)
            return;

        _immediateDispatchPosted = true;
        // Eto's AsyncInvoke, not RhinoApp.InvokeOnUiThread: the latter runs *inline* when already on
        // the UI thread, which would start a build in the middle of the Rhino document event that
        // produced the edit - during OnBeforeTransformObjects, before the transform has been applied.
        // This has to queue behind the current event.
        Application.Instance.AsyncInvoke(() =>
        {
            _immediateDispatchPosted = false;
            TryDispatchPendingBuild();
        });
    }

    /// <summary>
    /// The delay before this edit is dispatched. Zero unless the terrain dispatched recently, so an
    /// isolated edit is immediate and only a gesture is rate-limited - see <see cref="TerrainDebouncePolicy"/>.
    /// </summary>
    private int ResolveFinalDebounceMs(uint docSerial, Guid terrainId)
    {
        DateTime? lastDispatch = GetRebuildState(docSerial, terrainId).LastDispatchUtc;
        return TerrainDebouncePolicy.ResolveDelayMs(
            GetRuntimeCache(docSerial, terrainId).LastFinalDuration,
            lastDispatch.HasValue ? DateTime.UtcNow - lastDispatch.Value : null);
    }

    private bool HasPendingFinalBuild(uint docSerial, Guid terrainId)
    {
        if (_pendingRebuilds.ContainsKey((docSerial, terrainId, TerrainBuildMode.Final)))
            return true;

        return _rebuildStates.TryGetValue((docSerial, terrainId), out TerrainRebuildState? rebuildState) &&
               (rebuildState.IsBuilding || rebuildState.RequestedVersion > rebuildState.AppliedVersion);
    }

    private void QueuePendingBuild(uint docSerial, Guid terrainId, TerrainBuildMode mode, long version, int delayMs)
    {
        _pendingRebuilds[(docSerial, terrainId, mode)] = new PendingBuildRequest(DateTime.UtcNow.AddMilliseconds(delayMs), version);
        // A delayed request needs something to wake the UI thread when it falls due; see the wake tick.
        if (delayMs > 0)
            EnsureBuildWakeTimer();
    }

    /// <summary>
    /// Saves the document's terrain JSON on a later idle. <paramref name="raiseStateChanged"/> asks for the
    /// panel refresh a direct save would raise; a queued request that wants one keeps it when a later request
    /// does not.
    /// </summary>
    private void QueuePendingDocumentSave(uint docSerial, int delayMs, bool raiseStateChanged = true)
    {
        bool raise = raiseStateChanged ||
                     (_pendingDocumentSaves.TryGetValue(docSerial, out PendingDocumentSave queued) && queued.RaiseStateChanged);
        _pendingDocumentSaves[docSerial] = new PendingDocumentSave(DateTime.UtcNow.AddMilliseconds(delayMs), raise);
    }

    private readonly record struct PendingDocumentSave(DateTime Due, bool RaiseStateChanged);

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
        Mesh? mesh = runtimeCache.DisplayState?.TerrainMesh;
        bool hasExpensiveModifier = terrain.Modifiers.Any(static modifier =>
            modifier.IsEnabled &&
            modifier is RemeshModifierDefinition or GradePadModifierDefinition or GradePathModifierDefinition or RetainingWallModifierDefinition or InSituStairModifierDefinition);
        return TerrainSlowBuildWarningPolicy.ShouldWarn(
            terrain.ShowSlowBuildWarning,
            mode,
            mode == TerrainBuildMode.Preview ? runtimeCache.LastPreviewDuration : runtimeCache.LastFinalDuration,
            mesh?.Faces.Count,
            mesh?.Vertices.Count ?? 0,
            hasExpensiveModifier,
            out warning);
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

        if (rebuildState.WorkerTask != null)
            RetireRunningWorker(rebuildState, invalidateGeneration: true);

        if (!ConfirmLongRunningBuild(doc, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), rebuildState, mode, buildVersion))
            return;

        while (rebuildState.ProgressUpdates.TryDequeue(out _))
        {
        }
        terrain.LastBuildMessage = $"{(mode == TerrainBuildMode.Preview ? "Preview" : "Build")} #{buildVersion:N0}: snapshot starting...";
        WriteBuildStarted(terrain, mode, buildVersion);
        RaiseStatusChanged();
        var latency = new TerrainLatencyScope(
            doc.RuntimeSerialNumber,
            terrain.TerrainId,
            buildVersion,
            rebuildState.BuildGeneration + 1,
            mode);
        latency.Mark(TerrainLatencyPhase.SnapshotStart, $"{terrain.Modifiers.Count:N0} modifiers");
        var snapshotTimer = Stopwatch.StartNew();
        TerrainBuildSnapshot snapshot = CreateBuildSnapshot(
            doc,
            terrain,
            includeSectionTerrains: mode == TerrainBuildMode.Final);
        snapshotTimer.Stop();
        latency.Mark(TerrainLatencyPhase.SnapshotEnd);

        var workerCacheTimer = Stopwatch.StartNew();
        TerrainRuntimeCache workerCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId).CreateWorkerCopy();
        workerCacheTimer.Stop();
        latency.Mark(TerrainLatencyPhase.CloneEnd);
        var cancellation = new CancellationTokenSource();
        long buildGeneration = ++rebuildState.BuildGeneration;

        rebuildState.RunningVersion = buildVersion;
        rebuildState.RunningMode = mode;
        rebuildState.CancelRequested = false;
        rebuildState.IsBuilding = true;
        rebuildState.WorkerCancellation = cancellation;
        Action<TerrainBuildProgress> reportProgress = progress =>
            rebuildState.ProgressUpdates.Enqueue(new QueuedBuildProgress(buildVersion, buildGeneration, mode, progress));
        // Show the finished mesh ahead of its dependent outputs when the last build proved those
        // outputs cost real time. The copy happens here on the worker, not on the UI thread.
        TerrainRuntimeCache uiCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId);
        uint docSerial = doc.RuntimeSerialNumber;
        Guid interimTerrainId = terrain.TerrainId;
        Action<Mesh, Mesh?>? publishInterimGeometry = null;
        if (TerrainInterimPublishPolicy.ShouldPublishGeometryEarly(
                mode,
                uiCache.PeakDependentOutputsDuration,
                uiCache.DisplayState != null))
        {
            publishInterimGeometry = (primary, baseMesh) =>
            {
                Mesh primaryCopy = primary.DuplicateMesh();
                Mesh? baseCopy = ReferenceEquals(baseMesh, primary) ? primaryCopy : baseMesh?.DuplicateMesh();
                RhinoApp.InvokeOnUiThread((Action)(() => PublishInterimGeometry(
                    docSerial,
                    interimTerrainId,
                    buildVersion,
                    buildGeneration,
                    primaryCopy,
                    baseCopy)));
            };
        }

        latency.Mark(TerrainLatencyPhase.WorkerQueued);
        rebuildState.WorkerTask = Task.Run(() => ExecuteBackgroundBuild(
            snapshot,
            workerCache,
            mode,
            buildVersion,
            buildGeneration,
            snapshotTimer.Elapsed,
            workerCacheTimer.Elapsed,
            cancellation.Token,
            reportProgress,
            latency,
            publishInterimGeometry));

        // Wake the UI thread when the worker finishes. Completion is otherwise discovered only by
        // TryCompleteFinishedBuild polling inside OnIdle, and Rhino does not raise Idle merely because
        // a background thread completed: measured 2026-09-19 on the trailer-ramp terrain, a build whose
        // real work was under 5 ms sat finished-but-unpublished for 451 ms across zero Idle ticks,
        // while 22 callbacks posted with InvokeOnUiThread ran in that same window. So the UI thread was
        // available throughout - nothing had asked it to look.
        _ = rebuildState.WorkerTask.ContinueWith(
            (_, state) =>
            {
                int ticksAtPost = Volatile.Read(ref _buildWakeTicks);
                long panelTicksAtPost = TerrainUiThreadProbe.PanelRefreshTicks;
                int panelCountAtPost = TerrainUiThreadProbe.PanelRefreshCount;
                long tabTicksAtPost = TerrainUiThreadProbe.TabLayoutTicks;
                latency.Mark(TerrainLatencyPhase.WakePosted);

                // InvokeOnUiThread here, deliberately, even though the dispatch path uses Eto's
                // AsyncInvoke. Swapping this one to AsyncInvoke was tried on 2026-09-19 because the
                // wake marshal is the dominant cost of an edit, and it measured **three times worse**:
                // median wake 320 -> 1,079 ms and edit-to-visible 454 -> 1,140 ms over a 60-sample
                // gesture. The two call sites want different things - dispatch needs a queue that will
                // not run it inline, this needs whichever queue the host drains soonest - and on this
                // evidence that is Rhino's. Do not "unify" them.
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    var controller = (TerrainController)state!;
                    int ticksWhileWaiting = Volatile.Read(ref controller._buildWakeTicks) - ticksAtPost;
                    double panelMs = TerrainUiThreadProbe.TicksToMilliseconds(
                        TerrainUiThreadProbe.PanelRefreshTicks - panelTicksAtPost);
                    int panelRefreshes = TerrainUiThreadProbe.PanelRefreshCount - panelCountAtPost;
                    latency.Mark(
                        TerrainLatencyPhase.WakeRan,
                        $"{ticksWhileWaiting} wake ticks, timer {(controller._buildWakeTimer?.Started == true ? "running" : "stopped")}; " +
                        $"panel refresh {panelMs:N0} ms over {panelRefreshes} runs " +
                        $"(tab layout {TerrainUiThreadProbe.TicksToMilliseconds(TerrainUiThreadProbe.TabLayoutTicks - tabTicksAtPost):N0} ms)");
                    controller.PumpFinishedBuilds();
                }));
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // The post above is free but does not make the host look at its queue; this does.
        EnsureBuildWakeTimer();

        terrain.LastBuildMessage = mode == TerrainBuildMode.Preview
            ? $"Previewing terrain #{buildVersion:N0}..."
            : $"Building terrain #{buildVersion:N0}...";
        RaiseStatusChanged();
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
        WriteBuildStarted(terrain, mode, buildVersion);
        RaiseStateChanged();

        TerrainRuntimeCache? workerCache = null;
        try
        {
            var latency = new TerrainLatencyScope(
                doc.RuntimeSerialNumber,
                terrain.TerrainId,
                buildVersion,
                rebuildState.BuildGeneration,
                mode);
            latency.Mark(TerrainLatencyPhase.Dispatch, "synchronous");
            latency.Mark(TerrainLatencyPhase.SnapshotStart, $"{terrain.Modifiers.Count:N0} modifiers");
            var snapshotTimer = Stopwatch.StartNew();
            TerrainBuildSnapshot snapshot = CreateBuildSnapshot(
                doc,
                terrain,
                includeSectionTerrains: mode == TerrainBuildMode.Final);
            snapshotTimer.Stop();
            latency.Mark(TerrainLatencyPhase.SnapshotEnd);

            var workerCacheTimer = Stopwatch.StartNew();
            workerCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId).CreateWorkerCopy();
            workerCacheTimer.Stop();
            latency.Mark(TerrainLatencyPhase.CloneEnd);
            latency.Mark(TerrainLatencyPhase.WorkerQueued);

            BackgroundBuildResult result = ExecuteBackgroundBuild(
                snapshot,
                workerCache,
                mode,
                buildVersion,
                rebuildState.BuildGeneration,
                snapshotTimer.Elapsed,
                workerCacheTimer.Elapsed,
                CancellationToken.None,
                reportProgress: null,
                latency);
            latency.Mark(TerrainLatencyPhase.CompletionDispatch, "synchronous");
            if (result.WasCanceled || rebuildState.RequestedVersion > buildVersion)
            {
                terrain.LastBuildMessage = rebuildState.RequestedVersion > buildVersion
                    ? $"{mode} #{buildVersion:N0} cancelled; newer request queued."
                    : $"{mode} #{buildVersion:N0} cancelled.";
                terrain.LastStructuredDiagnostics.Clear();
                WriteBuildCancelled(terrain, mode, buildVersion);
                RaiseStateChanged();
                return false;
            }

            if (result.Error != null || result.Build == null)
            {
                if (mode == TerrainBuildMode.Final)
                    terrain.LastBuildUtc = DateTimeOffset.UtcNow;

                terrain.LastBuildMessage = FormatBuildFailureStatus(mode, buildVersion, result.Error);
                terrain.LastStructuredDiagnostics.Clear();
                WriteBuildFailed(terrain, mode, buildVersion);
                if (mode == TerrainBuildMode.Final)
                    Save(doc, state);
                return false;
            }

            ApplySuccessfulBuild(doc, state, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), rebuildState, result);
            return true;
        }
        finally
        {
            // A no-op after a merge; otherwise disposes the stage meshes this build made.
            workerCache?.DiscardOwnedMeshOutputs();
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
        long buildGeneration,
        TimeSpan snapshotElapsed,
        TimeSpan workerCacheCloneElapsed,
        CancellationToken cancellationToken,
        Action<TerrainBuildProgress>? reportProgress = null,
        TerrainLatencyScope? latency = null,
        Action<Mesh, Mesh?>? publishInterimGeometry = null)
    {
        var timer = Stopwatch.StartNew();
        latency?.Mark(TerrainLatencyPhase.WorkerStart);
        try
        {
            TerrainBuildResult build = _buildService.Build(
                snapshot,
                workerCache,
                mode,
                () => cancellationToken.IsCancellationRequested,
                reportProgress,
                latency,
                publishInterimGeometry);
            timer.Stop();
            latency?.Mark(TerrainLatencyPhase.WorkerEnd, "ok");
            return new BackgroundBuildResult(
                buildVersion,
                buildGeneration,
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
            latency?.Mark(TerrainLatencyPhase.WorkerEnd, "cancelled");
            return new BackgroundBuildResult(
                buildVersion,
                buildGeneration,
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
            latency?.Mark(TerrainLatencyPhase.WorkerEnd, $"failed: {ex.GetType().Name}");
            return new BackgroundBuildResult(
                buildVersion,
                buildGeneration,
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
        finally
        {
            snapshot.DisposeSectionTerrainMeshes();
        }
    }

    private void CompleteBackgroundBuild(
        RhinoDoc doc,
        DocumentState state,
        TerrainDefinition terrain,
        TerrainRebuildState rebuildState,
        BackgroundBuildResult result)
    {
        try
        {
            CompleteBackgroundBuildCore(doc, state, terrain, rebuildState, result);
        }
        finally
        {
            // Every exit but a merge leaves stage meshes this build made that nothing will read. After a
            // merge the worker cache is empty, so this is a no-op there.
            result.WorkerCache.DiscardOwnedMeshOutputs();
        }
    }

    private void CompleteBackgroundBuildCore(
        RhinoDoc doc,
        DocumentState state,
        TerrainDefinition terrain,
        TerrainRebuildState rebuildState,
        BackgroundBuildResult result)
    {
        rebuildState.IsBuilding = false;
        rebuildState.RunningVersion = 0;
        rebuildState.CancelRequested = false;
        TerrainLatencyScope latency = LatencyScopeFor(doc, terrain, result);

        if (result.Generation != rebuildState.BuildGeneration)
        {
            latency.Mark(TerrainLatencyPhase.Closed, "discarded (stale generation)");
            terrain.LastBuildMessage = $"{result.Mode} #{result.Version:N0} discarded after reset.";
            terrain.LastStructuredDiagnostics.Clear();
            WriteBuildCancelled(terrain, result.Mode, result.Version);
            RaiseStateChanged();
            return;
        }

        if (rebuildState.RequestedVersion > result.Version)
        {
            // A build that a newer edit overtook but that still ran to completion is the frame a
            // gesture is made of, so show it rather than discarding the work. Only a result that was
            // cancelled, failed, or is too old to be useful falls through to the old behaviour.
            if (!result.WasCanceled &&
                result.Error == null &&
                result.Build != null &&
                PublishSupersededGeometry(
                    doc, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), result))
            {
                latency.Mark(TerrainLatencyPhase.Closed, "superseded, published as preview");
                return;
            }

            latency.Mark(TerrainLatencyPhase.Closed, "superseded");
            terrain.LastBuildMessage = $"{result.Mode} #{result.Version:N0} cancelled; newer request queued.";
            terrain.LastStructuredDiagnostics.Clear();
            WriteBuildCancelled(terrain, result.Mode, result.Version);
            RaiseStateChanged();
            return;
        }

        if (result.WasCanceled)
        {
            if (result.Mode == TerrainBuildMode.Final)
                terrain.LastBuildUtc = DateTimeOffset.UtcNow;

            latency.Mark(TerrainLatencyPhase.Closed, "cancelled");
            terrain.LastBuildMessage = $"{result.Mode} #{result.Version:N0} cancelled.";
            terrain.LastStructuredDiagnostics.Clear();
            WriteBuildCancelled(terrain, result.Mode, result.Version);
            RaiseStateChanged();
            return;
        }

        if (result.Error != null || result.Build == null)
        {
            if (result.Mode == TerrainBuildMode.Final)
                terrain.LastBuildUtc = DateTimeOffset.UtcNow;

            latency.Mark(TerrainLatencyPhase.Closed, "failed");
            terrain.LastBuildMessage = FormatBuildFailureStatus(result.Mode, result.Version, result.Error);
            terrain.LastStructuredDiagnostics.Clear();
            WriteBuildFailed(terrain, result.Mode, result.Version);
            if (result.Mode == TerrainBuildMode.Final)
                Save(doc, state);
            return;
        }

        ApplySuccessfulBuild(doc, state, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), rebuildState, result);
    }

    private static TerrainLatencyScope LatencyScopeFor(RhinoDoc doc, TerrainDefinition terrain, BackgroundBuildResult result) =>
        new(doc.RuntimeSerialNumber, terrain.TerrainId, result.Version, result.Generation, result.Mode);

    private void ApplySuccessfulBuild(
        RhinoDoc doc,
        DocumentState state,
        TerrainDefinition terrain,
        TerrainRuntimeCache runtimeCache,
        TerrainRebuildState rebuildState,
        BackgroundBuildResult result)
    {
        TerrainBuildResult build = result.Build!;
        TerrainLatencyScope latency = LatencyScopeFor(doc, terrain, result);
        // The first final mesh enables cards elsewhere that compare against this terrain.
        bool firstFinal = result.Mode == TerrainBuildMode.Final && rebuildState.AppliedVersion == 0;
        build.RecordTiming("Snapshot build", result.SnapshotElapsed, $"{result.SnapshotTerrain.Modifiers.Count:N0} modifiers", MinorTimingDiagnosticThresholdMs);
        build.RecordTiming("Worker cache clone", result.WorkerCacheCloneElapsed, null, MinorTimingDiagnosticThresholdMs);

        var cacheMergeTimer = Stopwatch.StartNew();
        List<Mesh> displacedMeshes = runtimeCache.ReplaceBuildCachesFrom(result.WorkerCache);
        DisposeDisplacedCacheMeshesWhenSafe(displacedMeshes, rebuildState);
        cacheMergeTimer.Stop();
        latency.Mark(TerrainLatencyPhase.MergeEnd);
        build.RecordTiming("Worker cache merge", cacheMergeTimer.Elapsed, null, MinorTimingDiagnosticThresholdMs);

        SyncComputedModifierState(terrain, result.SnapshotTerrain);

        TimeSpan buildElapsed = result.BuildElapsed;
        if (result.Mode == TerrainBuildMode.Final)
        {
            terrain.LastBuildUtc = DateTimeOffset.UtcNow;
            terrain.LastAnalysisResults = TerrainRuntimeCacheCloner.CloneAnalyses(build.AnalysisResults);
            ClearLegacyOutputState(doc, terrain);
            // Remove managed live outputs left behind by older preview/output paths or a failed
            // replacement. Current preview geometry is conduit-only and must never accumulate in doc.
            PurgeOrphanedOwnedObjects(doc, terrain);
            runtimeCache.LastFinalDuration = buildElapsed;
            runtimeCache.ObserveDependentOutputsDuration(build.DependentOutputsElapsed);
            rebuildState.AppliedVersion = result.Version;
        }
        else
        {
            runtimeCache.LastPreviewDuration = buildElapsed;
            rebuildState.AppliedPreviewVersion = result.Version;
        }

        var displayTimer = Stopwatch.StartNew();
        UpdateDisplayState(doc, terrain, runtimeCache, build, result.Version);
        ReassertSculptPreviewMesh(doc, terrain.TerrainId);
        bool sectionReferenceMeshChanged = false;
        if (result.Mode == TerrainBuildMode.Final)
        {
            ulong finalMeshFingerprint = TerrainBuildService.ComputeMeshFingerprintForDiagnostics(build.PrimaryMesh);
            sectionReferenceMeshChanged = finalMeshFingerprint != runtimeCache.LastFinalMeshFingerprint;
            runtimeCache.LastFinalMeshFingerprint = finalMeshFingerprint;
        }
        displayTimer.Stop();
        latency.Mark(TerrainLatencyPhase.DisplayEnd);
        TerrainDisplayState displayState = runtimeCache.DisplayState
            ?? throw new InvalidOperationException("Terrain display state was not produced by the build.");
        build.RecordTiming("Display refresh", displayTimer.Elapsed, DescribeDisplayState(displayState), StageTimingDiagnosticThresholdMs);
        TimeSpan commandElapsed = result.SnapshotElapsed + result.WorkerCacheCloneElapsed + buildElapsed +
                                  cacheMergeTimer.Elapsed + displayTimer.Elapsed;

        if (result.Mode == TerrainBuildMode.Final)
        {
            SyncTerrainObjects(doc, terrain, build);
            latency.Mark(TerrainLatencyPhase.SyncEnd);
            terrain.LastStructuredDiagnostics = build.StructuredDiagnostics.ToList();
            terrain.LastBuildMessage = build.Diagnostics.Count == 0
                ? "Build succeeded."
                : string.Join(System.Environment.NewLine, build.Diagnostics.Take(8));

            // The document's string table only mirrors the in-memory state (WriteDocument serializes that
            // state when the file is saved, and undo snapshots it), so the write waits for the next idle,
            // after the redraw. Writing it here cost 1 s per edit in a live 44k-object document, where
            // something else reacts to document-string changes; the same write is free in a clean Rhino.
            var saveTimer = Stopwatch.StartNew();
            QueuePendingDocumentSave(doc.RuntimeSerialNumber, delayMs: 0, raiseStateChanged: false);
            saveTimer.Stop();
            latency.Mark(TerrainLatencyPhase.SaveEnd);
            build.RecordTiming("Document save", saveTimer.Elapsed, $"{state.Terrains.Count:N0} terrains; queued for idle", MinorTimingDiagnosticThresholdMs);

            var redrawTimer = Stopwatch.StartNew();
            doc.Views.Redraw();
            redrawTimer.Stop();
            latency.Mark(TerrainLatencyPhase.RedrawEnd);
            build.RecordTiming("Viewport redraw", redrawTimer.Elapsed, null, MinorTimingDiagnosticThresholdMs);

            var totalTimingDetail = $"snapshot {FormatElapsed(result.SnapshotElapsed)}, clone {FormatElapsed(result.WorkerCacheCloneElapsed)}, build {FormatElapsed(buildElapsed)}, merge {FormatElapsed(cacheMergeTimer.Elapsed)}, display {FormatElapsed(displayTimer.Elapsed)}, save {FormatElapsed(saveTimer.Elapsed)}, redraw {FormatElapsed(redrawTimer.Elapsed)}; {DescribeDisplayState(displayState)}";
            build.RecordTiming(
                "Rebuild total",
                result.SnapshotElapsed + result.WorkerCacheCloneElapsed + buildElapsed + cacheMergeTimer.Elapsed + displayTimer.Elapsed + saveTimer.Elapsed + redrawTimer.Elapsed,
                totalTimingDetail,
                TotalTimingDiagnosticThresholdMs);
            terrain.LastBuildMessage = FormatBuildMessage(
                build.Diagnostics.Count == 0 ? new[] { "Build succeeded." } : build.Diagnostics,
                result.SnapshotElapsed,
                result.WorkerCacheCloneElapsed,
                buildElapsed,
                cacheMergeTimer.Elapsed,
                displayTimer.Elapsed,
                saveTimer.Elapsed,
                redrawTimer.Elapsed,
                displayState,
                build.Timings,
                "Rebuild total");
            commandElapsed += saveTimer.Elapsed + redrawTimer.Elapsed;
            if (sectionReferenceMeshChanged)
                ScheduleTerrainDependents(doc, state, terrain.TerrainId);
        }
        else
        {
            terrain.LastStructuredDiagnostics = build.StructuredDiagnostics.ToList();
            build.RecordTiming(
                "Preview total",
                result.SnapshotElapsed + result.WorkerCacheCloneElapsed + buildElapsed + cacheMergeTimer.Elapsed + displayTimer.Elapsed,
                $"snapshot {FormatElapsed(result.SnapshotElapsed)}, clone {FormatElapsed(result.WorkerCacheCloneElapsed)}, build {FormatElapsed(buildElapsed)}, merge {FormatElapsed(cacheMergeTimer.Elapsed)}, display {FormatElapsed(displayTimer.Elapsed)}; {DescribeDisplayState(displayState)}",
                TotalTimingDiagnosticThresholdMs);

            terrain.LastBuildMessage = FormatBuildMessage(
                build.Diagnostics.Count == 0
                    ? new[] { "Preview updated; final rebuild queued." }
                    : new[] { "Preview updated." }.Concat(build.Diagnostics),
                result.SnapshotElapsed,
                result.WorkerCacheCloneElapsed,
                buildElapsed,
                cacheMergeTimer.Elapsed,
                displayTimer.Elapsed,
                null,
                null,
                displayState,
                build.Timings,
                "Preview total");

            var redrawTimer = Stopwatch.StartNew();
            doc.Views.Redraw();
            redrawTimer.Stop();
            latency.Mark(TerrainLatencyPhase.RedrawEnd);
            build.RecordTiming("Viewport redraw", redrawTimer.Elapsed, null, MinorTimingDiagnosticThresholdMs);
            commandElapsed += redrawTimer.Elapsed;
        }

        latency.Mark(TerrainLatencyPhase.Closed, "applied");
        WriteBuildFinished(terrain, result.Mode, result.Version, commandElapsed);
        string? cardResults = TryComputeCardResultSignature(doc, terrain);
        bool cardsUnchanged = !firstFinal && cardResults != null && cardResults == runtimeCache.LastCardResultSignature;
        runtimeCache.LastCardResultSignature = cardResults;
        if (cardsUnchanged)
            RaiseStatusChanged();
        else
            RaiseStateChanged();
    }

    /// <summary>
    /// What the cards show of this terrain's build results (see <see cref="TerrainCardResultSignature"/>);
    /// null when it cannot be computed, which callers treat as changed.
    /// </summary>
    private string? TryComputeCardResultSignature(RhinoDoc doc, TerrainDefinition terrain)
    {
        try
        {
            TerrainDisplayState? displayState = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId).DisplayState;
            var diagnostics = displayState?.RuntimeOverlays
                .Where(item => item.Channel == RuntimeOverlayChannel.Diagnostic)
                .Select(item => (item.Owner, item.Severity))
                ?? Enumerable.Empty<(RuntimeOverlayOwner, RuntimeOverlaySeverity)>();
            var warnings = terrain.Modifiers
                .Where(modifier => modifier is SmoothModifierDefinition or SculptModifierDefinition)
                .Select(modifier => (modifier.Id, GetModifierMeshQualityWarning(doc, terrain.TerrainId, modifier.Id)));
            return TerrainCardResultSignature.Compute(
                terrain,
                GetZoneAnalysisResults(doc, terrain.TerrainId),
                diagnostics,
                warnings);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatBuildMessage(
        IEnumerable<string> diagnosticLines,
        TimeSpan snapshotElapsed,
        TimeSpan cloneElapsed,
        TimeSpan buildElapsed,
        TimeSpan mergeElapsed,
        TimeSpan displayElapsed,
        TimeSpan? saveElapsed,
        TimeSpan? redrawElapsed,
        TerrainDisplayState displayState,
        IEnumerable<TerrainBuildTiming> timings,
        string totalStageName)
    {
        var lines = new List<string>();
        lines.AddRange(diagnosticLines.Where(static line => !string.IsNullOrWhiteSpace(line)));

        lines.Add("summary:");
        lines.Add($"snapshot: {FormatElapsed(snapshotElapsed)}");
        lines.Add($"clone: {FormatElapsed(cloneElapsed)}");
        lines.Add($"build: {FormatElapsed(buildElapsed)}");
        lines.Add($"merge: {FormatElapsed(mergeElapsed)}");
        lines.Add($"display: {FormatElapsed(displayElapsed)}");
        if (saveElapsed.HasValue)
            lines.Add($"save: {FormatElapsed(saveElapsed.Value)}");
        if (redrawElapsed.HasValue)
            lines.Add($"redraw: {FormatElapsed(redrawElapsed.Value)}");
        lines.Add($"outputs: {DescribeDisplayState(displayState)}");

        lines.Add("stages:");
        foreach (TerrainBuildTiming timing in timings.Where(t => t.Stage != totalStageName))
        {
            string detail = timing.Detail ?? string.Empty;
            if (timing.IsCacheHit && !detail.Contains("cache hit", StringComparison.OrdinalIgnoreCase))
                detail = string.IsNullOrWhiteSpace(detail) ? "cache hit" : $"{detail}; cache hit";

            lines.Add(string.IsNullOrWhiteSpace(detail)
                ? $"  {timing.Stage}: {FormatElapsed(timing.Elapsed)}"
                : $"  {timing.Stage}: {FormatElapsed(timing.Elapsed)} ({detail})");
        }

        return string.Join(System.Environment.NewLine, lines);
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

    private static string FormatBuildFailureStatus(TerrainBuildMode mode, long version, Exception? error)
    {
        string header = $"{BuildCommandLabel(mode)} #{version:N0} failed.";
        return error == null
            ? $"{header}{System.Environment.NewLine}Unknown build error."
            : $"{header}{System.Environment.NewLine}{error}";
    }

    private static void WriteBuildStarted(TerrainDefinition terrain, TerrainBuildMode mode, long version) =>
        RhinoApp.WriteLine($"[MoleHill] {terrain.Name}: {BuildCommandLabel(mode)} #{version:N0} started.");

    private static void WriteBuildFinished(
        TerrainDefinition terrain,
        TerrainBuildMode mode,
        long version,
        TimeSpan elapsed) =>
        RhinoApp.WriteLine(
            $"[MoleHill] {terrain.Name}: {BuildCommandLabel(mode)} #{version:N0} finished in {elapsed.TotalSeconds:0.##} s.");

    private static void WriteBuildCancelled(TerrainDefinition terrain, TerrainBuildMode mode, long version) =>
        RhinoApp.WriteLine($"[MoleHill] {terrain.Name}: {BuildCommandLabel(mode)} #{version:N0} cancelled.");

    private static void WriteBuildFailed(TerrainDefinition terrain, TerrainBuildMode mode, long version) =>
        RhinoApp.WriteLine(
            $"[MoleHill] {terrain.Name}: {BuildCommandLabel(mode)} #{version:N0} failed. See MoleHill Status for details.");

    private static string BuildCommandLabel(TerrainBuildMode mode) =>
        mode == TerrainBuildMode.Preview ? "Preview" : "Build";

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

    private sealed record QueuedBuildProgress(
        long Version,
        long Generation,
        TerrainBuildMode Mode,
        TerrainBuildProgress Progress);

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return $"{elapsed.TotalSeconds:0.##} s";
    }
}
