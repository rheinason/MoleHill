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

// Build orchestration: scheduling, debounce, background build lifecycle, apply-result, long-build warnings.
internal sealed partial class TerrainController
{
    private void ScheduleRebuild(RhinoDoc doc, Guid terrainId, bool notify = true)
    {
        if (!ModelUnitGuard.TryGet(doc, out _, report: false))
            return;

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

        if (rebuildState.WorkerTask != null)
            RetireRunningWorker(rebuildState, invalidateGeneration: true);

        if (!ConfirmLongRunningBuild(doc, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId), rebuildState, mode, buildVersion))
            return;

        while (rebuildState.ProgressUpdates.TryDequeue(out _))
        {
        }
        terrain.LastBuildMessage = $"{(mode == TerrainBuildMode.Preview ? "Preview" : "Build")} #{buildVersion:N0}: snapshot starting...";
        RhinoApp.WriteLine($"[MoleHill] {terrain.Name}: {terrain.LastBuildMessage}");
        RaiseStateChanged();
        var snapshotTimer = Stopwatch.StartNew();
        TerrainBuildSnapshot snapshot = TerrainBuildSnapshotBuilder.Create(doc, terrain);
        snapshotTimer.Stop();
        RhinoApp.WriteLine($"[MoleHill] {terrain.Name}: snapshot complete in {snapshotTimer.Elapsed.TotalSeconds:0.###} s; managed {GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.0} MB.");

        var workerCacheTimer = Stopwatch.StartNew();
        TerrainRuntimeCache workerCache = GetRuntimeCache(doc.RuntimeSerialNumber, terrain.TerrainId).CreateWorkerCopy();
        workerCacheTimer.Stop();
        var cancellation = new CancellationTokenSource();
        long buildGeneration = ++rebuildState.BuildGeneration;

        rebuildState.RunningVersion = buildVersion;
        rebuildState.RunningMode = mode;
        rebuildState.CancelRequested = false;
        rebuildState.IsBuilding = true;
        rebuildState.WorkerCancellation = cancellation;
        Action<TerrainBuildProgress> reportProgress = progress =>
            rebuildState.ProgressUpdates.Enqueue(new QueuedBuildProgress(buildVersion, buildGeneration, mode, progress));
        rebuildState.WorkerTask = Task.Run(() => ExecuteBackgroundBuild(
            snapshot,
            workerCache,
            mode,
            buildVersion,
            buildGeneration,
            snapshotTimer.Elapsed,
            workerCacheTimer.Elapsed,
            cancellation.Token,
            reportProgress));

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
                rebuildState.BuildGeneration,
                snapshotTimer.Elapsed,
                workerCacheTimer.Elapsed,
                CancellationToken.None);
            if (result.WasCanceled || rebuildState.RequestedVersion > buildVersion)
            {
                terrain.LastBuildMessage = rebuildState.RequestedVersion > buildVersion
                    ? $"{mode} #{buildVersion:N0} cancelled; newer request queued."
                    : $"{mode} #{buildVersion:N0} cancelled.";
                terrain.LastStructuredDiagnostics.Clear();
                RaiseStateChanged();
                return false;
            }

            if (result.Error != null || result.Build == null)
            {
                if (mode == TerrainBuildMode.Final)
                    terrain.LastBuildUtc = DateTimeOffset.UtcNow;

                terrain.LastBuildMessage = $"{mode} failed: {result.Error?.Message ?? "Unknown build error."}";
                terrain.LastStructuredDiagnostics.Clear();
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
        long buildGeneration,
        TimeSpan snapshotElapsed,
        TimeSpan workerCacheCloneElapsed,
        CancellationToken cancellationToken,
        Action<TerrainBuildProgress>? reportProgress = null)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            TerrainBuildResult build = _buildService.Build(
                snapshot,
                workerCache,
                mode,
                () => cancellationToken.IsCancellationRequested,
                reportProgress);
            timer.Stop();
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

        if (result.Generation != rebuildState.BuildGeneration)
        {
            terrain.LastBuildMessage = $"{result.Mode} #{result.Version:N0} discarded after reset.";
            terrain.LastStructuredDiagnostics.Clear();
            RaiseStateChanged();
            return;
        }

        if (rebuildState.RequestedVersion > result.Version)
        {
            terrain.LastBuildMessage = $"{result.Mode} #{result.Version:N0} cancelled; newer request queued.";
            terrain.LastStructuredDiagnostics.Clear();
            RaiseStateChanged();
            return;
        }

        if (result.WasCanceled)
        {
            if (result.Mode == TerrainBuildMode.Final)
                terrain.LastBuildUtc = DateTimeOffset.UtcNow;

            terrain.LastBuildMessage = $"{result.Mode} #{result.Version:N0} cancelled.";
            terrain.LastStructuredDiagnostics.Clear();
            RaiseStateChanged();
            return;
        }

        if (result.Error != null || result.Build == null)
        {
            if (result.Mode == TerrainBuildMode.Final)
                terrain.LastBuildUtc = DateTimeOffset.UtcNow;

            terrain.LastBuildMessage = $"{result.Mode} failed: {result.Error?.Message ?? "Unknown build error."}";
            terrain.LastStructuredDiagnostics.Clear();
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
        List<Mesh> displacedMeshes = runtimeCache.ReplaceBuildCachesFrom(result.WorkerCache);
        DisposeDisplacedCacheMeshesWhenSafe(displacedMeshes, rebuildState);
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
        ReassertSculptPreviewMesh(doc.RuntimeSerialNumber, terrain.TerrainId);
        displayTimer.Stop();
        TerrainDisplayState displayState = runtimeCache.DisplayState
            ?? throw new InvalidOperationException("Terrain display state was not produced by the build.");
        build.RecordTiming("Display refresh", displayTimer.Elapsed, DescribeDisplayState(displayState), StageTimingDiagnosticThresholdMs);

        if (result.Mode == TerrainBuildMode.Final)
        {
            SyncTerrainObjects(doc, terrain, build);
            terrain.LastStructuredDiagnostics = build.StructuredDiagnostics.ToList();
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
            build.RecordTiming("Viewport redraw", redrawTimer.Elapsed, null, MinorTimingDiagnosticThresholdMs);
        }

        RaiseStateChanged();
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

}
