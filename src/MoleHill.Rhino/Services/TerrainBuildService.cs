using System.Diagnostics;
using System.Text.Json;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using TriangleNet.Meshing;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    private const int StageTimingDiagnosticThresholdMs = 250;
    private const double MinRepresentablePadPlaneNormalZ = 1e-3;
    private const int TriangulateCacheVersion = 3;
    private const int InSituStairTreadDepthWarningColorArgb = unchecked((int)0xFFFF0000);

    public TerrainBuildResult Build(
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainRuntimeCache runtimeCache,
        TerrainBuildMode mode = TerrainBuildMode.Final,
        Func<bool>? shouldCancel = null)
    {
        TerrainBuildSnapshot snapshot = TerrainBuildSnapshotBuilder.Create(doc, terrain);
        return Build(snapshot, runtimeCache, mode, shouldCancel);
    }

    public TerrainBuildResult Build(
        TerrainBuildSnapshot snapshot,
        TerrainRuntimeCache runtimeCache,
        TerrainBuildMode mode = TerrainBuildMode.Final,
        Func<bool>? shouldCancel = null)
    {
        var totalTimer = Stopwatch.StartNew();
        TerrainDefinition terrain = snapshot.Terrain;
        var build = new TerrainBuildResult
        {
            Mode = mode,
            HasDeferredOutputs = mode == TerrainBuildMode.Preview
        };
        var usedStageKeys = new HashSet<string>(StringComparer.Ordinal);
        RhinoMesh? currentMesh = null;
        RhinoMesh? baseMesh = null;
        ulong currentMeshFingerprint = 0;
        ulong baseMeshFingerprint = 0;
        AddToleranceDiagnostics(build, GetToleranceProfile(snapshot, terrain));

        foreach (var indexedModifier in terrain.Modifiers.Select((modifier, index) => (modifier, index)).Where(item => item.modifier.IsEnabled))
        {
            ThrowIfCancellationRequested(shouldCancel);
            ModifierDefinition modifier = indexedModifier.modifier;
            string stageKey = TerrainStageKey.ForMode(mode, TerrainStageKey.CreateModifier(indexedModifier.index, modifier));
            usedStageKeys.Add(stageKey);

            switch (modifier)
            {
                case TriangulateModifierDefinition triangulate:
                    currentMesh = BuildTinMesh(snapshot, terrain, triangulate, build, runtimeCache, stageKey, out currentMeshFingerprint, shouldCancel);
                    if (currentMesh != null && baseMesh == null)
                    {
                        baseMesh = currentMesh.DuplicateMesh();
                        baseMeshFingerprint = ComputeMeshFingerprint(baseMesh);
                    }
                    break;
                case AddGeometryModifierDefinition addGeometry:
                    currentMesh = ExecuteCachedMeshStage(
                        build,
                        runtimeCache,
                        stageKey,
                        "Add Geometry",
                        ComputeModifierStageFingerprint(snapshot, terrain, addGeometry, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, addGeometry.Label) : ApplyAddGeometry(snapshot, terrain, currentMesh, addGeometry, build, runtimeCache, shouldCancel),
                        result => DescribeModifierMeshResult(addGeometry.Label, result),
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case RemeshModifierDefinition remesh:
                    currentMesh = ExecuteCachedMeshStage(
                        build,
                        runtimeCache,
                        stageKey,
                        "Remesh",
                        ComputeModifierStageFingerprint(snapshot, terrain, remesh, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, remesh.Label) : ApplyRemesh(snapshot, terrain, currentMesh, remesh, build, mode),
                        result => DescribeModifierMeshResult(remesh.Label, result),
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case SmoothModifierDefinition smooth:
                    usedStageKeys.Add(TerrainStageKey.CreateSmoothPrepared(stageKey));
                    currentMesh = ExecuteCachedMeshStage(
                        build,
                        runtimeCache,
                        stageKey,
                        "Smooth",
                        ComputeModifierStageFingerprint(snapshot, terrain, smooth, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, smooth.Label) : ApplySmooth(snapshot, terrain, currentMesh, smooth, build, runtimeCache, stageKey, mode),
                        result => DescribeModifierMeshResult(smooth.Label, result),
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case RetainingWallModifierDefinition retainingWall:
                    currentMesh = ExecuteCachedMeshStage(
                        build,
                        runtimeCache,
                        stageKey,
                        "Retaining Wall",
                        ComputeModifierStageFingerprint(snapshot, terrain, retainingWall, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, retainingWall.Label) : ApplyRetainingWalls(snapshot, terrain, currentMesh, retainingWall, build, mode),
                        result => DescribeModifierMeshResult(retainingWall.Label, result),
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case GradePadModifierDefinition gradePad:
                    usedStageKeys.Add(TerrainStageKey.CreateGradingTopology(stageKey, "Pad"));
                    currentMesh = BuildGradePadMesh(
                        snapshot,
                        terrain,
                        gradePad,
                        build,
                        runtimeCache,
                        indexedModifier.index,
                        stageKey,
                        currentMesh,
                        currentMeshFingerprint,
                        mode,
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case GradePathModifierDefinition gradePath:
                    usedStageKeys.Add(TerrainStageKey.CreateGradingTopology(stageKey, "Path"));
                    currentMesh = ExecuteCachedMeshStage(
                        build,
                        runtimeCache,
                        stageKey,
                        "Grade Path",
                        ComputeModifierStageFingerprint(snapshot, terrain, gradePath, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, gradePath.Label) : ApplyGradePath(snapshot, terrain, currentMesh, gradePath, build, runtimeCache, indexedModifier.index, stageKey, mode),
                        result => DescribeModifierMeshResult(gradePath.Label, result),
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case InSituStairModifierDefinition inSituStair:
                    currentMesh = ExecuteCachedMeshStage(
                        build,
                        runtimeCache,
                        stageKey,
                        "In-Situ Stair",
                        ComputeModifierStageFingerprint(snapshot, terrain, inSituStair, currentMeshFingerprint),
                        () => currentMesh == null
                            ? WarnMissingMesh(build, inSituStair.Label)
                            : ApplyInSituStair(snapshot, terrain, currentMesh, inSituStair, build, mode),
                        result => DescribeModifierMeshResult(inSituStair.Label, result),
                        out currentMeshFingerprint,
                        shouldCancel);
                    if (runtimeCache.StageEntries.TryGetValue(stageKey, out var stairStageEntry))
                    {
                        if (!string.IsNullOrWhiteSpace(inSituStair.ComputedTreadDepthSummary))
                        {
                            stairStageEntry.StairSurfaceCount = inSituStair.ComputedSurfaceCount;
                            stairStageEntry.StairTreadDepthSummary = inSituStair.ComputedTreadDepthSummary;
                            stairStageEntry.StairStepCountSummary = inSituStair.ComputedStepCountSummary;
                        }
                        else if (!string.IsNullOrWhiteSpace(stairStageEntry.StairTreadDepthSummary))
                        {
                            inSituStair.ComputedSurfaceCount = stairStageEntry.StairSurfaceCount;
                            inSituStair.ComputedTreadDepthSummary = stairStageEntry.StairTreadDepthSummary;
                            inSituStair.ComputedStepCountSummary = stairStageEntry.StairStepCountSummary;
                        }
                    }
                    break;
            }
        }

        build.PrimaryMesh = currentMesh;
        build.BaseMesh = baseMesh ?? currentMesh;
        if (currentMesh != null && mode == TerrainBuildMode.Final)
        {
            ThrowIfCancellationRequested(shouldCancel);
            RhinoMesh analysisMesh = currentMesh;
            RhinoMesh baselineMesh = baseMesh ?? analysisMesh;
            string analysisStageKey = TerrainStageKey.ForMode(mode, "analysis");
            usedStageKeys.Add(analysisStageKey);
            build.AnalysisResults.AddRange(ExecuteCachedAnalysisStage(
                build,
                runtimeCache,
                analysisStageKey,
                ComputeAnalysisFingerprint(snapshot, terrain, baselineMesh, analysisMesh, baseMeshFingerprint, currentMeshFingerprint),
                () => BuildAnalyses(snapshot, terrain, baselineMesh, analysisMesh, build, shouldCancel),
                _ => DescribeMesh(analysisMesh),
                shouldCancel));

            string zonesStageKey = TerrainStageKey.ForMode(mode, "zones");
            usedStageKeys.Add(zonesStageKey);
            ExecuteCachedZonesStage(
                build,
                runtimeCache,
                zonesStageKey,
                ComputeZonesFingerprint(snapshot, terrain, analysisMesh, build.PersistentHardConstraints, currentMeshFingerprint),
                () => BuildTerrainZones(snapshot, analysisMesh, terrain, build),
                () => $"{build.ZoneObjects.Count:N0} zone outputs",
                shouldCancel);

            BuildObjectPlacements(snapshot, terrain, analysisMesh, build, shouldCancel);
        }

        ThrowIfCancellationRequested(shouldCancel);
        runtimeCache.PruneUnused(usedStageKeys, mode);
        totalTimer.Stop();
        build.RecordTiming("Build pipeline", totalTimer.Elapsed, DescribeBuildOutputs(build));
        return build;
    }

    private static RhinoMesh? ExecuteCachedMeshStage(
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        string stageName,
        ulong stageFingerprint,
        Func<RhinoMesh?> action,
        Func<RhinoMesh?, string?> detailFactory,
        out ulong outputFingerprint,
        Func<bool>? shouldCancel)
    {
        var timer = Stopwatch.StartNew();
        ThrowIfCancellationRequested(shouldCancel);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == stageFingerprint)
        {
            RhinoMesh? cachedMesh = RestoreCachedMeshStage(build, cachedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory(cachedMesh)));
            return cachedMesh;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int auxiliaryStart = build.AuxiliaryObjects.Count;
        RhinoMesh? result = action();
        ThrowIfCancellationRequested(shouldCancel);

        return StoreMeshStageCache(
            build,
            runtimeCache,
            stageKey,
            stageName,
            stageFingerprint,
            stageFingerprint,
            result,
            build.PersistentHardConstraints,
            build.AuxiliaryObjects.Skip(auxiliaryStart),
            build.Diagnostics.Skip(diagnosticsStart),
            detailFactory(result),
            timer,
            out outputFingerprint,
            shouldCancel,
            structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
    }

    private static List<TerrainAnalysisSummary> ExecuteCachedAnalysisStage(
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        ulong stageFingerprint,
        Func<List<TerrainAnalysisSummary>> action,
        Func<IReadOnlyList<TerrainAnalysisSummary>, string?> detailFactory,
        Func<bool>? shouldCancel)
    {
        const string stageName = "Analysis";
        var timer = Stopwatch.StartNew();
        ThrowIfCancellationRequested(shouldCancel);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == stageFingerprint)
        {
            build.Diagnostics.AddRange(cachedEntry.Diagnostics);
            build.StructuredDiagnostics.AddRange(cachedEntry.StructuredDiagnostics);
            List<TerrainAnalysisSummary> cachedAnalysis = TerrainRuntimeCacheCloner.CloneAnalyses(cachedEntry.AnalysisOutput);
            build.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.AuxiliaryObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory(cachedAnalysis)));
            return cachedAnalysis;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int auxiliaryStart = build.AuxiliaryObjects.Count;
        List<TerrainAnalysisSummary> analysis = action();
        ThrowIfCancellationRequested(shouldCancel);
        timer.Stop();

        runtimeCache.StageEntries[stageKey] = new StageCacheEntry
        {
            StageName = stageName,
            PreResolutionFingerprint = stageFingerprint,
            ResolvedInputFingerprint = stageFingerprint,
            OutputFingerprint = stageFingerprint,
            AnalysisOutput = TerrainRuntimeCacheCloner.CloneAnalyses(analysis),
            AuxiliaryObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.AuxiliaryObjects.Skip(auxiliaryStart)),
            Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList(),
            StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList()
        };

        build.RecordTiming(stageName, timer.Elapsed, detailFactory(analysis));
        return analysis;
    }

    private static void ExecuteCachedZonesStage(
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        ulong stageFingerprint,
        Action action,
        Func<string?> detailFactory,
        Func<bool>? shouldCancel)
    {
        const string stageName = "Zones";
        var timer = Stopwatch.StartNew();
        ThrowIfCancellationRequested(shouldCancel);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == stageFingerprint)
        {
            build.Diagnostics.AddRange(cachedEntry.Diagnostics);
            build.StructuredDiagnostics.AddRange(cachedEntry.StructuredDiagnostics);
            build.ZoneObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.ZoneObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int zoneStart = build.ZoneObjects.Count;
        action();
        ThrowIfCancellationRequested(shouldCancel);
        timer.Stop();

        runtimeCache.StageEntries[stageKey] = new StageCacheEntry
        {
            StageName = stageName,
            PreResolutionFingerprint = stageFingerprint,
            ResolvedInputFingerprint = stageFingerprint,
            OutputFingerprint = stageFingerprint,
            ZoneObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.ZoneObjects.Skip(zoneStart)),
            Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList(),
            StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList()
        };

        build.RecordTiming(stageName, timer.Elapsed, detailFactory());
    }

    private static void ExecuteCachedMarkersStage(
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        ulong stageFingerprint,
        Action action,
        Func<string?> detailFactory,
        Func<bool>? shouldCancel)
    {
        const string stageName = "Markers";
        var timer = Stopwatch.StartNew();
        ThrowIfCancellationRequested(shouldCancel);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == stageFingerprint)
        {
            build.Diagnostics.AddRange(cachedEntry.Diagnostics);
            build.StructuredDiagnostics.AddRange(cachedEntry.StructuredDiagnostics);
            build.MarkerObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.MarkerObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int markerStart = build.MarkerObjects.Count;
        action();
        ThrowIfCancellationRequested(shouldCancel);
        timer.Stop();

        runtimeCache.StageEntries[stageKey] = new StageCacheEntry
        {
            StageName = stageName,
            PreResolutionFingerprint = stageFingerprint,
            ResolvedInputFingerprint = stageFingerprint,
            OutputFingerprint = stageFingerprint,
            MarkerObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.MarkerObjects.Skip(markerStart)),
            Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList(),
            StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList()
        };

        build.RecordTiming(stageName, timer.Elapsed, detailFactory());
    }

    private static RhinoMesh? WarnMissingMesh(TerrainBuildResult build, string modifierLabel)
    {
        build.Diagnostics.Add($"{modifierLabel} requires a terrain mesh generated earlier in the stack.");
        return null;
    }

    private static TerrainTolerancePolicy.Profile GetToleranceProfile(TerrainBuildSnapshot snapshot, TerrainDefinition terrain)
    {
        return TerrainTolerancePolicy.Create(
            terrain.GlobalTolerance,
            snapshot.ModelAbsoluteTolerance,
            snapshot.ModelUnitSystem);
    }

    private static void AddToleranceDiagnostics(TerrainBuildResult build, TerrainTolerancePolicy.Profile profile)
    {
        build.Diagnostics.Add(
            $"Terrain detail size: {profile.DetailSize:G4}; input merge tolerance: {profile.InputMergeTolerance:G4}; curve tolerance: {profile.CurveChordTolerance:G4}; Grade Path tolerance: {profile.GradePathTolerance:G4}; Grade Pad tolerance: {profile.GradePadTolerance:G4}.");
    }

    private static RhinoMesh? BuildTinMesh(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        TriangulateModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        out ulong outputFingerprint,
        Func<bool>? shouldCancel = null)
    {
        const string stageName = "Triangulate";
        var timer = Stopwatch.StartNew();
        ulong preResolutionFingerprint = ComputeTriangulatePreResolutionFingerprint(snapshot, terrain, modifier);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == preResolutionFingerprint)
        {
            RhinoMesh? cachedMesh = RestoreCachedMeshStage(build, cachedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, cachedMesh)));
            return cachedMesh;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        ThrowIfCancellationRequested(shouldCancel);
        var points = TerrainBuildSnapshotResolver.ResolvePoints(snapshot, modifier.Points);
        var breaklineCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Breaklines);
        var contourCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Contours);
        var boundaryCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundary);

        if (points.Count == 0 && breaklineCurves.Count == 0 && contourCurves.Count == 0)
        {
            build.Diagnostics.Add("Triangulate has no point, contour, or breakline sources.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                0,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double inputTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.InputMergeTolerance;
        double curveTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.CurveChordTolerance;

        var spotXyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            spotXyz[i * 3] = points[i].X;
            spotXyz[i * 3 + 1] = points[i].Y;
            spotXyz[i * 3 + 2] = points[i].Z;
        }

        var persistentHardConstraints = CreateConstraintPolylines(
            breaklineCurves,
            curveTolerance,
            preserveInputElevation: true);
        var persistentElevationConstraints = CreateConstraintPolylines(
            contourCurves,
            curveTolerance,
            preserveInputElevation: true);

        ThrowIfCancellationRequested(shouldCancel);
        var polylines = TerrainTriangulationInputBuilder.CreateTriangulationPolylines(
            breaklineCurves,
            contourCurves,
            curveTolerance);
        var boundaryPolylines = CreateBoundaryPolylines(boundaryCurves, curveTolerance);

        var breaklineData = BreaklineDiscretizer.Process(polylines, shouldCancel);
        var merged = PointCloudProcessor.Merge(spotXyz, points.Count, breaklineData, inputTolerance, shouldCancel);
        ThrowIfCancellationRequested(shouldCancel);
        ulong resolvedInputFingerprint = ComputeTriangulateResolvedInputFingerprint(
            terrain,
            modifier,
            inputTolerance,
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            persistentHardConstraints,
            boundaryPolylines);

        if (cachedEntry != null &&
            cachedEntry.ResolvedInputFingerprint == resolvedInputFingerprint)
        {
            StageCacheEntry refreshedEntry = CloneStageCacheEntry(
                cachedEntry,
                preResolutionFingerprint,
                resolvedInputFingerprint);
            runtimeCache.StageEntries[stageKey] = refreshedEntry;

            RhinoMesh? resolvedCachedMesh = RestoreCachedMeshStage(build, refreshedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, resolvedCachedMesh)));
            return resolvedCachedMesh;
        }

        if (merged.VertexCount < 3)
        {
            build.Diagnostics.Add("Triangulate needs at least three unique points after deduplication.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        if (merged.InvalidsSkipped > 0)
            build.Diagnostics.Add($"{merged.DescribeInvalidPoints()} during triangulation.");
        if (merged.DuplicatesRemoved > 0)
            build.Diagnostics.Add($"{merged.DuplicatesRemoved} duplicate points merged during triangulation.");

        if (TryBuildValidatedTinMesh(
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            boundaryPolylines,
            inputTolerance,
            modifier.CreateBoundaryPeelSettings(),
            runtimeCache.TinEngine,
            runtimeCache.CoreCaseRecorder,
            stageName,
            out var exactMesh,
            out var exactMessage,
            shouldCancel))
        {
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(persistentHardConstraints);
            build.PersistentElevationConstraints.Clear();
            build.PersistentElevationConstraints.AddRange(persistentElevationConstraints);
            if (!string.IsNullOrWhiteSpace(exactMessage))
                build.Diagnostics.Add(exactMessage);
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                exactMesh,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, exactMesh),
                timer,
                out outputFingerprint);
        }

        if (!ShouldAttemptTriangulationCleanupRetry(merged.VertexCount, merged.SegmentCount, shouldCancel, out var cleanupSkipMessage))
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add(cleanupSkipMessage);
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint,
                shouldCancel);
        }

        var cleanup = TinInputCleaner.Clean(merged, inputTolerance);
        ThrowIfCancellationRequested(shouldCancel);
        if (!cleanup.HasChanges)
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add($"Automatic input cleanup made no safe changes: {cleanup.ToDiagnosticSummary()}.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint);
        }

        if (cleanup.VertexCount < 3)
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add($"Automatic input cleanup reduced the dataset below three usable vertices: {cleanup.ToDiagnosticSummary()}.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint);
        }

        ThrowIfCancellationRequested(shouldCancel);
        if (!TryBuildValidatedTinMesh(
            cleanup.XyCoords,
            cleanup.ZValues,
            cleanup.Segments,
            boundaryPolylines,
            inputTolerance,
            modifier.CreateBoundaryPeelSettings(),
            runtimeCache.TinEngine,
            runtimeCache.CoreCaseRecorder,
            $"{stageName} Cleanup Retry",
            out var cleanedMesh,
            out var cleanupMessage,
            shouldCancel))
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add($"Automatic input cleanup retry failed: {cleanup.ToDiagnosticSummary()}.");
            if (!string.IsNullOrWhiteSpace(cleanupMessage))
                build.Diagnostics.Add(cleanupMessage);
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint);
        }

        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(persistentHardConstraints);
        build.PersistentElevationConstraints.Clear();
        build.PersistentElevationConstraints.AddRange(persistentElevationConstraints);
        build.Diagnostics.Add($"Automatic input cleanup retry succeeded: {cleanup.ToDiagnosticSummary()}.");
        if (!string.IsNullOrWhiteSpace(cleanupMessage))
            build.Diagnostics.Add(cleanupMessage);
        return StoreMeshStageCache(
            build,
            runtimeCache,
            stageKey,
            stageName,
            preResolutionFingerprint,
            resolvedInputFingerprint,
            cleanedMesh,
            build.PersistentHardConstraints,
            Array.Empty<GeneratedRhinoObject>(),
            build.Diagnostics.Skip(diagnosticsStart),
            DescribeModifierMeshResult(modifier.Label, cleanedMesh),
            timer,
            out outputFingerprint);
    }

    private static RhinoMesh ApplyAddGeometry(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        AddGeometryModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        Func<bool>? shouldCancel = null)
    {
        ThrowIfCancellationRequested(shouldCancel);
        var points = TerrainBuildSnapshotResolver.ResolvePoints(snapshot, modifier.Points);
        var breaklineCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Breaklines);
        var contourCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Contours);
        var boundaryCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundary);

        if (points.Count == 0 && breaklineCurves.Count == 0 && contourCurves.Count == 0 && boundaryCurves.Count == 0)
        {
            build.Diagnostics.Add("Add Geometry has no sources.");
            return mesh.DuplicateMesh();
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var meshVertices, out _, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for add geometry.");
            return mesh;
        }

        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double inputTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.InputMergeTolerance;
        double curveTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.CurveChordTolerance;

        int existingPointCount = meshVertices.Length / 3;
        var spotXyz = new double[(existingPointCount + points.Count) * 3];
        Array.Copy(meshVertices, spotXyz, meshVertices.Length);
        for (int i = 0; i < points.Count; i++)
        {
            int targetIndex = (existingPointCount + i) * 3;
            spotXyz[targetIndex] = points[i].X;
            spotXyz[targetIndex + 1] = points[i].Y;
            spotXyz[targetIndex + 2] = points[i].Z;
        }

        var newHardConstraints = CreateConstraintPolylines(
            breaklineCurves,
            curveTolerance,
            preserveInputElevation: true);
        var persistentHardConstraints = CombineConstraints(build.PersistentHardConstraints, newHardConstraints);
        var newElevationConstraints = CreateConstraintPolylines(
            contourCurves,
            curveTolerance,
            preserveInputElevation: true);
        var persistentElevationConstraints = CombineConstraints(build.PersistentElevationConstraints, newElevationConstraints);

        ThrowIfCancellationRequested(shouldCancel);
        var polylines = CreateFlatPolylines(CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints));
        polylines.AddRange(TerrainTriangulationInputBuilder.CreateTriangulationPolylines(
            breaklineCurves,
            contourCurves,
            curveTolerance));

        var boundaryPolylines = CombineBoundaryPolylines(
            CreateBoundaryPolylines(mesh, curveTolerance),
            CreateBoundaryPolylines(boundaryCurves, curveTolerance));

        var breaklineData = BreaklineDiscretizer.Process(polylines, shouldCancel);
        var merged = PointCloudProcessor.Merge(spotXyz, existingPointCount + points.Count, breaklineData, inputTolerance, shouldCancel);
        if (merged.VertexCount < 3)
        {
            build.Diagnostics.Add("Add Geometry needs at least three unique points after deduplication.");
            return mesh;
        }

        if (merged.InvalidsSkipped > 0)
            build.Diagnostics.Add($"{merged.DescribeInvalidPoints()} during add geometry.");
        if (merged.DuplicatesRemoved > 0)
            build.Diagnostics.Add($"{merged.DuplicatesRemoved} duplicate points merged during add geometry.");

        if (TryBuildValidatedTinMesh(
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            boundaryPolylines,
            inputTolerance,
            modifier.CreateBoundaryPeelSettings(),
            runtimeCache.TinEngine,
            runtimeCache.CoreCaseRecorder,
            modifier.Label,
            out var exactMesh,
            out var exactMessage,
            shouldCancel))
        {
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(persistentHardConstraints);
            build.PersistentElevationConstraints.Clear();
            build.PersistentElevationConstraints.AddRange(persistentElevationConstraints);
            if (!string.IsNullOrWhiteSpace(exactMessage))
                build.Diagnostics.Add(exactMessage);
            return exactMesh!;
        }

        if (!ShouldAttemptTriangulationCleanupRetry(merged.VertexCount, merged.SegmentCount, shouldCancel, out var cleanupSkipMessage))
        {
            build.Diagnostics.Add(exactMessage ?? "Add Geometry failed.");
            build.Diagnostics.Add(cleanupSkipMessage);
            return mesh;
        }

        var cleanup = TinInputCleaner.Clean(merged, inputTolerance);
        if (!cleanup.HasChanges)
        {
            build.Diagnostics.Add(exactMessage ?? "Add Geometry failed.");
            build.Diagnostics.Add($"Automatic input cleanup made no safe changes: {cleanup.ToDiagnosticSummary()}.");
            return mesh;
        }

        if (cleanup.VertexCount < 3)
        {
            build.Diagnostics.Add(exactMessage ?? "Add Geometry failed.");
            build.Diagnostics.Add($"Automatic input cleanup reduced the dataset below three usable vertices: {cleanup.ToDiagnosticSummary()}.");
            return mesh;
        }

        if (!TryBuildValidatedTinMesh(
            cleanup.XyCoords,
            cleanup.ZValues,
            cleanup.Segments,
            boundaryPolylines,
            inputTolerance,
            modifier.CreateBoundaryPeelSettings(),
            runtimeCache.TinEngine,
            runtimeCache.CoreCaseRecorder,
            $"{modifier.Label} Cleanup Retry",
            out var cleanedMesh,
            out var cleanupMessage,
            shouldCancel))
        {
            build.Diagnostics.Add(exactMessage ?? "Add Geometry failed.");
            build.Diagnostics.Add($"Automatic input cleanup retry failed: {cleanup.ToDiagnosticSummary()}.");
            if (!string.IsNullOrWhiteSpace(cleanupMessage))
                build.Diagnostics.Add(cleanupMessage);
            return mesh;
        }

        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(persistentHardConstraints);
        build.PersistentElevationConstraints.Clear();
        build.PersistentElevationConstraints.AddRange(persistentElevationConstraints);
        build.Diagnostics.Add($"Automatic input cleanup retry succeeded: {cleanup.ToDiagnosticSummary()}.");
        if (!string.IsNullOrWhiteSpace(cleanupMessage))
            build.Diagnostics.Add(cleanupMessage);
        return cleanedMesh!;
    }

    private static bool TryBuildValidatedTinMesh(
        double[] xyCoords,
        double[] zValues,
        int[] segments,
        TinBoundaryPreparer.BoundaryPolyline[] boundaryPolylines,
        double tolerance,
        BoundaryTrianglePeelSettings boundaryPeelSettings,
        TinEngine engine,
        TerrainCoreCaseRecorder? coreCaseRecorder,
        string stageName,
        out RhinoMesh? mesh,
        out string? message,
        Func<bool>? shouldCancel = null)
    {
        mesh = null;
        ThrowIfCancellationRequested(shouldCancel);

        var prepared = TinBoundaryPreparer.Prepare(
            xyCoords,
            zValues,
            segments,
            boundaryPolylines,
            tolerance);
        ThrowIfCancellationRequested(shouldCancel);

        var result = engine.Build(
            prepared.XyCoords,
            prepared.ZValues,
            prepared.Segments,
            QualitySettings.None,
            out message,
            useConvexHull: prepared.UseConvexHull,
            boundaryPeelSettings: boundaryPeelSettings,
            shouldCancel: shouldCancel);
        ThrowIfCancellationRequested(shouldCancel);

        if (!string.IsNullOrWhiteSpace(prepared.WarningMessage))
            message = AppendBuildMessage(message, prepared.WarningMessage);
        if (!string.IsNullOrWhiteSpace(prepared.InfoMessage))
            message = AppendBuildMessage(message, prepared.InfoMessage);

        coreCaseRecorder?.RecordTin(
            stageName,
            prepared.XyCoords,
            prepared.ZValues,
            prepared.Segments,
            prepared.UseConvexHull,
            boundaryPeelSettings,
            result != null,
            result?.VertexCount,
            result?.FaceCount,
            message);

        if (result == null)
            return false;

        try
        {
            mesh = RhinoGeometryConversions.ToRhinoMesh(result);
        }
        catch (Exception ex)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? $"Triangulation produced an invalid mesh: {ex.Message}"
                : $"{message} Triangulation produced an invalid mesh: {ex.Message}";
            mesh = null;
            return false;
        }

        if (mesh.Faces.Count == 0 || mesh.Vertices.Count == 0 || !mesh.IsValid)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? "Triangulation produced an invalid mesh."
                : $"{message} Triangulation produced an invalid mesh.";
            mesh = null;
            return false;
        }

        return true;
    }

    private static bool ShouldAttemptTriangulationCleanupRetry(
        int vertexCount,
        int segmentCount,
        Func<bool>? shouldCancel,
        out string message)
    {
        ThrowIfCancellationRequested(shouldCancel);

        if (vertexCount > 120_000 || segmentCount > 180_000)
        {
            message = $"Automatic triangulation cleanup retry skipped for large input ({vertexCount:N0} verts, {segmentCount:N0} segments) to keep rebuilds responsive.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static RhinoMesh ApplyRemesh(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RemeshModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double tolerance = toleranceProfile.CurveChordTolerance;
        double previewEdgeLength = modifier.EdgeLength;
        double previewMaxArea = modifier.MaxArea;
        double previewMinAngle = modifier.MinAngle;
        if (mode == TerrainBuildMode.Preview)
        {
            if (previewEdgeLength > 0)
                previewEdgeLength *= 2.0;
            if (previewMaxArea > 0)
                previewMaxArea *= 4.0;
            previewMinAngle = 0.0;
        }

        var localConstraints = CreateConstraintPolylines(
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Constraints),
            tolerance,
            preserveInputElevation: false,
            requestedEdgeLength: previewEdgeLength,
            maxArea: previewMaxArea);

        if (modifier.EdgeLength <= 0 && modifier.MaxArea <= 0 && modifier.MinAngle <= 0 && localConstraints.Count == 0)
            return mesh.DuplicateMesh();

        var constraints = CombineConstraints(build.PersistentHardConstraints, localConstraints);

        var remeshed = RebuildMeshWithConstraints(
            snapshot,
            terrain,
            mesh,
            constraints,
            previewEdgeLength,
            previewMaxArea,
            previewMinAngle,
            "Remesh",
            build,
            out _,
            toleranceOverride: toleranceProfile.RemeshConstraintTolerance);

        return remeshed;
    }

    private static RhinoMesh ApplySmooth(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        SmoothModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        TerrainBuildMode mode)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for smoothing.");
            return mesh;
        }

        double tolerance = GetToleranceProfile(snapshot, terrain).CurveChordTolerance;
        double effectiveStrength = Math.Clamp(modifier.Strength, 0.0, 1.0);
        var boundaries = new List<(double[] xyVerts, int vertCount)>();
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundaries))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out var polyline))
                continue;

            int count = polyline.Count;
            if (polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;

            var xyVerts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xyVerts[i * 2] = polyline[i].X;
                xyVerts[i * 2 + 1] = polyline[i].Y;
            }

            boundaries.Add((xyVerts, count));
        }

        var breaklines = new List<MeshSmoother.BreaklinePolyline>();
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Breaklines))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            int count = polyline.Count;
            bool isClosed = curve.IsClosed || (count > 2 && polyline[0].DistanceTo(polyline[^1]) <= tolerance);
            if (isClosed && polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;

            var xyPts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xyPts[i * 2] = polyline[i].X;
                xyPts[i * 2 + 1] = polyline[i].Y;
            }

            breaklines.Add(new MeshSmoother.BreaklinePolyline(xyPts, count, isClosed));
        }

        string preparedStageKey = TerrainStageKey.CreateSmoothPrepared(stageKey);
        ulong preparedFingerprint = ComputeSmoothPreparedFingerprint(
            snapshot,
            terrain,
            modifier,
            ComputeMeshFingerprint(mesh));
        MeshSmoother.PreparedSmoothingData prepared;
        if (runtimeCache.SmoothEntries.TryGetValue(preparedStageKey, out var cachedPrepared) &&
            cachedPrepared.Fingerprint == preparedFingerprint)
        {
            prepared = TerrainRuntimeCacheCloner.CloneSmoothStageCacheEntry(cachedPrepared).Prepared;
        }
        else
        {
            prepared = MeshSmoother.Prepare(
                vertices,
                mesh.Vertices.Count,
                faces,
                mesh.Faces.Count,
                boundaries.ToArray(),
                breaklines.ToArray(),
                tolerance);
            runtimeCache.SmoothEntries[preparedStageKey] = new SmoothStageCacheEntry
            {
                Fingerprint = preparedFingerprint,
                Prepared = prepared
            };
        }

        var smoothed = MeshSmoother.SmoothPrepared(
            vertices,
            prepared,
            effectiveStrength,
            Math.Clamp(modifier.BreaklineFixity, 0.0, 1.0),
            mode == TerrainBuildMode.Preview
                ? Math.Min(2, Math.Max(1, modifier.Iterations))
                : Math.Max(1, modifier.Iterations));

        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            smoothed[i * 3] = vertices[i * 3];
            smoothed[i * 3 + 1] = vertices[i * 3 + 1];
        }

        if (smoothed.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            build.Diagnostics.Add("Smooth produced invalid mesh data. Original mesh kept.");
            return mesh;
        }

        var smoothedMesh = RhinoGeometryConversions.BuildMesh(smoothed, mesh.Vertices.Count, faces, mesh.Faces.Count);
        if (smoothedMesh.Faces.Count == 0 || smoothedMesh.Vertices.Count == 0 || !smoothedMesh.IsValid)
        {
            build.Diagnostics.Add("Smooth produced an invalid mesh. Original mesh kept.");
            return mesh;
        }

        return smoothedMesh;
    }

    private static void BuildTerrainZones(TerrainBuildSnapshot snapshot, RhinoMesh mesh, TerrainDefinition terrain, TerrainBuildResult build)
    {
        if (terrain.Zones.Count == 0)
            return;

        var totalTimer = Stopwatch.StartNew();
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for terrain zones.");
            return;
        }

        var entries = new List<ZoneBoundaryEntry>();
        double tolerance = GetToleranceProfile(snapshot, terrain).InputMergeTolerance;
        var resolveTimer = Stopwatch.StartNew();
        for (int zoneIndex = 0; zoneIndex < terrain.Zones.Count; zoneIndex++)
        {
            var zone = terrain.Zones[zoneIndex];
            if (!zone.IsEnabled)
                continue;

            var zoneEntries = ResolveZoneBoundaries(snapshot, zone, zoneIndex, tolerance);
            if (zone.Boundaries.HasReferences && zoneEntries.Count == 0)
            {
                build.Diagnostics.Add($"Zone '{zone.Name}' has no valid closed curves or horizontal planar surfaces.");
            }

            entries.AddRange(zoneEntries);
        }
        resolveTimer.Stop();

        if (entries.Count == 0)
        {
            build.Diagnostics.Add("Zones has no valid closed boundaries.");
            return;
        }

        entries.Sort(CompareZoneEntries);
        var boundaries = entries.Select(entry => entry.Boundary).ToArray();

        var splitTimer = Stopwatch.StartNew();
        var result = MeshAreaSplitter.SplitPreservingTopology(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            boundaries,
            tolerance,
            out var splitWarning);
        splitTimer.Stop();

        if (result == null)
        {
            build.Diagnostics.Add(splitWarning ?? "Zones failed.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(splitWarning))
            build.Diagnostics.Add(splitWarning);

        var zoneOutputCounts = new Dictionary<Guid, int>();
        var outputTimer = Stopwatch.StartNew();
        for (int i = 0; i < entries.Count; i++)
        {
            var subMesh = RhinoGeometryConversions.BuildSubMesh(result, i);
            if (subMesh.Faces.Count == 0)
                continue;

            var zone = entries[i].Zone;
            zoneOutputCounts.TryGetValue(zone.ZoneId, out int currentCount);
            currentCount++;
            zoneOutputCounts[zone.ZoneId] = currentCount;

            string outputName = currentCount == 1 ? zone.Name : $"{zone.Name} {currentCount}";
            build.ZoneObjects.Add(new GeneratedRhinoObject
            {
                Geometry = subMesh,
                Name = outputName,
                ColorArgb = null,
                LayerPath = GetBakedLayerPath(entries[i].InputLayerPath),
                SourceLayerPath = entries[i].InputLayerPath,
                MaterialName = null
            });
        }
        outputTimer.Stop();
        totalTimer.Stop();

        build.RecordTiming(
            "Zones",
            totalTimer.Elapsed,
            $"{entries.Count} boundaries over {mesh.Faces.Count:N0} source faces; resolve {resolveTimer.Elapsed.TotalSeconds:0.##} s, classify {splitTimer.Elapsed.TotalSeconds:0.##} s, output {outputTimer.Elapsed.TotalSeconds:0.##} s",
            StageTimingDiagnosticThresholdMs);
    }

    private static RhinoMesh ApplyRetainingWalls(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RetainingWallModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double wallTolerance = toleranceProfile.RetainingWallTolerance(modifier.MaxWallWidth);
        double maxWallWidth = Math.Max(wallTolerance, modifier.MaxWallWidth);
        build.Diagnostics.Add($"Retaining Wall tolerance: {wallTolerance:G4}; max wall width: {maxWallWidth:G4}.");
        var resolveTimer = Stopwatch.StartNew();
        var wallCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.WallCurves);
        resolveTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Resolve",
            resolveTimer.Elapsed,
            $"{wallCurves.Count:N0} curve inputs",
            StageTimingDiagnosticThresholdMs);
        if (wallCurves.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no curve inputs.");
            return mesh;
        }

        var planTimer = Stopwatch.StartNew();
        var plan = RetainingWallPlannerCore.Plan(
            wallCurves,
            maxWallWidth,
            curveParsingTolerance: wallTolerance);
        planTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Plan",
            plan.Timing.Total > TimeSpan.Zero ? plan.Timing.Total : planTimer.Elapsed,
            DescribeRetainingWallPlanTiming(plan.Timing, plan.Walls.Count),
            StageTimingDiagnosticThresholdMs);
        foreach (var entry in plan.Report)
            build.Diagnostics.Add(entry.ToString());

        if (plan.Walls.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall produced no accepted wall pairs.");
            return mesh;
        }

        List<SurfaceRemesher.ConstraintPolyline> wallConstraints = new(plan.Walls.Count * 2);
        var wallOutputTimer = new Stopwatch();
        var constraintCurveTimer = new Stopwatch();
        int usableWallCount = 0;
        int wallBrepOutputCount = 0;
        foreach (var wall in plan.Walls)
        {
            constraintCurveTimer.Start();
            if (!IsWallStripUsable(wall.Rails, wallTolerance, out var stripMessage))
            {
                constraintCurveTimer.Stop();
                build.Diagnostics.Add($"Retaining wall pair ({wall.CurveA}, {wall.CurveB}) skipped: {stripMessage}");
                continue;
            }
            constraintCurveTimer.Stop();
            usableWallCount++;

            wallOutputTimer.Start();
            if (mode == TerrainBuildMode.Final)
            {
                if (wall.Brep != null)
                {
                    build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                    {
                        Geometry = wall.Brep,
                        Name = $"Wall {wall.CurveA}-{wall.CurveB}",
                        Kind = GeneratedObjectKind.RetainingWall,
                        LayerPath = TerrainDefinition.ResolveAuxiliaryLayerPath(modifier.OutputLayerPath ?? terrain.AuxiliaryLayerPath)
                    });
                    wallBrepOutputCount++;
                }
            }
            wallOutputTimer.Stop();

            constraintCurveTimer.Start();
            SurfaceRemesher.ConstraintPolyline[] wallSetConstraints = BuildWallConstraintCurves(wall.Rails, wallTolerance);
            constraintCurveTimer.Stop();
            if (wallSetConstraints.Length == 0)
                continue;

            wallConstraints.AddRange(wallSetConstraints);
        }
        build.RecordTiming(
            "Retaining Wall Outputs",
            wallOutputTimer.Elapsed,
            $"{wallBrepOutputCount:N0} Brep outputs from {usableWallCount:N0} usable walls",
            StageTimingDiagnosticThresholdMs);
        build.RecordTiming(
            "Retaining Wall Constraint Curves",
            constraintCurveTimer.Elapsed,
            $"{wallConstraints.Count:N0} raw rail constraints from {usableWallCount:N0} usable walls",
            StageTimingDiagnosticThresholdMs);

        int rawConstraintCount = wallConstraints.Count;
        var prepareTimer = Stopwatch.StartNew();
        wallConstraints = PrepareWallConstraintsForRemesh(mesh, wallConstraints, wallTolerance);
        prepareTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Constraint Prep",
            prepareTimer.Elapsed,
            $"{rawConstraintCount:N0} raw -> {wallConstraints.Count:N0} prepared constraints",
            StageTimingDiagnosticThresholdMs);

        if (wallConstraints.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no usable wall pairs to insert as breaklines.");
            return mesh;
        }

        var combineTimer = Stopwatch.StartNew();
        List<SurfaceRemesher.ConstraintPolyline> terrainElevationConstraints =
            CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints);
        List<SurfaceRemesher.ConstraintPolyline> remeshConstraints =
            CombineConstraints(terrainElevationConstraints, wallConstraints);
        combineTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Constraint Merge",
            combineTimer.Elapsed,
            $"{build.PersistentHardConstraints.Count:N0} hard + {build.PersistentElevationConstraints.Count:N0} elevation + {wallConstraints.Count:N0} wall -> {remeshConstraints.Count:N0} remesh constraints",
            StageTimingDiagnosticThresholdMs);

        var remeshTimer = Stopwatch.StartNew();
        var remeshed = RebuildMeshWithConstraints(
            snapshot,
            terrain,
            mesh,
            remeshConstraints,
            0.0,
            0.0,
            0.0,
            "Retaining Wall",
            build,
            out bool keptInputMesh,
            preferReducedInteriorSeed: true,
            addReducedInteriorGuideSeeds: false,
            addConstraintCorridorSeeds: false,
            toleranceOverride: wallTolerance,
            recordDetailedTimings: true);
        remeshTimer.Stop();
        build.RecordTiming(
            "Retaining Wall Remesh",
            remeshTimer.Elapsed,
            keptInputMesh
                ? "kept upstream mesh"
                : ReferenceEquals(remeshed, mesh) ? "returned upstream mesh" : $"{remeshed.Vertices.Count:N0} verts, {remeshed.Faces.Count:N0} faces",
            StageTimingDiagnosticThresholdMs);

        if (!ReferenceEquals(remeshed, mesh))
        {
            var persistTimer = Stopwatch.StartNew();
            List<SurfaceRemesher.ConstraintPolyline> mergedConstraints = CombineConstraints(build.PersistentHardConstraints, wallConstraints);
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(mergedConstraints);
            persistTimer.Stop();
            build.RecordTiming(
                "Retaining Wall Persist Constraints",
                persistTimer.Elapsed,
                $"{mergedConstraints.Count:N0} hard constraints",
                StageTimingDiagnosticThresholdMs);
            return remeshed;
        }

        if (keptInputMesh)
        {
            var fallbackTimer = Stopwatch.StartNew();
            bool inserted = TryInsertWallConstraintsIntoExistingMesh(
                mesh,
                wallConstraints,
                wallTolerance,
                build,
                reportFailures: true,
                afterCombinedRemeshFailed: true,
                out RhinoMesh insertedMesh);
            fallbackTimer.Stop();
            build.RecordTiming(
                "Retaining Wall Topology Fallback",
                fallbackTimer.Elapsed,
                inserted ? $"{insertedMesh.Vertices.Count:N0} verts, {insertedMesh.Faces.Count:N0} faces" : "not inserted",
                StageTimingDiagnosticThresholdMs);

            if (!inserted)
                return remeshed;

            var persistTimer = Stopwatch.StartNew();
            List<SurfaceRemesher.ConstraintPolyline> mergedConstraints = CombineConstraints(build.PersistentHardConstraints, wallConstraints);
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(mergedConstraints);
            persistTimer.Stop();
            build.RecordTiming(
                "Retaining Wall Persist Constraints",
                persistTimer.Elapsed,
                $"{mergedConstraints.Count:N0} hard constraints",
                StageTimingDiagnosticThresholdMs);
            return insertedMesh;
        }

        return remeshed;
    }

    private static string DescribeRetainingWallPlanTiming(RetainingWallPlannerCore.PlanTiming timing, int wallCount)
    {
        if (timing.Total <= TimeSpan.Zero)
            return $"{wallCount:N0} accepted walls";

        return $"{wallCount:N0} accepted walls; preprocess {FormatMilliseconds(timing.Preprocess)}, pairing {FormatMilliseconds(timing.Pairing)}, interactions {FormatMilliseconds(timing.Interactions)}, wall geometry {FormatMilliseconds(timing.Walls)}";
    }

    private static string FormatMilliseconds(TimeSpan elapsed)
    {
        return $"{elapsed.TotalMilliseconds:0.###} ms";
    }

    private static RhinoMesh? BuildGradePadMesh(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        GradePadModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        int modifierIndex,
        string stageKey,
        RhinoMesh? mesh,
        ulong upstreamFingerprint,
        TerrainBuildMode mode,
        out ulong outputFingerprint,
        Func<bool>? shouldCancel = null)
    {
        const string stageName = "Grade Pad";
        string topologyStageKey = TerrainStageKey.CreateGradingTopology(stageKey, "Pad");
        var timer = Stopwatch.StartNew();
        ulong preResolutionFingerprint = ComputeModifierStageFingerprint(snapshot, terrain, modifier, upstreamFingerprint);

        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == preResolutionFingerprint)
        {
            RhinoMesh? cachedMesh = RestoreCachedMeshStage(build, cachedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, cachedMesh)));
            return cachedMesh;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        ThrowIfCancellationRequested(shouldCancel);
        if (mesh == null)
        {
            WarnMissingMesh(build, modifier.Label);
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                0,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint);
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade pad.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                preResolutionFingerprint,
                mesh,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, mesh),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double curveTolerance = toleranceProfile.CurveChordTolerance;
        double gradePadTolerance = toleranceProfile.GradePadTolerance;
        ResolvedGradePadInputs resolvedInputs = ResolveGradePadInputs(
            snapshot,
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            modifier,
            curveTolerance,
            gradePadTolerance);
        build.Diagnostics.AddRange(resolvedInputs.Diagnostics);
        build.StructuredDiagnostics.AddRange(resolvedInputs.StructuredDiagnostics);
        ThrowIfCancellationRequested(shouldCancel);
        if (resolvedInputs.Pads.Length == 0)
        {
            build.Diagnostics.Add("Grade Pad has no valid closed boundaries.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                preResolutionFingerprint,
                mesh,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, mesh),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        var effectiveLocks = CombinePadLockCurves(resolvedInputs.Locks, build.PersistentHardConstraints);
        List<GradingPatch> patchSummaries = BuildPadPatchSummaries(resolvedInputs.Pads);
        List<string> dirtyStageKeys = runtimeCache.FindIntersectingGradingStageKeys(
            TerrainRuntimeCache.GetStagePrefix(mode),
            modifierIndex,
            patchSummaries,
            topologyStageKey);
        if (dirtyStageKeys.Count > 0)
        {
            runtimeCache.InvalidateStages(dirtyStageKeys);
            build.Diagnostics.Add(
                $"Grade Pad invalidated {dirtyStageKeys.Count} overlapping downstream grading stage(s): {string.Join(", ", dirtyStageKeys.Select(TerrainStageKey.GetBase))}.");
        }

        ulong topologyFingerprint = ComputeGradePadTopologyFingerprint(
            upstreamFingerprint,
            gradePadTolerance,
            modifier,
            resolvedInputs.Pads,
            effectiveLocks);

        GradingTopologyCacheEntry? topologyEntry;
        var topologyTimer = Stopwatch.StartNew();
        if (runtimeCache.GradingTopologyEntries.TryGetValue(topologyStageKey, out var cachedTopologyEntry) &&
            string.Equals(cachedTopologyEntry.GraderKind, "Pad", StringComparison.Ordinal) &&
            cachedTopologyEntry.Fingerprint == topologyFingerprint)
        {
            topologyEntry = TerrainRuntimeCacheCloner.CloneGradingTopologyEntry(cachedTopologyEntry);
            build.Diagnostics.AddRange(topologyEntry.Diagnostics);
            build.StructuredDiagnostics.AddRange(topologyEntry.StructuredDiagnostics);
            topologyTimer.Stop();
            build.RecordTiming(
                "Grade Pad Topology",
                topologyTimer.Elapsed,
                AppendCacheHitDetail($"{topologyEntry.VertexCount:N0} verts, {topologyEntry.FaceCount:N0} faces"),
                StageTimingDiagnosticThresholdMs);
        }
        else
        {
            ThrowIfCancellationRequested(shouldCancel);
            var topologyDiagnostics = new List<string>();
            var gradeResult = PadGrader.Grade(
                vertices,
                mesh.Vertices.Count,
                faces,
                mesh.Faces.Count,
                resolvedInputs.Pads,
                effectiveLocks.Length > 0 ? effectiveLocks : null,
                out var gradeWarning,
                out IReadOnlyList<MoleHill.Core.Grading.OutputPolyline> failureOutputPolylines,
                gradePadTolerance,
                toleranceProfile.DetailSize);
            ThrowIfCancellationRequested(shouldCancel);
            runtimeCache.CoreCaseRecorder?.RecordPad(
                modifier.Label,
                vertices,
                mesh.Vertices.Count,
                faces,
                mesh.Faces.Count,
                resolvedInputs.Pads,
                effectiveLocks.Length > 0 ? effectiveLocks : null,
                gradeResult != null,
                gradeResult?.VertexCount,
                gradeResult?.FaceCount,
                gradeWarning);

            if (!string.IsNullOrWhiteSpace(gradeWarning))
                topologyDiagnostics.Add(gradeWarning!);

            double[] topologyVertices;
            int topologyVertexCount;
            int[] topologyFaces;
            int topologyFaceCount;
            bool gradePadTopologyFailed = gradeResult == null;
            if (gradeResult == null)
            {
                if (string.IsNullOrWhiteSpace(gradeWarning))
                    topologyDiagnostics.Add("Grade Pad protected patch failed; upstream mesh retained.");
                build.Diagnostics.AddRange(topologyDiagnostics);
                AddOutputPolylinesAsBreaklines(failureOutputPolylines, build);
                topologyVertices = (double[])vertices.Clone();
                topologyVertexCount = mesh.Vertices.Count;
                topologyFaces = (int[])faces.Clone();
                topologyFaceCount = mesh.Faces.Count;
            }
            else
            {
                if (gradeResult.Diagnostics.Count > 0)
                    topologyDiagnostics.AddRange(gradeResult.Diagnostics);

                build.Diagnostics.AddRange(topologyDiagnostics);
                build.StructuredDiagnostics.AddRange(gradeResult.StructuredDiagnostics);
                AddOutputPolylinesAsBreaklines(gradeResult.OutputPolylines, build);
                topologyVertices = gradeResult.Vertices;
                topologyVertexCount = gradeResult.VertexCount;
                topologyFaces = gradeResult.Faces;
                topologyFaceCount = gradeResult.FaceCount;
            }

            topologyTimer.Stop();

            topologyEntry = new GradingTopologyCacheEntry
            {
                GraderKind = "Pad",
                Fingerprint = topologyFingerprint,
                OutputFingerprint = ComputeGradingTopologyOutputFingerprint("Pad", topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount),
                Vertices = topologyVertices,
                VertexCount = topologyVertexCount,
                Faces = topologyFaces,
                FaceCount = topologyFaceCount,
                PatchSummaries = gradeResult?.PatchSummaries.Count > 0
                    ? ClonePatchSummaries(gradeResult.PatchSummaries)
                    : patchSummaries,
                Diagnostics = topologyDiagnostics,
                StructuredDiagnostics = gradeResult?.StructuredDiagnostics.ToList() ?? new List<GradingDiagnostic>()
            };
            runtimeCache.GradingTopologyEntries[topologyStageKey] = TerrainRuntimeCacheCloner.CloneGradingTopologyEntry(topologyEntry);
            build.RecordTiming(
                "Grade Pad",
                topologyTimer.Elapsed,
                gradePadTopologyFailed
                    ? $"failed; upstream {topologyVertexCount:N0} verts, {topologyFaceCount:N0} faces retained"
                    : $"{topologyVertexCount:N0} verts, {topologyFaceCount:N0} faces",
                StageTimingDiagnosticThresholdMs);
        }

        bool gradePadStageFailed = topologyEntry.Diagnostics.Any(
            static diagnostic => diagnostic.Contains("split-local fallback is disabled", StringComparison.OrdinalIgnoreCase) ||
                                 diagnostic.Contains("Grade Pad protected patch failed", StringComparison.OrdinalIgnoreCase));
        build.Diagnostics.Add(gradePadStageFailed
            ? $"Grade Pad protected patch failed; upstream mesh retained ({DescribeTopologyCounts(mesh.Vertices.Count, mesh.Faces.Count, topologyEntry.VertexCount, topologyEntry.FaceCount)})."
            : $"Grade Pad local patch ({DescribeTopologyCounts(mesh.Vertices.Count, mesh.Faces.Count, topologyEntry.VertexCount, topologyEntry.FaceCount)}).");

        ulong resolvedInputFingerprint = ComputeGradePadResolvedInputFingerprint(topologyEntry.OutputFingerprint, resolvedInputs.Pads, modifier);
        if (cachedEntry != null && cachedEntry.ResolvedInputFingerprint == resolvedInputFingerprint)
        {
            StageCacheEntry refreshedEntry = CloneStageCacheEntry(cachedEntry, preResolutionFingerprint, resolvedInputFingerprint);
            runtimeCache.StageEntries[stageKey] = refreshedEntry;

            RhinoMesh? resolvedCachedMesh = RestoreCachedMeshStage(build, refreshedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, resolvedCachedMesh)));
            return resolvedCachedMesh;
        }

        var filterTimer = Stopwatch.StartNew();
        // Preview uses raw topology + Z-only grading. Full mode topology already has graded vertices
        // from PadGrader.Grade, so no second pass is needed.
        double[] gradedVertices = mode == TerrainBuildMode.Preview
            ? PadGrader.ApplyGradingZ(
                topologyEntry.Vertices,
                topologyEntry.VertexCount,
                topologyEntry.Faces,
                topologyEntry.FaceCount,
                resolvedInputs.Pads,
                effectiveLocks.Length > 0 ? effectiveLocks : null)
            : topologyEntry.Vertices;
        filterTimer.Stop();
        ThrowIfCancellationRequested(shouldCancel);
        build.RecordTiming(
            "Grade Pad Filter",
            filterTimer.Elapsed,
            $"{topologyEntry.VertexCount:N0} verts",
            StageTimingDiagnosticThresholdMs);

        RhinoMesh resultMesh = FinalizeGradingMesh(
            RhinoGeometryConversions.BuildMesh(gradedVertices, topologyEntry.VertexCount, topologyEntry.Faces, topologyEntry.FaceCount),
            "Grade Pad",
            build);
        AddPersistentElevationConstraints(build, resolvedInputs.Constraints);

        return StoreMeshStageCache(
            build,
            runtimeCache,
            stageKey,
            stageName,
            preResolutionFingerprint,
            resolvedInputFingerprint,
            resultMesh,
            build.PersistentHardConstraints,
            Array.Empty<GeneratedRhinoObject>(),
            build.Diagnostics.Skip(diagnosticsStart),
            DescribeModifierMeshResult(modifier.Label, resultMesh),
            timer,
            out outputFingerprint,
            structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
    }

    private static RhinoMesh ApplyGradePadLegacy(
        RhinoMesh mesh,
        double[] vertices,
        int[] faces,
        GradePadModifierDefinition modifier,
        ResolvedGradePadInputs resolvedInputs,
        PadGrader.LockCurve[] effectiveLocks,
        double tolerance,
        TerrainBuildResult build)
    {
        var result = PadGrader.Grade(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            resolvedInputs.Pads,
            effectiveLocks.Length == 0 ? null : effectiveLocks,
            out var warning,
            tolerance);

        if (result == null)
        {
            build.Diagnostics.Add(warning ?? "Grade Pad failed.");
            return mesh;
        }

        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);

        if (result.Diagnostics.Count > 0)
            build.AddGradingDiagnostics(result);

        return FinalizeGradingMesh(
            RhinoGeometryConversions.BuildMesh(result.Vertices, result.VertexCount, result.Faces, result.FaceCount),
            "Grade Pad",
            build);
    }

    private static ResolvedGradePadInputs ResolveGradePadInputs(
        TerrainBuildSnapshot snapshot,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        GradePadModifierDefinition modifier,
        double curveTolerance,
        double gradePadTolerance)
    {
        var pads = new List<PadGrader.PadBoundary>();
        var diagnostics = new List<string>();
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundaries))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, curveTolerance, requireClosed: true, out var polyline))
                continue;

            int count = polyline.Count;
            if (count > 1 && polyline[0].DistanceTo(polyline[^1]) < curveTolerance)
                count--;
            if (count < 3)
                continue;

            double stitchApronDistance = ModelUnits.FromMeters(0.5, snapshot.ModelUnitSystem);
            if (!TryCreatePlanarPadBoundary(polyline, count, modifier.SlopeAngle, modifier.MaxDistance, stitchApronDistance, out var pad, out string? diagnostic))
            {
                if (!string.IsNullOrWhiteSpace(diagnostic))
                    diagnostics.Add(diagnostic!);
                continue;
            }

            pads.Add(pad!);
        }

        var locks = new List<PadGrader.LockCurve>();
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.LockCurves))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, curveTolerance, requireClosed: false, out var polyline))
                continue;
            if (polyline.Count < 2)
                continue;

            var xyVerts = new double[polyline.Count * 2];
            for (int i = 0; i < polyline.Count; i++)
            {
                xyVerts[i * 2] = polyline[i].X;
                xyVerts[i * 2 + 1] = polyline[i].Y;
            }

            locks.Add(new PadGrader.LockCurve(xyVerts, polyline.Count));
        }

        PadGrader.PadBoundary[] padArray = pads.ToArray();
        PadGrader.LockCurve[] lockArray = locks.ToArray();
        PadGrader.ConstraintSet constraintSet = padArray.Length == 0
            ? new PadGrader.ConstraintSet
            {
                Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0,
                Diagnostics = Array.Empty<string>(),
                StructuredDiagnostics = Array.Empty<GradingDiagnostic>()
            }
            : PadGrader.CreateConstraints(
                vertices,
                vertexCount,
                faces,
                faceCount,
                padArray,
                lockArray.Length == 0 ? null : lockArray,
                gradePadTolerance);
        var allDiagnostics = new List<string>(diagnostics.Count + constraintSet.Diagnostics.Length);
        allDiagnostics.AddRange(diagnostics);
        allDiagnostics.AddRange(constraintSet.Diagnostics);

        return new ResolvedGradePadInputs
        {
            Pads = padArray,
            Locks = lockArray,
            Constraints = constraintSet.Constraints,
            SuggestedEdgeLength = constraintSet.SuggestedEdgeLength,
            Diagnostics = allDiagnostics.ToArray(),
            StructuredDiagnostics = constraintSet.StructuredDiagnostics
        };
    }

    private static bool TryCreatePlanarPadBoundary(
        Polyline polyline,
        int vertexCount,
        double slopeAngle,
        double maxDistance,
        double stitchApronDistance,
        out PadGrader.PadBoundary? pad,
        out string? diagnostic)
    {
        pad = null;
        diagnostic = null;

        var boundaryVertices = new double[vertexCount * 3];
        var points = new Point3d[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            Point3d point = polyline[i];
            points[i] = point;
            boundaryVertices[i * 3] = point.X;
            boundaryVertices[i * 3 + 1] = point.Y;
            boundaryVertices[i * 3 + 2] = point.Z;
        }

        if (!TryGetPadPlaneCoefficients(points, out double planeXCoeff, out double planeYCoeff, out double planeConstant))
        {
            diagnostic = "Grade Pad skipped a boundary because it did not define a stable planar pad.";
            return false;
        }

        pad = PadGrader.PadBoundary.CreatePlanar(
            boundaryVertices,
            vertexCount,
            planeXCoeff,
            planeYCoeff,
            planeConstant,
            slopeAngle,
            maxDistance,
            stitchApronDistance: stitchApronDistance);
        return true;
    }

    private static bool TryGetPadPlaneCoefficients(
        IReadOnlyList<Point3d> points,
        out double planeXCoeff,
        out double planeYCoeff,
        out double planeConstant)
    {
        planeXCoeff = 0.0;
        planeYCoeff = 0.0;
        planeConstant = 0.0;

        if (points.Count < 3)
            return false;

        PlaneFitResult fit = Plane.FitPlaneToPoints(points, out Plane plane);
        if (fit == PlaneFitResult.Failure || Math.Abs(plane.Normal.Z) < MinRepresentablePadPlaneNormalZ)
            return false;

        if (plane.Normal.Z < 0)
            plane.Flip();

        planeXCoeff = -plane.Normal.X / plane.Normal.Z;
        planeYCoeff = -plane.Normal.Y / plane.Normal.Z;
        planeConstant = plane.Origin.Z + (plane.Normal.X * plane.Origin.X + plane.Normal.Y * plane.Origin.Y) / plane.Normal.Z;
        return true;
    }

    private static SurfaceRemesher.ConstraintPolyline[] CreateGradePadConstraints(
        IReadOnlyList<PadGrader.PadBoundary> pads,
        IReadOnlyList<PadGrader.LockCurve> locks)
    {
        if (pads.Count == 0 && locks.Count == 0)
            return Array.Empty<SurfaceRemesher.ConstraintPolyline>();

        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(pads.Count + locks.Count);
        foreach (var pad in pads)
        {
            constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                (double[])pad.BoundaryVertices.Clone(),
                pad.VertexCount,
                IsClosed: true,
                PreserveInputElevation: false));
        }

        foreach (var lockCurve in locks)
        {
            var points = new double[lockCurve.VertexCount * 3];
            for (int i = 0; i < lockCurve.VertexCount; i++)
            {
                points[i * 3] = lockCurve.XyVertices[i * 2];
                points[i * 3 + 1] = lockCurve.XyVertices[i * 2 + 1];
            }

            constraints.Add(new SurfaceRemesher.ConstraintPolyline(points, lockCurve.VertexCount, IsClosed: false, PreserveInputElevation: false));
        }

        return constraints.ToArray();
    }

    private static PadGrader.LockCurve[] CombinePadLockCurves(
        IReadOnlyList<PadGrader.LockCurve> localLocks,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints)
    {
        if (localLocks.Count == 0 && persistentHardConstraints.Count == 0)
            return Array.Empty<PadGrader.LockCurve>();

        var combined = new List<PadGrader.LockCurve>(localLocks.Count + persistentHardConstraints.Count);
        combined.AddRange(localLocks);

        foreach (var constraint in persistentHardConstraints)
        {
            if (constraint.PointCount < 2 || constraint.IsClosed)
                continue;

            var xyVertices = new double[constraint.PointCount * 2];
            for (int i = 0; i < constraint.PointCount; i++)
            {
                xyVertices[i * 2] = constraint.Points[i * 3];
                xyVertices[i * 2 + 1] = constraint.Points[i * 3 + 1];
            }

            combined.Add(new PadGrader.LockCurve(xyVertices, constraint.PointCount));
        }

        return combined.ToArray();
    }

    private static RhinoMesh ApplyGradePath(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        GradePathModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        int modifierIndex,
        string stageKey,
        TerrainBuildMode mode)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade path.");
            return mesh;
        }

        if (modifier.Width <= 0)
        {
            build.Diagnostics.Add("Grade Path width must be positive.");
            return mesh;
        }

        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double curveTolerance = toleranceProfile.CurveChordTolerance;
        double gradePathTolerance = toleranceProfile.GradePathTolerance;
        ResolvedGradePathInputs resolvedInputs = ResolveGradePathInputs(snapshot, vertices, mesh.Vertices.Count, faces, mesh.Faces.Count, modifier, curveTolerance, gradePathTolerance);
        if (resolvedInputs.Paths.Length == 0)
        {
            build.Diagnostics.Add("Grade Path has no valid paths.");
            runtimeCache.GradingTopologyEntries[TerrainStageKey.CreateGradingTopology(stageKey, "Path")] = new GradingTopologyCacheEntry
            {
                GraderKind = "Path",
                Fingerprint = 0,
                OutputFingerprint = ComputeGradingTopologyOutputFingerprint("Path", vertices, mesh.Vertices.Count, faces, mesh.Faces.Count),
                Vertices = (double[])vertices.Clone(),
                VertexCount = mesh.Vertices.Count,
                Faces = (int[])faces.Clone(),
                FaceCount = mesh.Faces.Count,
                PatchSummaries = new List<GradingPatch>(),
                Diagnostics = new List<string>()
            };
            return mesh;
        }

        List<GradingPatch> patchSummaries = BuildPathPatchSummaries(resolvedInputs.Paths);
        List<string> dirtyStageKeys = runtimeCache.FindIntersectingGradingStageKeys(
            TerrainRuntimeCache.GetStagePrefix(mode),
            modifierIndex,
            patchSummaries);
        if (dirtyStageKeys.Count > 0)
        {
            runtimeCache.InvalidateStages(dirtyStageKeys);
            build.Diagnostics.Add(
                $"Grade Path invalidated {dirtyStageKeys.Count} overlapping downstream grading stage(s): {string.Join(", ", dirtyStageKeys.Select(TerrainStageKey.GetBase))}.");
        }

        if (build.PersistentHardConstraints.Count > 0 && resolvedInputs.Constraints.Length > 0)
        {
            var pathConstraintData = resolvedInputs.Constraints
                .Select(static constraint => new ConstraintConflictDiagnostics.PolylineData(constraint.Points, constraint.PointCount, constraint.IsClosed))
                .ToArray();
            var hardConstraintData = build.PersistentHardConstraints
                .Select(static constraint => new ConstraintConflictDiagnostics.PolylineData(constraint.Points, constraint.PointCount, constraint.IsClosed))
                .ToArray();
            var conflictSummary = ConstraintConflictDiagnostics.Analyze(pathConstraintData, hardConstraintData, gradePathTolerance);
            build.Diagnostics.Add(conflictSummary.CreateSummaryMessage());
            if (conflictSummary.CreateSampleMessage() is string sampleMessage)
                build.Diagnostics.Add(sampleMessage);
        }

        string topologyStageKey = TerrainStageKey.CreateGradingTopology(stageKey, "Path");
        var coreTimer = Stopwatch.StartNew();
        GradingResult? gradingResult = PathGrader.Grade(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            resolvedInputs.Paths,
            build.PersistentHardConstraints,
            out string? warning,
            gradePathTolerance);
        coreTimer.Stop();
        runtimeCache.CoreCaseRecorder?.RecordPath(
            modifier.Label,
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            resolvedInputs.Paths,
            build.PersistentHardConstraints,
            gradingResult != null,
            gradingResult?.VertexCount,
            gradingResult?.FaceCount,
            warning);

        if (gradingResult == null)
        {
            build.RecordTiming("Grade Path Core", coreTimer.Elapsed, "failed", StageTimingDiagnosticThresholdMs);
            build.Diagnostics.Add(warning ?? "Grade Path failed.");
            runtimeCache.GradingTopologyEntries[topologyStageKey] = BuildPathTopologyEntryFromMesh(
                mesh,
                patchSummaries,
                build.Diagnostics);
            return mesh;
        }

        build.RecordTiming(
            "Grade Path Core",
            coreTimer.Elapsed,
            DescribeTopologyCounts(mesh.Vertices.Count, mesh.Faces.Count, gradingResult.VertexCount, gradingResult.FaceCount),
            StageTimingDiagnosticThresholdMs);
        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);
        build.AddGradingDiagnostics(gradingResult);

        AddOutputPolylinesAsBreaklines(gradingResult.OutputPolylines, build);
        AddPersistentElevationConstraints(build, resolvedInputs.Constraints);
        runtimeCache.GradingTopologyEntries[topologyStageKey] = BuildPathTopologyEntry(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            gradingResult,
            patchSummaries,
            build.Diagnostics);
        return FinalizeGradingMesh(
            RhinoGeometryConversions.BuildMesh(gradingResult.Vertices, gradingResult.VertexCount, gradingResult.Faces, gradingResult.FaceCount),
            "Grade Path",
            build);
    }

    private static void AddOutputPolylinesAsBreaklines(
        IReadOnlyList<MoleHill.Core.Grading.OutputPolyline> polylines,
        TerrainBuildResult build)
    {
        foreach (var poly in polylines)
        {
            if (poly.VertexCount < 2)
                continue;
            build.PersistentHardConstraints.Add(new SurfaceRemesher.ConstraintPolyline(
                poly.Vertices,
                poly.VertexCount,
                poly.IsClosed,
                PreserveInputElevation: true));
        }
    }

    private static void AddPersistentHardConstraints(
        TerrainBuildResult build,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        List<SurfaceRemesher.ConstraintPolyline> preservedConstraints = CreatePreservedElevationConstraints(constraints);
        if (preservedConstraints.Count == 0)
            return;

        List<SurfaceRemesher.ConstraintPolyline> mergedConstraints = CombineConstraints(build.PersistentHardConstraints, preservedConstraints);
        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(mergedConstraints);
    }

    private static void AddPersistentElevationConstraints(
        TerrainBuildResult build,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        List<SurfaceRemesher.ConstraintPolyline> preservedConstraints = CreatePreservedElevationConstraints(constraints);
        if (preservedConstraints.Count == 0)
            return;

        List<SurfaceRemesher.ConstraintPolyline> mergedConstraints = CombineConstraints(build.PersistentElevationConstraints, preservedConstraints);
        build.PersistentElevationConstraints.Clear();
        build.PersistentElevationConstraints.AddRange(mergedConstraints);
    }

    private static List<SurfaceRemesher.ConstraintPolyline> CreatePreservedElevationConstraints(
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        var preservedConstraints = new List<SurfaceRemesher.ConstraintPolyline>(constraints.Count);
        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            if (constraint.PointCount < 2)
                continue;

            int pointValueCount = Math.Min(constraint.Points.Length, constraint.PointCount * 3);
            if (pointValueCount < constraint.PointCount * 3)
                continue;

            var points = new double[pointValueCount];
            Array.Copy(constraint.Points, points, pointValueCount);
            preservedConstraints.Add(new SurfaceRemesher.ConstraintPolyline(
                points,
                constraint.PointCount,
                constraint.IsClosed,
                PreserveInputElevation: true));
        }

        return preservedConstraints;
    }

    private static List<SurfaceRemesher.ConstraintPolyline> CreateInSituStairConstraints(
        IReadOnlyList<InSituStairReference> references)
    {
        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(references.Count);
        foreach (InSituStairReference reference in references)
        {
            SurfaceStripGrader.SurfaceDefinition surface = reference.SupportSurface;
            if (surface.BoundaryVertexCount < 3)
                continue;

            int pointValueCount = Math.Min(surface.BoundaryVertices.Length, surface.BoundaryVertexCount * 3);
            if (pointValueCount < surface.BoundaryVertexCount * 3)
                continue;

            var points = new double[pointValueCount];
            Array.Copy(surface.BoundaryVertices, points, pointValueCount);
            constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                points,
                surface.BoundaryVertexCount,
                IsClosed: true,
                PreserveInputElevation: true));
        }

        return constraints;
    }

    private static ResolvedGradePathInputs ResolveGradePathInputs(
        TerrainBuildSnapshot snapshot,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        GradePathModifierDefinition modifier,
        double curveTolerance,
        double gradePathTolerance)
    {
        var paths = new List<PathGrader.PathDefinition>();
        double requestedEdgeLength = TerrainBuildHeuristics.GetGradePathCurveSamplingLength(modifier.Width);
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Paths))
        {
            if (!RhinoSourceResolver.TryGetPolyline(
                    curve,
                    curveTolerance,
                    requireClosed: false,
                    requestedEdgeLength,
                    maxArea: 0.0,
                    out var polyline))
            {
                continue;
            }

            var pathXy = new double[polyline.Count * 2];
            var pathZ = new double[polyline.Count];
            for (int i = 0; i < polyline.Count; i++)
            {
                pathXy[i * 2] = polyline[i].X;
                pathXy[i * 2 + 1] = polyline[i].Y;
                pathZ[i] = polyline[i].Z;
            }

            paths.Add(new PathGrader.PathDefinition(pathXy, pathZ, polyline.Count, modifier.Width, modifier.SlopeAngle, modifier.MaxDistance));
        }

        var pathArray = paths.ToArray();
        var constraintSet = pathArray.Length == 0
            ? new PathGrader.ConstraintSet
            {
                Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0
            }
            : PathGrader.CreateConstraints(vertices, vertexCount, faces, faceCount, pathArray, gradePathTolerance);

        return new ResolvedGradePathInputs
        {
            Paths = pathArray,
            Constraints = constraintSet.Constraints,
            SuggestedEdgeLength = constraintSet.SuggestedEdgeLength
        };
    }

    private static RhinoMesh ApplyInSituStair(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        InSituStairModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode)
    {
        modifier.ComputedSurfaceCount = null;
        modifier.ComputedTreadDepthSummary = null;
        modifier.ComputedStepCountSummary = null;

        if (mode == TerrainBuildMode.Preview)
        {
            build.Diagnostics.Add("In-Situ Stair preview deferred to full rebuild.");
            return mesh.DuplicateMesh();
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for in-situ stair grading.");
            return mesh;
        }

        if (modifier.RiserHeight <= 0)
        {
            build.Diagnostics.Add("In-Situ Stair riser height must be positive.");
            return mesh;
        }

        var referenceResolveTimer = Stopwatch.StartNew();
        var referenceMeshes = TerrainBuildSnapshotResolver.ResolveMeshes(snapshot, modifier.ReferenceSurface);
        referenceResolveTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Reference Resolve",
            referenceResolveTimer.Elapsed,
            $"{referenceMeshes.Count:N0} reference meshes",
            StageTimingDiagnosticThresholdMs);
        if (referenceMeshes.Count == 0)
        {
            build.Diagnostics.Add("In-Situ Stair has no valid reference surface.");
            return mesh;
        }

        var referenceBuildTimer = Stopwatch.StartNew();
        if (!InSituStairReferenceBuilder.TryBuild(
                referenceMeshes,
                modifier.RiserHeight,
                modifier.SlopeAngle,
                modifier.MaxDistance,
                out var stairBuild,
                out errorMessage))
        {
            referenceBuildTimer.Stop();
            build.Diagnostics.Add(errorMessage ?? "In-Situ Stair could not interpret the reference surface.");
            return mesh;
        }
        referenceBuildTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Reference Build",
            referenceBuildTimer.Elapsed,
            $"{stairBuild!.SurfaceCount:N0} surfaces, {stairBuild.StepCountSummary} steps",
            StageTimingDiagnosticThresholdMs);

        double tolerance = GetToleranceProfile(snapshot, terrain).RemeshConstraintTolerance;

        modifier.ComputedSurfaceCount = stairBuild.SurfaceCount;
        modifier.ComputedTreadDepthSummary = stairBuild.TreadDepthSummary;
        modifier.ComputedStepCountSummary = stairBuild.StepCountSummary;

        double[] currentVertices = vertices;
        int currentVertexCount = mesh.Vertices.Count;
        int[] currentFaces = faces;
        int currentFaceCount = mesh.Faces.Count;
        var gradingWarnings = new List<string>();

        var gradingTimer = Stopwatch.StartNew();
        SurfaceStripGrader.SurfaceDefinition[] stairSurfaces = stairBuild.References
            .Select(static reference => reference.SupportSurface)
            .ToArray();
        var batchGradeTimer = Stopwatch.StartNew();
        var batchResult = SurfaceStripGrader.Grade(
            currentVertices,
            currentVertexCount,
            currentFaces,
            currentFaceCount,
            stairSurfaces,
            build.PersistentHardConstraints,
            out var batchGradingWarning,
            out var batchProfile);
        batchGradeTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Surface Grade",
            batchGradeTimer.Elapsed,
            $"batched {stairSurfaces.Length:N0} surfaces: {currentVertexCount:N0} verts/{currentFaceCount:N0} faces -> {batchResult?.VertexCount ?? 0:N0} verts/{batchResult?.FaceCount ?? 0:N0} faces; {batchProfile.FormatSummary()}",
            StageTimingDiagnosticThresholdMs);

        if (batchResult == null)
        {
            build.Diagnostics.Add(batchGradingWarning ?? "In-Situ Stair grading failed.");
            return mesh;
        }

        currentVertices = batchResult.Vertices;
        currentVertexCount = batchResult.VertexCount;
        currentFaces = batchResult.Faces;
        currentFaceCount = batchResult.FaceCount;
        if (!string.IsNullOrWhiteSpace(batchGradingWarning))
            gradingWarnings.Add(batchGradingWarning);

        gradingTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Grade",
            gradingTimer.Elapsed,
            $"{stairBuild.References.Count:N0} surfaces -> {currentVertexCount:N0} verts, {currentFaceCount:N0} faces",
            StageTimingDiagnosticThresholdMs);

        var outputTimer = Stopwatch.StartNew();
        int outputCountBefore = build.AuxiliaryObjects.Count;
        double minTreadDepth = Math.Max(0.0, modifier.MinTreadDepth);
        double? lowestWarnedTreadDepth = null;
        int treadDepthWarningCount = 0;
        foreach (var stairReference in stairBuild.References)
        {
            bool warnTreadDepth = minTreadDepth > 0 && stairReference.TreadDepth < minTreadDepth;
            if (warnTreadDepth)
            {
                treadDepthWarningCount++;
                lowestWarnedTreadDepth = lowestWarnedTreadDepth.HasValue
                    ? Math.Min(lowestWarnedTreadDepth.Value, stairReference.TreadDepth)
                    : stairReference.TreadDepth;
            }

            foreach (var stairBrep in stairReference.StairBreps)
            {
                build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                {
                    Geometry = stairBrep,
                    Name = "Stair",
                    LayerPath = terrain.AuxiliaryLayerPath,
                    ColorArgb = warnTreadDepth ? InSituStairTreadDepthWarningColorArgb : null
                });
            }

            if (modifier.ShowTreadLabels)
            {
                build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                {
                    Geometry = new TextDot($"Tread {stairReference.TreadDepth:G4}", stairReference.TreadDepthLabelPoint),
                    Name = "Stair Tread Depth",
                    LayerPath = terrain.AuxiliaryLayerPath
                });
            }
        }
        outputTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Outputs",
            outputTimer.Elapsed,
            $"{build.AuxiliaryObjects.Count - outputCountBefore:N0} auxiliary outputs",
            StageTimingDiagnosticThresholdMs);

        build.Diagnostics.Add(stairBuild.StatusSummary);
        if (treadDepthWarningCount > 0 && lowestWarnedTreadDepth.HasValue)
        {
            string surfaceText = treadDepthWarningCount == 1 ? "surface" : "surfaces";
            build.Diagnostics.Add(
                $"In-Situ Stair tread depth warning: {treadDepthWarningCount:N0} {surfaceText} below minimum {minTreadDepth:G4}; lowest tread {lowestWarnedTreadDepth.Value:G4}. Stair solids shown bright red.");
        }
        foreach (var warning in stairBuild.Warnings)
            build.Diagnostics.Add(warning);
        foreach (var gradingWarning in gradingWarnings)
            build.Diagnostics.Add(gradingWarning);

        var persistTimer = Stopwatch.StartNew();
        AddPersistentHardConstraints(build, CreateInSituStairConstraints(stairBuild.References));
        persistTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Persist Constraints",
            persistTimer.Elapsed,
            $"{build.PersistentHardConstraints.Count:N0} hard constraints",
            StageTimingDiagnosticThresholdMs);

        var meshBuildTimer = Stopwatch.StartNew();
        RhinoMesh stairMesh = RhinoGeometryConversions.BuildMesh(currentVertices, currentVertexCount, currentFaces, currentFaceCount);
        meshBuildTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Mesh Build",
            meshBuildTimer.Elapsed,
            $"{stairMesh.Vertices.Count:N0} verts, {stairMesh.Faces.Count:N0} faces",
            StageTimingDiagnosticThresholdMs);

        var cleanupTimer = Stopwatch.StartNew();
        RhinoMesh cleanedStairMesh = CleanTinyFaces(stairMesh, tolerance, "In-Situ Stair", build);
        cleanupTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Tiny Cleanup",
            cleanupTimer.Elapsed,
            ReferenceEquals(cleanedStairMesh, stairMesh)
                ? "unchanged"
                : $"{cleanedStairMesh.Vertices.Count:N0} verts, {cleanedStairMesh.Faces.Count:N0} faces",
            StageTimingDiagnosticThresholdMs);

        return cleanedStairMesh;
    }

    private static List<ZoneBoundaryEntry> ResolveZoneBoundaries(TerrainBuildSnapshot snapshot, CollageZoneDefinition zone, int zoneOrder, double tolerance)
    {
        var result = new List<ZoneBoundaryEntry>();
        int sourceOrder = 0;

        string? inputLayerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        foreach (var obj in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, zone.Boundaries))
        {
            switch (obj.Geometry)
            {
                case Curve curve:
                    if (TryCreateAreaBoundary(curve, tolerance, out var curveBoundary))
                    {
                        result.Add(new ZoneBoundaryEntry
                        {
                            Zone = zone,
                            Boundary = curveBoundary,
                            ZoneOrder = zoneOrder,
                            SourceOrder = sourceOrder++,
                            PriorityZ = GetCurvePriorityZ(curve),
                            InputLayerPath = inputLayerPath ?? obj.LayerPath
                        });
                    }

                    break;
                case Brep brep:
                    AppendPlanarBrepBoundaries(result, zone, zoneOrder, ref sourceOrder, brep, tolerance, inputLayerPath ?? obj.LayerPath);
                    break;
                case Extrusion extrusion:
                    var extrusionBrep = extrusion.ToBrep();
                    if (extrusionBrep != null)
                        AppendPlanarBrepBoundaries(result, zone, zoneOrder, ref sourceOrder, extrusionBrep, tolerance, inputLayerPath ?? obj.LayerPath);
                    break;
            }
        }

        return result;
    }

    private static int CompareZoneEntries(ZoneBoundaryEntry left, ZoneBoundaryEntry right)
    {
        bool leftUsesZ = left.Zone.UseInputElevationForPriority;
        bool rightUsesZ = right.Zone.UseInputElevationForPriority;

        if (leftUsesZ && rightUsesZ)
        {
            int zCompare = left.PriorityZ.CompareTo(right.PriorityZ);
            if (zCompare != 0)
                return zCompare;
        }
        else
        {
            int zoneCompare = left.ZoneOrder.CompareTo(right.ZoneOrder);
            if (zoneCompare != 0)
                return zoneCompare;
        }

        int sourceCompare = left.SourceOrder.CompareTo(right.SourceOrder);
        if (sourceCompare != 0)
            return sourceCompare;

        return left.ZoneOrder.CompareTo(right.ZoneOrder);
    }

    private static bool TryCreateAreaBoundary(Curve curve, double tolerance, out MeshAreaSplitter.AreaBoundary boundary)
    {
        boundary = null!;

        if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out var polyline))
            return false;

        int count = polyline.Count;
        if (polyline[0].DistanceTo(polyline[^1]) < tolerance)
            count--;

        if (count < 3)
            return false;

        var xyVerts = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            xyVerts[i * 2] = polyline[i].X;
            xyVerts[i * 2 + 1] = polyline[i].Y;
        }

        boundary = new MeshAreaSplitter.AreaBoundary(xyVerts, count);
        return true;
    }

    private static void AppendPlanarBrepBoundaries(
        List<ZoneBoundaryEntry> entries,
        CollageZoneDefinition zone,
        int zoneOrder,
        ref int sourceOrder,
        Brep brep,
        double tolerance,
        string? inputLayerPath)
    {
        var candidates = new List<(Curve curve, double priorityZ)>();

        foreach (var face in brep.Faces)
        {
            if (!face.TryGetPlane(out var plane, tolerance))
                continue;

            if (Math.Abs(plane.Normal.Z) < 0.5)
                continue;

            foreach (var loop in face.Loops)
            {
                if (loop.LoopType != BrepLoopType.Outer)
                    continue;

                var loopCurve = loop.To3dCurve();
                if (loopCurve == null)
                    continue;

                candidates.Add((loopCurve, GetCurvePriorityZ(loopCurve)));
            }
        }

        if (candidates.Count == 0)
            return;

        double topZ = candidates.Max(candidate => candidate.priorityZ);
        foreach (var (curve, priorityZ) in candidates.Where(candidate => Math.Abs(candidate.priorityZ - topZ) <= tolerance))
        {
            if (!TryCreateAreaBoundary(curve, tolerance, out var boundary))
                continue;

            entries.Add(new ZoneBoundaryEntry
            {
                Zone = zone,
                Boundary = boundary,
                ZoneOrder = zoneOrder,
                SourceOrder = sourceOrder++,
                PriorityZ = priorityZ,
                InputLayerPath = inputLayerPath
            });
        }
    }

    internal static string? GetBakedLayerPath(string? inputLayerPath)
    {
        if (string.IsNullOrWhiteSpace(inputLayerPath))
            return null;

        var segments = inputLayerPath
            .Split(new[] { "::" }, StringSplitOptions.None)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        if (segments.Length == 0)
            return "MoleHill::Zones";

        return $"MoleHill::Zones::{string.Join("::", segments)}";
    }

    private static double GetCurvePriorityZ(Curve curve)
    {
        var bbox = curve.GetBoundingBox(true);
        return (bbox.Min.Z + bbox.Max.Z) * 0.5;
    }

    private static void BuildMarkers(TerrainBuildSnapshot snapshot, TerrainDefinition terrain, RhinoMesh mesh, TerrainBuildResult build, Func<bool>? shouldCancel)
    {
        int enabledMarkerCount = terrain.Markers.Count(marker => marker.IsEnabled);
        if (enabledMarkerCount == 0)
            return;

        int outputCountBefore = build.MarkerObjects.Count;
        var timer = Stopwatch.StartNew();
        mesh.Normals.ComputeNormals();

        foreach (var marker in terrain.Markers.Where(marker => marker.IsEnabled))
        {
            ThrowIfCancellationRequested(shouldCancel);
            var samplePoints = TerrainBuildSnapshotResolver.ResolveMarkerSamplePoints(snapshot, marker.Sources);
            for (int sampleIndex = 0; sampleIndex < samplePoints.Count; sampleIndex++)
            {
                if ((sampleIndex & 31) == 0)
                    ThrowIfCancellationRequested(shouldCancel);

                var samplePoint = samplePoints[sampleIndex];
                var meshPoint = mesh.ClosestMeshPoint(samplePoint, 0.0);
                if (meshPoint == null)
                    continue;

                Point3d worldPoint = mesh.PointAt(meshPoint);
                string text;

                switch (marker)
                {
                    case ElevationMarkerDefinition elevation:
                        text = worldPoint.Z.ToString(elevation.Format);
                        break;
                    case SlopeMarkerDefinition slope:
                        var normal = mesh.NormalAt(meshPoint);
                        double slopeRadians = Math.Atan2(Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y), Math.Abs(normal.Z));
                        double value = slope.AsPercent
                            ? Math.Tan(slopeRadians) * 100.0
                            : slopeRadians * 180.0 / Math.PI;
                        text = value.ToString(slope.Format) + (slope.AsPercent ? "%" : "deg");
                        break;
                    default:
                        continue;
                }

                if (marker.UseBlockInstance)
                {
                    build.MarkerObjects.Add(new GeneratedRhinoObject
                    {
                        Name = marker.Name,
                        InstanceDefinitionName = GetMarkerBlockName(marker),
                        MarkerBlockTemplate = GetMarkerBlockTemplate(marker),
                        InstanceTransform = Transform.Translation(worldPoint - Point3d.Origin)
                            * Transform.Scale(Point3d.Origin, Math.Max(marker.BlockScale, 0.01)),
                        ColorArgb = marker.ColorArgb
                    });
                }

                if (!marker.ShowValueLabel)
                    continue;

                build.MarkerObjects.Add(new GeneratedRhinoObject
                {
                    Geometry = new TextDot(text, worldPoint),
                    Name = marker.Name,
                    ColorArgb = marker.ColorArgb
                });
            }
        }

        timer.Stop();
        int addedOutputs = build.MarkerObjects.Count - outputCountBefore;
        build.RecordTiming(
            "Markers",
            timer.Elapsed,
            $"{enabledMarkerCount:N0} enabled markers produced {addedOutputs:N0} outputs on {mesh.Faces.Count:N0} faces",
            StageTimingDiagnosticThresholdMs);
    }

    private static void BuildObjectPlacements(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        int enabledObjectCount = terrain.Objects.Count(item => item.IsEnabled);
        if (enabledObjectCount == 0)
            return;

        var timer = Stopwatch.StartNew();
        mesh.Normals.ComputeNormals();

        var resolvedEntries = terrain.Objects
            .Where(item => item.IsEnabled)
            .Select(item => (Definition: item, Objects: TerrainBuildSnapshotResolver.ResolveObjects(snapshot, item.Sources)))
            .ToList();

        var owners = new Dictionary<Guid, Guid>();
        var overlappingObjectIds = new HashSet<Guid>();
        foreach (var entry in resolvedEntries)
        {
            foreach (var obj in entry.Objects)
            {
                if (!owners.TryAdd(obj.ObjectId, entry.Definition.Id))
                    overlappingObjectIds.Add(obj.ObjectId);
            }
        }

        foreach (var objectId in overlappingObjectIds.OrderBy(id => id))
            build.Diagnostics.Add($"Objects skipped {FormatObjectRef(objectId)} because it is matched by multiple object definitions.");

        int placedCount = 0;
        foreach (var entry in resolvedEntries)
        {
            ThrowIfCancellationRequested(shouldCancel);

            var stateByObjectId = entry.Definition.PlacementStates
                .Where(state => state.ObjectId != Guid.Empty)
                .GroupBy(state => state.ObjectId)
                .ToDictionary(group => group.Key, group => group.Last());

            var placementGroup = new TerrainObjectPlacementGroup
            {
                DefinitionId = entry.Definition.Id
            };

            foreach (var obj in entry.Objects)
            {
                ThrowIfCancellationRequested(shouldCancel);

                if (overlappingObjectIds.Contains(obj.ObjectId))
                    continue;

                if (!TryBuildObjectPlacement(snapshot, entry.Definition, obj, stateByObjectId, mesh, out var placement, out string? diagnostic))
                {
                    if (!string.IsNullOrWhiteSpace(diagnostic))
                        build.Diagnostics.Add(diagnostic);
                    continue;
                }

                placementGroup.Placements.Add(placement);
            }

            if (placementGroup.Placements.Count == 0)
                continue;

            placedCount += placementGroup.Placements.Count;
            build.ObjectPlacements.Add(placementGroup);
        }

        timer.Stop();
        build.RecordTiming(
            "Objects",
            timer.Elapsed,
            $"{enabledObjectCount:N0} enabled definitions produced {placedCount:N0} object placements",
            StageTimingDiagnosticThresholdMs);
    }

    private static bool TryBuildObjectPlacement(
        TerrainBuildSnapshot snapshot,
        TerrainObjectDefinition definition,
        ResolvedSourceObject obj,
        IReadOnlyDictionary<Guid, TerrainObjectPlacementState> stateByObjectId,
        RhinoMesh mesh,
        out TerrainObjectPlacement placement,
        out string? diagnostic)
    {
        placement = new TerrainObjectPlacement();
        diagnostic = null;

        GeometryBase baselineGeometry = obj.Geometry.Duplicate();
        Transform previousTransform = stateByObjectId.TryGetValue(obj.ObjectId, out var existingState)
            ? existingState.GetLastAppliedTransform()
            : Transform.Identity;
        if (!TryGetInverse(previousTransform, out Transform inversePrevious))
        {
            diagnostic = $"Objects skipped {FormatObjectRef(obj.ObjectId)} because its stored placement transform could not be inverted.";
            return false;
        }

        if (!TryRemoveAppliedTransform(baselineGeometry, previousTransform))
        {
            diagnostic = $"Objects skipped {FormatObjectRef(obj.ObjectId)} because its stored placement transform could not be inverted.";
            return false;
        }

        BoundingBox baselineBoundingBox = baselineGeometry.GetBoundingBox(true);
        if (!baselineBoundingBox.IsValid && obj.WorldBoundingBox.IsValid)
            baselineBoundingBox = TransformBoundingBox(obj.WorldBoundingBox, inversePrevious);

        Transform appliedTransform;
        switch (definition)
        {
            case LowestPointObjectDefinition:
                if (!TryCreateLowestPointPlacement(snapshot, definition, obj.ObjectId, baselineGeometry, baselineBoundingBox, mesh, out appliedTransform, out diagnostic))
                    return false;
                break;
            case SurfaceOrientedObjectDefinition:
                if (!TryCreateSurfaceOrientedPlacement(snapshot, definition, obj.ObjectId, obj, baselineGeometry, previousTransform, mesh, out appliedTransform, out diagnostic))
                    return false;
                break;
            default:
                diagnostic = $"Objects skipped {FormatObjectRef(obj.ObjectId)} because its object definition type is unsupported.";
                return false;
        }

        placement = new TerrainObjectPlacement
        {
            ObjectId = obj.ObjectId,
            AppliedTransform = appliedTransform
        };
        diagnostic = null;
        return true;
    }

    private static bool TryCreateLowestPointPlacement(
        TerrainBuildSnapshot snapshot,
        TerrainObjectDefinition definition,
        Guid objectId,
        GeometryBase geometry,
        BoundingBox fallbackBoundingBox,
        RhinoMesh mesh,
        out Transform appliedTransform,
        out string? diagnostic)
    {
        appliedTransform = Transform.Identity;
        diagnostic = null;

        if (!TryGetLowestPoint(geometry, fallbackBoundingBox, out var lowestPoint))
        {
            diagnostic = $"Objects skipped {FormatObjectRef(objectId)} because its lowest point could not be resolved.";
            return false;
        }

        if (!TryResolveTerrainPoint(snapshot, mesh, lowestPoint, out Point3d terrainPoint, out _, out diagnostic))
            return false;

        Point3d targetPoint = terrainPoint + (Vector3d.ZAxis * definition.ZOffset);
        Transform basePlacement = Transform.Translation(targetPoint - lowestPoint);
        Transform randomLocal = CreateRandomPlacementTransform(definition, objectId, lowestPoint, Vector3d.ZAxis);
        appliedTransform = basePlacement * randomLocal;
        return true;
    }

    private static bool TryCreateSurfaceOrientedPlacement(
        TerrainBuildSnapshot snapshot,
        TerrainObjectDefinition definition,
        Guid objectId,
        ResolvedSourceObject obj,
        GeometryBase geometry,
        Transform previousTransform,
        RhinoMesh mesh,
        out Transform appliedTransform,
        out string? diagnostic)
    {
        appliedTransform = Transform.Identity;
        diagnostic = null;

        if (!TryCreateSurfaceSourcePlane(obj, geometry, previousTransform, objectId, out Plane sourcePlane, out Transform preAlignTransform, out diagnostic))
            return false;

        if (!TryResolveTerrainPoint(snapshot, mesh, sourcePlane.Origin, out Point3d terrainPoint, out Vector3d terrainNormal, out diagnostic))
            return false;

        if (!TryCreateTerrainFrame(terrainPoint, terrainNormal, sourcePlane.XAxis, out Plane targetPlane))
        {
            diagnostic = "Objects skipped a source object because a stable terrain frame could not be computed.";
            return false;
        }

        if (Math.Abs(definition.ZOffset) > 1e-9)
            targetPlane.Origin += targetPlane.Normal * definition.ZOffset;

        Transform basePlacement = Transform.PlaneToPlane(sourcePlane, targetPlane);
        Transform randomLocal = CreateRandomPlacementTransform(definition, objectId, sourcePlane.Origin, sourcePlane.Normal);
        appliedTransform = basePlacement * randomLocal * preAlignTransform;
        return true;
    }

    private static bool TryCreateSurfaceSourcePlane(
        ResolvedSourceObject obj,
        GeometryBase geometry,
        Transform previousTransform,
        Guid objectId,
        out Plane sourcePlane,
        out Transform preAlignTransform,
        out string? diagnostic)
    {
        sourcePlane = Plane.Unset;
        preAlignTransform = Transform.Identity;
        diagnostic = null;
        if (!TryCreateObjectPosePlane(obj, geometry, previousTransform, out Plane posePlane))
        {
            diagnostic = "Objects skipped a source object because an upright placement frame could not be resolved.";
            return false;
        }

        if (!TryCreateUprightPosePlane(posePlane, out Plane uprightPosePlane))
        {
            diagnostic = "Objects skipped a source object because its plan rotation could not be resolved.";
            return false;
        }

        preAlignTransform = Transform.PlaneToPlane(posePlane, uprightPosePlane);

        BoundingBox localBounds = BoundingBox.Empty;
        if (obj.HasSourceTransform && obj.LocalBoundingBox.IsValid)
        {
            localBounds = obj.LocalBoundingBox;
        }
        else
        {
            GeometryBase uprightGeometry = geometry.Duplicate();
            if (TryApplyTransform(uprightGeometry, preAlignTransform))
                localBounds = uprightGeometry.GetBoundingBox(uprightPosePlane);
            if (!localBounds.IsValid && obj.WorldBoundingBox.IsValid)
                localBounds = TransformBoundingBox(obj.WorldBoundingBox, preAlignTransform);
        }
        if (!localBounds.IsValid)
        {
            diagnostic = $"Objects skipped {FormatObjectRef(objectId)} because its bounding box is invalid.";
            return false;
        }

        double centerX = (localBounds.Min.X + localBounds.Max.X) * 0.5;
        double centerY = (localBounds.Min.Y + localBounds.Max.Y) * 0.5;
        double bottomZ = localBounds.Min.Z;
        Point3d sourceOrigin = uprightPosePlane.Origin
                             + (uprightPosePlane.XAxis * centerX)
                             + (uprightPosePlane.YAxis * centerY)
                             + (uprightPosePlane.ZAxis * bottomZ);
        sourcePlane = new Plane(sourceOrigin, uprightPosePlane.XAxis, uprightPosePlane.YAxis);
        return sourcePlane.IsValid;
    }

    private static bool TryCreateObjectPosePlane(
        ResolvedSourceObject obj,
        GeometryBase geometry,
        Transform previousTransform,
        out Plane plane)
    {
        plane = Plane.Unset;

        if (obj.HasSourceTransform)
        {
            Transform sourceTransform = obj.SourceTransform;
            if (!IsIdentityTransform(previousTransform))
            {
                if (!TryGetInverse(previousTransform, out Transform inversePrevious))
                    return false;

                sourceTransform = inversePrevious * sourceTransform;
            }

            if (TryCreatePosePlaneFromTransform(sourceTransform, out plane))
                return true;
        }

        if (TryCreatePosePlaneFromGeometry(geometry, out plane))
            return true;

        BoundingBox bbox = geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
            return false;

        plane = new Plane(bbox.Center, Vector3d.XAxis, Vector3d.YAxis);
        return true;
    }

    private static bool TryCreatePosePlaneFromTransform(Transform transform, out Plane plane)
    {
        plane = Plane.Unset;

        Point3d origin = Point3d.Origin;
        origin.Transform(transform);

        Vector3d xAxis = Vector3d.XAxis;
        xAxis.Transform(transform);
        Vector3d yAxis = Vector3d.YAxis;
        yAxis.Transform(transform);
        if (!xAxis.Unitize() || !yAxis.Unitize())
            return false;

        plane = new Plane(origin, xAxis, yAxis);
        return plane.IsValid;
    }

    private static bool TryCreateUprightPosePlane(Plane posePlane, out Plane uprightPlane)
    {
        uprightPlane = Plane.Unset;

        Vector3d xAxis = ProjectToWorldHorizontal(posePlane.XAxis);
        if (!xAxis.Unitize())
        {
            xAxis = ProjectToWorldHorizontal(posePlane.YAxis);
            if (!xAxis.Unitize())
                return false;
        }

        Vector3d yAxis = Vector3d.CrossProduct(Vector3d.ZAxis, xAxis);
        if (!yAxis.Unitize())
            return false;

        uprightPlane = new Plane(posePlane.Origin, xAxis, yAxis);
        return uprightPlane.IsValid;
    }

    private static bool TryCreatePosePlaneFromGeometry(GeometryBase geometry, out Plane plane)
    {
        plane = Plane.Unset;
        if (!TryGetPrincipalAxes(geometry, out Point3d centroid, out Vector3d axisA, out Vector3d axisB, out Vector3d axisC))
            return false;

        Vector3d[] axes = [axisA, axisB, axisC];
        int upIndex = 0;
        double bestUpAlignment = double.MinValue;
        for (int index = 0; index < axes.Length; index++)
        {
            if (!axes[index].Unitize())
                continue;

            double alignment = Math.Abs(Vector3d.Multiply(axes[index], Vector3d.ZAxis));
            if (alignment > bestUpAlignment)
            {
                bestUpAlignment = alignment;
                upIndex = index;
            }
        }

        Vector3d upAxis = axes[upIndex];
        if (!upAxis.Unitize())
            return false;
        if (Vector3d.Multiply(upAxis, Vector3d.ZAxis) < 0.0)
            upAxis = -upAxis;

        int[] horizontalIndices = Enumerable.Range(0, axes.Length)
            .Where(index => index != upIndex)
            .ToArray();
        if (horizontalIndices.Length == 0)
            return false;

        int xIndex = horizontalIndices
            .OrderByDescending(index => ProjectToWorldHorizontal(axes[index]).Length)
            .First();
        Vector3d xAxis = axes[xIndex] - (upAxis * Vector3d.Multiply(axes[xIndex], upAxis));
        if (!xAxis.Unitize())
        {
            xAxis = Vector3d.XAxis - (upAxis * Vector3d.Multiply(Vector3d.XAxis, upAxis));
            if (!xAxis.Unitize())
            {
                xAxis = Vector3d.YAxis - (upAxis * Vector3d.Multiply(Vector3d.YAxis, upAxis));
                if (!xAxis.Unitize())
                    return false;
            }
        }

        Vector3d yAxis = Vector3d.CrossProduct(upAxis, xAxis);
        if (!yAxis.Unitize())
            return false;

        plane = new Plane(centroid, xAxis, yAxis);
        return plane.IsValid;
    }

    private static bool TryGetPrincipalAxes(
        GeometryBase geometry,
        out Point3d centroid,
        out Vector3d axisA,
        out Vector3d axisB,
        out Vector3d axisC)
    {
        centroid = Point3d.Unset;
        axisA = Vector3d.Unset;
        axisB = Vector3d.Unset;
        axisC = Vector3d.Unset;

        switch (geometry)
        {
            case Mesh mesh:
                if (TryGetMeshPrincipalAxes(mesh, out centroid, out axisA, out axisB, out axisC))
                    return true;
                break;
            case Brep brep:
                if (TryGetBrepPrincipalAxes(brep, out centroid, out axisA, out axisB, out axisC))
                    return true;
                break;
            case Extrusion extrusion:
                var extrusionBrep = extrusion.ToBrep();
                if (extrusionBrep != null &&
                    TryGetBrepPrincipalAxes(extrusionBrep, out centroid, out axisA, out axisB, out axisC))
                    return true;
                break;
            case Curve curve:
                var length = LengthMassProperties.Compute(curve);
                if (length != null)
                {
                    using (length)
                    {
                        if (TryGetPrincipalAxesFromLength(length, out centroid, out axisA, out axisB, out axisC))
                            return true;
                    }
                }
                break;
        }

        return false;
    }

    private static bool TryGetMeshPrincipalAxes(
        Mesh mesh,
        out Point3d centroid,
        out Vector3d axisA,
        out Vector3d axisB,
        out Vector3d axisC)
    {
        centroid = Point3d.Unset;
        axisA = Vector3d.Unset;
        axisB = Vector3d.Unset;
        axisC = Vector3d.Unset;

        var volume = VolumeMassProperties.Compute(mesh);
        if (volume != null)
        {
            using (volume)
            {
                if (TryGetPrincipalAxesFromVolume(volume, out centroid, out axisA, out axisB, out axisC))
                    return true;
            }
        }

        var area = AreaMassProperties.Compute(mesh);
        if (area != null)
        {
            using (area)
            {
                if (TryGetPrincipalAxesFromArea(area, out centroid, out axisA, out axisB, out axisC))
                    return true;
            }
        }

        return false;
    }

    private static bool TryGetBrepPrincipalAxes(
        Brep brep,
        out Point3d centroid,
        out Vector3d axisA,
        out Vector3d axisB,
        out Vector3d axisC)
    {
        centroid = Point3d.Unset;
        axisA = Vector3d.Unset;
        axisB = Vector3d.Unset;
        axisC = Vector3d.Unset;

        var volume = VolumeMassProperties.Compute(brep);
        if (volume != null)
        {
            using (volume)
            {
                if (TryGetPrincipalAxesFromVolume(volume, out centroid, out axisA, out axisB, out axisC))
                    return true;
            }
        }

        var area = AreaMassProperties.Compute(brep);
        if (area != null)
        {
            using (area)
            {
                if (TryGetPrincipalAxesFromArea(area, out centroid, out axisA, out axisB, out axisC))
                    return true;
            }
        }

        return false;
    }

    private static bool TryGetPrincipalAxesFromVolume(
        VolumeMassProperties massProperties,
        out Point3d centroid,
        out Vector3d axisA,
        out Vector3d axisB,
        out Vector3d axisC)
    {
        centroid = massProperties.Centroid;
        return massProperties.WorldCoordinatesPrincipalMoments(
            out _,
            out axisA,
            out _,
            out axisB,
            out _,
            out axisC);
    }

    private static bool TryGetPrincipalAxesFromArea(
        AreaMassProperties massProperties,
        out Point3d centroid,
        out Vector3d axisA,
        out Vector3d axisB,
        out Vector3d axisC)
    {
        centroid = massProperties.Centroid;
        return massProperties.WorldCoordinatesPrincipalMoments(
            out _,
            out axisA,
            out _,
            out axisB,
            out _,
            out axisC);
    }

    private static bool TryGetPrincipalAxesFromLength(
        LengthMassProperties massProperties,
        out Point3d centroid,
        out Vector3d axisA,
        out Vector3d axisB,
        out Vector3d axisC)
    {
        centroid = massProperties.Centroid;
        return massProperties.WorldCoordinatesPrincipalMoments(
            out _,
            out axisA,
            out _,
            out axisB,
            out _,
            out axisC);
    }

    private static bool TryResolveTerrainPoint(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        Point3d samplePoint,
        out Point3d terrainPoint,
        out Vector3d terrainNormal,
        out string? diagnostic)
    {
        terrainPoint = Point3d.Unset;
        terrainNormal = Vector3d.Unset;
        diagnostic = null;

        BoundingBox meshBounds = mesh.GetBoundingBox(true);
        if (!meshBounds.IsValid)
        {
            diagnostic = "Objects skipped a source object because the terrain bounds are invalid.";
            return false;
        }

        double zMargin = Math.Max(snapshot.ModelAbsoluteTolerance * 10.0, ModelUnits.FromMeters(1.0, snapshot.ModelUnitSystem));
        double rayStartZ = Math.Max(samplePoint.Z, meshBounds.Max.Z) + zMargin;
        var ray = new Ray3d(new Point3d(samplePoint.X, samplePoint.Y, rayStartZ), -Vector3d.ZAxis);
        double rayDistance = Intersection.MeshRay(mesh, ray);
        if (rayDistance < 0.0)
        {
            diagnostic = "Objects skipped a source object because it is outside the terrain footprint.";
            return false;
        }

        terrainPoint = ray.PointAt(rayDistance);
        var meshPoint = mesh.ClosestMeshPoint(terrainPoint, Math.Max(snapshot.ModelAbsoluteTolerance * 4.0, ModelUnits.FromMeters(1e-4, snapshot.ModelUnitSystem)));
        if (meshPoint == null)
        {
            diagnostic = "Objects skipped a source object because no terrain sample point was found.";
            return false;
        }

        terrainNormal = mesh.NormalAt(meshPoint);
        if (!terrainNormal.Unitize())
        {
            diagnostic = "Objects skipped a source object because the terrain normal is invalid at the sample point.";
            return false;
        }

        return true;
    }

    private static bool TryCreateTerrainFrame(Point3d origin, Vector3d terrainNormal, Vector3d preferredXAxis, out Plane plane)
    {
        plane = Plane.Unset;
        if (!terrainNormal.Unitize())
            return false;

        Vector3d xAxis = preferredXAxis - (terrainNormal * Vector3d.Multiply(preferredXAxis, terrainNormal));
        if (!xAxis.Unitize())
        {
            xAxis = Vector3d.XAxis - (terrainNormal * Vector3d.Multiply(Vector3d.XAxis, terrainNormal));
            if (!xAxis.Unitize())
            {
                xAxis = Vector3d.YAxis - (terrainNormal * Vector3d.Multiply(Vector3d.YAxis, terrainNormal));
                if (!xAxis.Unitize())
                    return false;
            }
        }

        Vector3d yAxis = Vector3d.CrossProduct(terrainNormal, xAxis);
        if (!yAxis.Unitize())
            return false;

        plane = new Plane(origin, xAxis, yAxis);
        return plane.IsValid;
    }

    private static Transform CreateRandomPlacementTransform(
        TerrainObjectDefinition definition,
        Guid objectId,
        Point3d anchor,
        Vector3d axis)
    {
        Transform scaleTransform = Transform.Identity;
        double scale = SampleDeterministicRange(
            definition.Id,
            objectId,
            definition.RandomSeed,
            0,
            definition.RandomScaleMin,
            definition.RandomScaleMax,
            1.0);
        if (Math.Abs(scale - 1.0) > 1e-9)
            scaleTransform = Transform.Scale(anchor, scale);

        Transform rotationTransform = Transform.Identity;
        double rotationDegrees = SampleDeterministicRange(
            definition.Id,
            objectId,
            definition.RandomSeed,
            1,
            definition.RandomRotationMinDegrees,
            definition.RandomRotationMaxDegrees,
            0.0);
        if (Math.Abs(rotationDegrees) > 1e-9)
        {
            Vector3d rotationAxis = axis;
            if (!rotationAxis.Unitize())
                rotationAxis = Vector3d.ZAxis;

            rotationTransform = Transform.Rotation(RhinoMath.ToRadians(rotationDegrees), rotationAxis, anchor);
        }

        return rotationTransform * scaleTransform;
    }

    private static double SampleDeterministicRange(
        Guid definitionId,
        Guid objectId,
        int seed,
        int channel,
        double min,
        double max,
        double fallbackValue)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
            return fallbackValue;

        if (max < min)
            (min, max) = (max, min);

        if (Math.Abs(max - min) <= 1e-9)
            return min;

        double unit = SampleDeterministicUnit(definitionId, objectId, seed, channel);
        return min + ((max - min) * unit);
    }

    private static double SampleDeterministicUnit(Guid definitionId, Guid objectId, int seed, int channel)
    {
        byte[] buffer = new byte[40];
        definitionId.ToByteArray().CopyTo(buffer, 0);
        objectId.ToByteArray().CopyTo(buffer, 16);
        BitConverter.TryWriteBytes(buffer.AsSpan(32, 4), seed);
        BitConverter.TryWriteBytes(buffer.AsSpan(36, 4), channel);

        ulong hash = 14695981039346656037UL;
        foreach (byte value in buffer)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        const double divisor = 1UL << 53;
        ulong mantissa = hash >> 11;
        return mantissa / divisor;
    }

    private static BoundingBox TransformBoundingBox(BoundingBox bbox, Transform transform)
    {
        if (!bbox.IsValid)
            return BoundingBox.Empty;

        Point3d[] corners = bbox.GetCorners();
        for (int index = 0; index < corners.Length; index++)
            corners[index].Transform(transform);

        return new BoundingBox(corners);
    }

    private static Vector3d ProjectToWorldHorizontal(Vector3d axis)
    {
        return new Vector3d(axis.X, axis.Y, 0.0);
    }

    private static bool TryRemoveAppliedTransform(GeometryBase geometry, Transform appliedTransform)
    {
        if (IsIdentityTransform(appliedTransform))
            return true;

        if (!TryGetInverse(appliedTransform, out Transform inverse))
            return false;

        return TryApplyTransform(geometry, inverse);
    }

    private static bool TryApplyTransform(GeometryBase geometry, Transform transform)
    {
        if (IsIdentityTransform(transform))
            return true;

        return geometry.Transform(transform);
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

    private static bool TryGetLowestPoint(GeometryBase geometry, BoundingBox fallbackBoundingBox, out Point3d point)
    {
        point = Point3d.Unset;

        switch (geometry)
        {
            case Point rhinoPoint:
                point = rhinoPoint.Location;
                return true;
            case PointCloud pointCloud when pointCloud.Count > 0:
                point = Enumerable.Range(0, pointCloud.Count)
                    .Select(index => pointCloud[index].Location)
                    .OrderBy(candidate => candidate.Z)
                    .First();
                return true;
            case Curve curve:
                point = GetLowestCurvePoint(curve);
                return point.IsValid;
            case Mesh mesh when TryGetLowestMeshPoint(mesh, out point):
                return true;
            case Brep brep:
                return TryGetLowestPointFromMeshes(Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>(), out point);
            case Extrusion extrusion:
                var extrusionBrep = extrusion.ToBrep();
                if (extrusionBrep == null)
                    break;

                return TryGetLowestPointFromMeshes(Mesh.CreateFromBrep(extrusionBrep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>(), out point);
            case InstanceReferenceGeometry when fallbackBoundingBox.IsValid:
                point = new Point3d(
                    (fallbackBoundingBox.Min.X + fallbackBoundingBox.Max.X) * 0.5,
                    (fallbackBoundingBox.Min.Y + fallbackBoundingBox.Max.Y) * 0.5,
                    fallbackBoundingBox.Min.Z);
                return true;
        }

        BoundingBox bbox = geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
            bbox = fallbackBoundingBox;
        if (!bbox.IsValid)
            return false;

        point = new Point3d(
            (bbox.Min.X + bbox.Max.X) * 0.5,
            (bbox.Min.Y + bbox.Max.Y) * 0.5,
            bbox.Min.Z);
        return true;
    }

    private static Point3d GetLowestCurvePoint(Curve curve)
    {
        var candidates = new List<Point3d> { curve.PointAtStart, curve.PointAtEnd };
        var parameters = curve.DivideByCount(64, true);
        if (parameters != null)
        {
            foreach (double parameter in parameters)
                candidates.Add(curve.PointAt(parameter));
        }

        return candidates
            .Where(candidate => candidate.IsValid)
            .OrderBy(candidate => candidate.Z)
            .FirstOrDefault();
    }

    private static bool TryGetLowestPointFromMeshes(IEnumerable<Mesh> meshes, out Point3d point)
    {
        point = Point3d.Unset;
        bool found = false;
        foreach (var mesh in meshes)
        {
            if (!TryGetLowestMeshPoint(mesh, out Point3d candidate))
                continue;

            if (!found || candidate.Z < point.Z)
            {
                point = candidate;
                found = true;
            }
        }

        return found;
    }

    private static bool TryGetLowestMeshPoint(Mesh mesh, out Point3d point)
    {
        point = Point3d.Unset;
        if (mesh.Vertices.Count == 0)
            return false;

        var lowest = mesh.Vertices[0];
        for (int index = 1; index < mesh.Vertices.Count; index++)
        {
            var candidate = mesh.Vertices[index];
            if (candidate.Z < lowest.Z)
                lowest = candidate;
        }

        point = new Point3d(lowest.X, lowest.Y, lowest.Z);
        return true;
    }

    private static string FormatObjectRef(Guid objectId)
    {
        return objectId == Guid.Empty
            ? "object"
            : objectId.ToString("N")[..8];
    }

    private static List<TerrainAnalysisSummary> BuildAnalyses(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh fallbackBaseMesh,
        RhinoMesh currentMesh,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        var results = new List<TerrainAnalysisSummary>(terrain.Analyses.Count);
        ThrowIfCancellationRequested(shouldCancel);
        if (!RhinoGeometryConversions.TryExtractMeshData(currentMesh, out var currentVertices, out var currentFaces, out _))
            return results;

        GetElevationRange(currentVertices, currentMesh.Vertices.Count, out double elevMinZ, out double elevMaxZ);
        double surfaceArea = AreaMassProperties.Compute(currentMesh)?.Area ?? 0.0;

        foreach (var analysis in terrain.Analyses)
        {
            ThrowIfCancellationRequested(shouldCancel);

            TerrainAnalysisSummary? summary = analysis switch
            {
                EarthworkAnalysisDefinition earthwork => BuildEarthworkSummary(
                    snapshot,
                    fallbackBaseMesh,
                    currentMesh,
                    currentVertices,
                    currentFaces,
                    earthwork,
                    surfaceArea,
                    elevMinZ,
                    elevMaxZ,
                    shouldCancel),
                SlopeAnalysisDefinition slope => BuildSlopeSummary(
                    currentMesh,
                    currentVertices,
                    currentFaces,
                    slope,
                    surfaceArea),
                ElevationAnalysisDefinition => new TerrainAnalysisSummary
                {
                    AnalysisId = analysis.Id,
                    SurfaceArea = surfaceArea,
                    ElevationMinZ = elevMinZ,
                    ElevationMaxZ = elevMaxZ
                },
                CurveSlopeLabelAnalysisDefinition curveSlope => TerrainAnalysisAnnotationBuilder.BuildCurveSlopeSummary(
                    snapshot,
                    currentMesh,
                    curveSlope,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                CurveElevationLabelAnalysisDefinition curveElevation => TerrainAnalysisAnnotationBuilder.BuildCurveElevationSummary(
                    snapshot,
                    currentMesh,
                    curveElevation,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                ProjectedElevationLabelAnalysisDefinition projectedElevation => TerrainAnalysisAnnotationBuilder.BuildProjectedElevationSummary(
                    snapshot,
                    currentMesh,
                    projectedElevation,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                PointSlopeLabelAnalysisDefinition pointSlope => TerrainAnalysisAnnotationBuilder.BuildPointSlopeSummary(
                    snapshot,
                    currentMesh,
                    pointSlope,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                TerrainSectionAnalysisDefinition terrainSection => TerrainAnalysisAnnotationBuilder.BuildTerrainSectionSummary(
                    snapshot,
                    currentMesh,
                    terrainSection,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                CrossSectionStationAnalysisDefinition crossSection => TerrainAnalysisAnnotationBuilder.BuildCrossSectionStationSummary(
                    snapshot,
                    currentMesh,
                    crossSection,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                LongitudinalSectionAnalysisDefinition longitudinal => TerrainAnalysisAnnotationBuilder.BuildLongitudinalSectionSummary(
                    snapshot,
                    currentMesh,
                    longitudinal,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                CutFillAnalysisDefinition cutFill => BuildCutFillSummary(
                    snapshot,
                    fallbackBaseMesh,
                    currentMesh,
                    currentVertices,
                    currentFaces,
                    cutFill,
                    surfaceArea,
                    elevMinZ,
                    elevMaxZ,
                    shouldCancel),
                ContourAnalysisDefinition contour => BuildContourSummary(
                    terrain,
                    currentMesh,
                    contour,
                    elevMinZ,
                    elevMaxZ,
                    build),
                _ => null
            };

            if (summary != null)
                results.Add(summary);
        }

        return results;
    }

    private static TerrainAnalysisSummary BuildSlopeSummary(
        RhinoMesh currentMesh,
        double[] currentVertices,
        int[] currentFaces,
        SlopeAnalysisDefinition analysis,
        double surfaceArea)
    {
        double lowPercent = ConvertSlopeUnitToPercent(analysis.RangeLow, analysis.Unit);
        double highPercent = ConvertSlopeUnitToPercent(analysis.RangeHigh, analysis.Unit);
        var palette = SlopePreviewPaletteCatalog.Resolve(analysis.PalettePreset);
        var slope = SlopeAnalyzer.Analyze(
            currentVertices,
            currentMesh.Vertices.Count,
            currentFaces,
            currentMesh.Faces.Count,
            SlopeAnalyzer.SlopeUnit.Percent,
            Math.Max(0.0, lowPercent),
            Math.Max(0.0, highPercent),
            palette.Stops);

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SurfaceArea = surfaceArea,
            SlopeMinPercent = slope.Min,
            SlopeMaxPercent = slope.Max,
            SlopeAveragePercent = slope.Average,
            SlopeDisplayLowPercent = slope.ColorLow,
            SlopeDisplayHighPercent = slope.ColorHigh
        };
    }

    private static TerrainAnalysisSummary BuildEarthworkSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh fallbackBaseMesh,
        RhinoMesh currentMesh,
        double[] currentVertices,
        int[] currentFaces,
        EarthworkAnalysisDefinition analysis,
        double surfaceArea,
        double elevMinZ,
        double elevMaxZ,
        Func<bool>? shouldCancel)
    {
        var stats = ComputeReferenceComparisonStats(
            snapshot,
            fallbackBaseMesh,
            currentMesh,
            currentVertices,
            currentFaces,
            analysis.Reference,
            analysis.Boundary,
            shouldCancel);

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SurfaceArea = surfaceArea,
            ElevationMinZ = elevMinZ,
            ElevationMaxZ = elevMaxZ,
            CutVolume = stats.CutVolume,
            FillVolume = stats.FillVolume,
            NetVolume = stats.NetVolume,
            EarthworkIsEstimated = stats.IsEstimated
        };
    }

    private static TerrainAnalysisSummary BuildCutFillSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh fallbackBaseMesh,
        RhinoMesh currentMesh,
        double[] currentVertices,
        int[] currentFaces,
        CutFillAnalysisDefinition analysis,
        double surfaceArea,
        double elevMinZ,
        double elevMaxZ,
        Func<bool>? shouldCancel)
    {
        var stats = ComputeReferenceComparisonStats(
            snapshot,
            fallbackBaseMesh,
            currentMesh,
            currentVertices,
            currentFaces,
            analysis.Reference,
            analysis.Boundary,
            shouldCancel);

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SurfaceArea = surfaceArea,
            ElevationMinZ = elevMinZ,
            ElevationMaxZ = elevMaxZ,
            CutFillDisplayAbsMax = stats.CutFillDisplayAbsMax,
            CutVolume = stats.CutVolume,
            FillVolume = stats.FillVolume,
            NetVolume = stats.NetVolume,
            EarthworkIsEstimated = stats.IsEstimated
        };
    }

    private static TerrainAnalysisSummary BuildContourSummary(
        TerrainDefinition terrain,
        RhinoMesh currentMesh,
        ContourAnalysisDefinition analysis,
        double elevMinZ,
        double elevMaxZ,
        TerrainBuildResult build)
    {
        var (objects, summary) = BuildContourCore(currentMesh, analysis, elevMinZ, elevMaxZ,
            TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath));
        build.AuxiliaryObjects.AddRange(objects);
        return summary;
    }

    internal static (List<GeneratedRhinoObject> Objects, TerrainAnalysisSummary Summary) BuildContourObjects(
        RhinoMesh mesh,
        ContourAnalysisDefinition analysis)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out _, out _))
            return (new List<GeneratedRhinoObject>(), new TerrainAnalysisSummary { AnalysisId = analysis.Id });

        GetElevationRange(vertices, mesh.Vertices.Count, out double minZ, out double maxZ);
        return BuildContourCore(mesh, analysis, minZ, maxZ);
    }

    private static (List<GeneratedRhinoObject> Objects, TerrainAnalysisSummary Summary) BuildContourCore(
        RhinoMesh mesh,
        ContourAnalysisDefinition analysis,
        double elevMinZ,
        double elevMaxZ,
        string? fallbackLayerPath = null)
    {
        var objects = new List<GeneratedRhinoObject>();
        var levels = BuildContourLevels(elevMinZ, elevMaxZ, analysis.StartZ, Math.Max(analysis.Interval, 0.01));
        int contourCurveCount = 0;
        int contourLevelCount = 0;
        double firstLevel = 0.0;
        double lastLevel = 0.0;

        foreach (double level in levels)
        {
            var plane = new Plane(new Point3d(0.0, 0.0, level), Vector3d.ZAxis);
            Polyline[]? polylines = Intersection.MeshPlane(mesh, plane);
            if (polylines == null || polylines.Length == 0)
                continue;

            int levelCurveIndex = 0;
            bool levelHasCurves = false;
            foreach (var polyline in polylines)
            {
                if (polyline.Count < 2)
                    continue;

                levelHasCurves = true;
                contourCurveCount++;
                if (!analysis.IsEnabled)
                    continue;

                levelCurveIndex++;
                objects.Add(new GeneratedRhinoObject
                {
                    Geometry = new PolylineCurve(polyline),
                    Name = levelCurveIndex == 1
                        ? $"{analysis.Label} {level:G4}"
                        : $"{analysis.Label} {level:G4} ({levelCurveIndex})",
                    AnalysisId = analysis.Id,
                    ColorArgb = analysis.ColorArgb,
                    LayerPath = analysis.OutputLayerPath ?? fallbackLayerPath
                });
            }

            if (!levelHasCurves)
                continue;

            contourLevelCount++;
            if (contourLevelCount == 1)
                firstLevel = level;
            lastLevel = level;
        }

        var summary = new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            ContourCurveCount = contourCurveCount,
            ContourLevelCount = contourLevelCount,
            ContourFirstLevel = contourLevelCount > 0 ? firstLevel : 0.0,
            ContourLastLevel = contourLevelCount > 0 ? lastLevel : 0.0
        };
        return (objects, summary);
    }

    private static ReferenceComparisonStats ComputeReferenceComparisonStats(
        TerrainBuildSnapshot snapshot,
        RhinoMesh fallbackBaseMesh,
        RhinoMesh currentMesh,
        double[] currentVertices,
        int[] currentFaces,
        SourceReferenceSet referenceSet,
        SourceReferenceSet boundarySet,
        Func<bool>? shouldCancel)
    {
        RhinoMesh baseMesh = ResolveReferenceMesh(snapshot, referenceSet) ?? fallbackBaseMesh;
        var boundaries = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, boundarySet);
        EstimateEarthworks(baseMesh, currentMesh, boundaries, snapshot.ModelAbsoluteTolerance, out double cutVolume, out double fillVolume, shouldCancel);

        return new ReferenceComparisonStats(
            cutVolume,
            fillVolume,
            ComputeCutFillDisplayAbsMax(baseMesh, currentVertices, currentFaces, currentMesh.Faces.Count, boundaries, snapshot.ModelAbsoluteTolerance),
            !referenceSet.HasReferences);
    }

    private static double ComputeCutFillDisplayAbsMax(
        RhinoMesh baseMesh,
        double[] currentVertices,
        int[] currentFaces,
        int faceCount,
        IReadOnlyList<Curve> boundaries,
        double tolerance)
    {
        double cutFillAbsMax = 0.0;
        if (!RhinoGeometryConversions.TryExtractMeshData(baseMesh, out _, out _, out _))
            return cutFillAbsMax;

        for (int fi = 0; fi < faceCount; fi++)
        {
            int a = currentFaces[fi * 3];
            int b = currentFaces[fi * 3 + 1];
            int c = currentFaces[fi * 3 + 2];
            var centroid = new Point3d(
                (currentVertices[a * 3] + currentVertices[b * 3] + currentVertices[c * 3]) / 3.0,
                (currentVertices[a * 3 + 1] + currentVertices[b * 3 + 1] + currentVertices[c * 3 + 1]) / 3.0,
                (currentVertices[a * 3 + 2] + currentVertices[b * 3 + 2] + currentVertices[c * 3 + 2]) / 3.0);
            if (!IsInsideBoundaries(centroid, boundaries, tolerance))
                continue;

            var mp = baseMesh.ClosestMeshPoint(centroid, 0.0);
            if (mp == null)
                continue;

            double delta = Math.Abs(centroid.Z - baseMesh.PointAt(mp).Z);
            if (delta > cutFillAbsMax)
                cutFillAbsMax = delta;
        }

        return cutFillAbsMax;
    }

    private static void GetElevationRange(double[] vertices, int vertexCount, out double minZ, out double maxZ)
    {
        minZ = double.MaxValue;
        maxZ = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double z = vertices[i * 3 + 2];
            if (z < minZ)
                minZ = z;
            if (z > maxZ)
                maxZ = z;
        }

        if (minZ == double.MaxValue)
            minZ = 0.0;
        if (maxZ == double.MinValue)
            maxZ = 0.0;
    }

    private static List<double> BuildContourLevels(double minZ, double maxZ, double startZ, double interval)
    {
        var levels = new List<double>();
        if (interval <= 1e-9 || maxZ < minZ)
            return levels;

        long firstIndex = (long)Math.Ceiling(((minZ - startZ) / interval) - 1e-9);
        long lastIndex = (long)Math.Floor(((maxZ - startZ) / interval) + 1e-9);
        if (lastIndex < firstIndex)
            return levels;

        for (long index = firstIndex; index <= lastIndex; index++)
            levels.Add(startZ + (index * interval));

        return levels;
    }

    private static double ConvertSlopeUnitToPercent(double slopeValue, SlopeAnalyzer.SlopeUnit unit)
    {
        return SlopeAnalyzer.ConvertUnitToRatio(slopeValue, unit) * 100.0;
    }

    private static RhinoMesh BuildSlopePreviewMesh(
        double[] vertices,
        int[] faces,
        int faceCount,
        SlopeAnalyzer.SlopeResult slope)
    {
        var coloredMesh = new RhinoMesh();
        coloredMesh.Vertices.Capacity = faceCount * 3;
        coloredMesh.Faces.Capacity = faceCount;
        coloredMesh.VertexColors.Capacity = faceCount * 3;

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int i0 = faces[faceIndex * 3];
            int i1 = faces[faceIndex * 3 + 1];
            int i2 = faces[faceIndex * 3 + 2];
            int vertexIndex = coloredMesh.Vertices.Count;

            coloredMesh.Vertices.Add(vertices[i0 * 3], vertices[i0 * 3 + 1], vertices[i0 * 3 + 2]);
            coloredMesh.Vertices.Add(vertices[i1 * 3], vertices[i1 * 3 + 1], vertices[i1 * 3 + 2]);
            coloredMesh.Vertices.Add(vertices[i2 * 3], vertices[i2 * 3 + 1], vertices[i2 * 3 + 2]);
            coloredMesh.Faces.AddFace(vertexIndex, vertexIndex + 1, vertexIndex + 2);

            var color = System.Drawing.Color.FromArgb(
                slope.FaceColors[faceIndex * 3],
                slope.FaceColors[faceIndex * 3 + 1],
                slope.FaceColors[faceIndex * 3 + 2]);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
        }

        coloredMesh.Normals.ComputeNormals();
        coloredMesh.UnifyNormals();
        coloredMesh.Compact();
        return coloredMesh;
    }

    private static void EstimateEarthworks(RhinoMesh baseMesh, RhinoMesh currentMesh, IReadOnlyList<Curve> boundaries, double tolerance, out double cutVolume, out double fillVolume, Func<bool>? shouldCancel)
    {
        cutVolume = 0.0;
        fillVolume = 0.0;

        if (!RhinoGeometryConversions.TryExtractMeshData(currentMesh, out var currentVertices, out var currentFaces, out _))
            return;

        for (int faceIndex = 0; faceIndex < currentMesh.Faces.Count; faceIndex++)
        {
            if ((faceIndex & 127) == 0)
                ThrowIfCancellationRequested(shouldCancel);

            int a = currentFaces[faceIndex * 3];
            int b = currentFaces[faceIndex * 3 + 1];
            int c = currentFaces[faceIndex * 3 + 2];

            var pa = new Point3d(currentVertices[a * 3], currentVertices[a * 3 + 1], currentVertices[a * 3 + 2]);
            var pb = new Point3d(currentVertices[b * 3], currentVertices[b * 3 + 1], currentVertices[b * 3 + 2]);
            var pc = new Point3d(currentVertices[c * 3], currentVertices[c * 3 + 1], currentVertices[c * 3 + 2]);

            var centroid = new Point3d(
                (pa.X + pb.X + pc.X) / 3.0,
                (pa.Y + pb.Y + pc.Y) / 3.0,
                (pa.Z + pb.Z + pc.Z) / 3.0);

            if (!IsInsideBoundaries(centroid, boundaries, tolerance))
                continue;

            var basePoint = baseMesh.ClosestMeshPoint(centroid, 0.0);
            if (basePoint == null)
                continue;

            double baseZ = baseMesh.PointAt(basePoint).Z;
            double deltaZ = centroid.Z - baseZ;
            double projectedArea = Math.Abs(
                (pb.X - pa.X) * (pc.Y - pa.Y) -
                (pb.Y - pa.Y) * (pc.X - pa.X)) * 0.5;

            double volume = projectedArea * deltaZ;
            if (volume >= 0)
                fillVolume += volume;
            else
                cutVolume += -volume;
        }
    }

    private static RhinoMesh? ResolveReferenceMesh(TerrainBuildSnapshot snapshot, SourceReferenceSet referenceSet)
    {
        var meshes = TerrainBuildSnapshotResolver.ResolveMeshes(snapshot, referenceSet);
        if (meshes.Count == 0)
            return null;

        if (meshes.Count == 1)
            return meshes[0];

        var combined = new RhinoMesh();
        foreach (var mesh in meshes)
            combined.Append(mesh);

        RhinoGeometryConversions.NormalizeMeshInPlace(combined);
        return combined;
    }

    private static void ThrowIfCancellationRequested(Func<bool>? shouldCancel)
    {
        if (shouldCancel?.Invoke() == true)
            throw new OperationCanceledException("Terrain rebuild cancelled.");
    }

    private static bool IsInsideBoundaries(Point3d point, IReadOnlyList<Curve> boundaries, double tolerance)
    {
        if (boundaries.Count == 0)
            return true;

        foreach (var curve in boundaries)
        {
            var containment = curve.Contains(new Point3d(point.X, point.Y, curve.PointAtStart.Z), Plane.WorldXY, tolerance);
            if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                return true;
        }

        return false;
    }

    private static bool IsWallStripUsable(RetainingWallPlannerCore.WallRails rails, double tolerance, out string message)
    {
        message = string.Empty;
        int minimum = rails.IsClosed ? 3 : 2;
        if (rails.ToePoints.Length < minimum || rails.TopPoints.Length < minimum)
        {
            message = "too few rail points.";
            return false;
        }

        if (rails.MinWidth < Math.Max(tolerance * 0.1, 1e-6))
        {
            message = "strip width collapses too tightly.";
            return false;
        }

        return true;
    }

    private static SurfaceRemesher.ConstraintPolyline[] BuildWallConstraintCurves(RetainingWallPlannerCore.WallRails rails, double tolerance)
    {
        var curves = new List<SurfaceRemesher.ConstraintPolyline>(2);
        int minimum = rails.IsClosed ? 3 : 2;
        SurfaceRemesher.ConstraintPolyline toeCurve = CreateWallRailConstraint(rails.ToePoints, rails.IsClosed, tolerance);
        if (toeCurve.PointCount >= minimum)
            curves.Add(toeCurve);

        SurfaceRemesher.ConstraintPolyline topCurve = CreateWallRailConstraint(rails.TopPoints, rails.IsClosed, tolerance);
        if (topCurve.PointCount >= minimum)
            curves.Add(topCurve);

        return curves.ToArray();
    }

    private static SurfaceRemesher.ConstraintPolyline CreateWallRailConstraint(Point3d[] railPoints, bool isClosed, double tolerance)
    {
        int minimum = isClosed ? 3 : 2;
        if (railPoints.Length < minimum)
            return new SurfaceRemesher.ConstraintPolyline(Array.Empty<double>(), 0, isClosed, PreserveInputElevation: true);

        double tolSq = Math.Max(tolerance, 1e-6);
        tolSq *= tolSq;
        var points = new List<Point3d>(railPoints.Length);
        for (int i = 0; i < railPoints.Length; i++)
        {
            Point3d point = railPoints[i];
            if (points.Count > 0 && DistanceSquared2D(points[^1], point) <= tolSq)
            {
                points[^1] = point;
                continue;
            }

            points.Add(point);
        }

        if (isClosed && points.Count > 1 && DistanceSquared2D(points[0], points[^1]) <= tolSq)
            points.RemoveAt(points.Count - 1);

        if (points.Count < minimum)
            return new SurfaceRemesher.ConstraintPolyline(Array.Empty<double>(), 0, isClosed, PreserveInputElevation: true);

        var values = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            values[i * 3] = points[i].X;
            values[i * 3 + 1] = points[i].Y;
            values[i * 3 + 2] = points[i].Z;
        }

        return new SurfaceRemesher.ConstraintPolyline(values, points.Count, isClosed, PreserveInputElevation: true);
    }

    private static List<SurfaceRemesher.ConstraintPolyline> PrepareWallConstraintsForRemesh(
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance)
    {
        var prepared = new List<SurfaceRemesher.ConstraintPolyline>(constraints.Count);
        if (constraints.Count == 0)
            return prepared;

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
        {
            foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
            {
                SurfaceRemesher.ConstraintPolyline cleaned = CleanWallConstraintPolyline(constraint, tolerance);
                if (cleaned.PointCount >= 2)
                    prepared.Add(cleaned);
            }

            return prepared;
        }

        var snapper = new ConstraintCoincidenceSnapper(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            Math.Max(tolerance, 1e-6));

        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            SurfaceRemesher.ConstraintPolyline snapped = snapper.SnapConstraintPolyline(constraint);
            SurfaceRemesher.ConstraintPolyline cleaned = CleanWallConstraintPolyline(snapped, tolerance);
            if (cleaned.PointCount >= 2)
                prepared.Add(cleaned);
        }

        return CombineConstraints(Array.Empty<SurfaceRemesher.ConstraintPolyline>(), prepared);
    }

    private static SurfaceRemesher.ConstraintPolyline CleanWallConstraintPolyline(
        SurfaceRemesher.ConstraintPolyline constraint,
        double tolerance)
    {
        if (constraint.PointCount < 2)
            return constraint;

        double tolSq = Math.Max(tolerance, 1e-6);
        tolSq *= tolSq;
        var points = new List<double>(constraint.PointCount * 3);
        for (int i = 0; i < constraint.PointCount; i++)
        {
            double x = constraint.Points[i * 3];
            double y = constraint.Points[i * 3 + 1];
            double z = constraint.Points[i * 3 + 2];
            if (points.Count >= 3)
            {
                double dx = points[^3] - x;
                double dy = points[^2] - y;
                if ((dx * dx) + (dy * dy) <= tolSq)
                {
                    points[^3] = x;
                    points[^2] = y;
                    points[^1] = z;
                    continue;
                }
            }

            points.Add(x);
            points.Add(y);
            points.Add(z);
        }

        int pointCount = points.Count / 3;
        int minimum = constraint.IsClosed ? 3 : 2;
        return pointCount >= minimum
            ? new SurfaceRemesher.ConstraintPolyline(points.ToArray(), pointCount, constraint.IsClosed, constraint.PreserveInputElevation)
            : new SurfaceRemesher.ConstraintPolyline(Array.Empty<double>(), 0, constraint.IsClosed, constraint.PreserveInputElevation);
    }

    private static bool TryInsertWallConstraintsIntoExistingMesh(
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> wallConstraints,
        double tolerance,
        TerrainBuildResult build,
        bool reportFailures,
        bool afterCombinedRemeshFailed,
        out RhinoMesh insertedMesh)
    {
        insertedMesh = mesh;
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            if (reportFailures)
                build.Diagnostics.Add(errorMessage ?? "Retaining Wall topology insertion could not extract the upstream mesh.");
            return false;
        }

        if (!MeshConstraintTopologyInserter.TryInsert(
                vertices,
                mesh.Vertices.Count,
                faces,
                mesh.Faces.Count,
                wallConstraints,
                tolerance,
                out double[] outputVertices,
                out int outputVertexCount,
                out int[] outputFaces,
                out int outputFaceCount,
                out string? topologyError))
        {
            if (reportFailures)
                build.Diagnostics.Add(topologyError ?? "Retaining Wall topology insertion could not insert wall constraints into the existing mesh.");
            return false;
        }

        if (!TopologyChanged(vertices, mesh.Vertices.Count, faces, mesh.Faces.Count, outputVertices, outputVertexCount, outputFaces, outputFaceCount, tolerance))
        {
            if (reportFailures)
                build.Diagnostics.Add("Retaining Wall topology insertion found no terrain faces crossed by wall constraints.");
            return false;
        }

        var inputBoundary = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, mesh.Faces.Count);
        var outputBoundary = MeshTopologyValidator.AnalyzeBoundaryGraph(outputFaces, outputFaceCount);
        if (!IsTopologyInsertionBoundarySafe(inputBoundary, outputBoundary, out string boundaryMessage))
        {
            if (reportFailures)
                build.Diagnostics.Add($"Retaining Wall topology insertion rejected: {boundaryMessage}");
            return false;
        }

        ApplyPreservedConstraintElevations(outputVertices, wallConstraints, tolerance);
        insertedMesh = BuildMeshFromArrays(outputVertices, outputFaces);
        build.Diagnostics.Add(afterCombinedRemeshFailed
            ? "Retaining Wall topology fallback inserted wall breaklines into the existing mesh after combined remesh failed."
            : "Retaining Wall topology insertion inserted wall breaklines into the existing mesh.");
        return true;
    }

    private static bool IsTopologyInsertionBoundarySafe(
        MeshTopologyValidator.BoundaryGraphAnalysis inputBoundary,
        MeshTopologyValidator.BoundaryGraphAnalysis outputBoundary,
        out string message)
    {
        if (inputBoundary.HasSingleClosedBoundaryLoop && !outputBoundary.HasSingleClosedBoundaryLoop)
        {
            message = "it would break the original single closed terrain boundary.";
            return false;
        }

        if (!inputBoundary.HasOpenBoundaryChains && outputBoundary.HasOpenBoundaryChains)
        {
            message = "it would create open naked-edge chains.";
            return false;
        }

        if (outputBoundary.BoundaryComponentCount > inputBoundary.BoundaryComponentCount)
        {
            message = $"it would create extra boundary loops ({inputBoundary.BoundaryComponentCount} -> {outputBoundary.BoundaryComponentCount}).";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TopologyChanged(
        double[] inputVertices,
        int inputVertexCount,
        int[] inputFaces,
        int inputFaceCount,
        double[] outputVertices,
        int outputVertexCount,
        int[] outputFaces,
        int outputFaceCount,
        double tolerance)
    {
        if (outputVertexCount != inputVertexCount || outputFaceCount != inputFaceCount)
            return true;

        double tolSq = Math.Max(tolerance, 1e-9);
        tolSq *= tolSq;
        for (int i = 0; i < inputVertexCount; i++)
        {
            double dx = inputVertices[i * 3] - outputVertices[i * 3];
            double dy = inputVertices[i * 3 + 1] - outputVertices[i * 3 + 1];
            double dz = inputVertices[i * 3 + 2] - outputVertices[i * 3 + 2];
            if ((dx * dx) + (dy * dy) + (dz * dz) > tolSq)
                return true;
        }

        for (int i = 0; i < inputFaceCount * 3; i++)
        {
            if (inputFaces[i] != outputFaces[i])
                return true;
        }

        return false;
    }

    private static void ApplyPreservedConstraintElevations(
        double[] vertices,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance)
    {
        double matchTolerance = Math.Max(tolerance, 1e-6);
        double matchToleranceSquared = matchTolerance * matchTolerance;
        int vertexCount = vertices.Length / 3;
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            double x = vertices[vertexIndex * 3];
            double y = vertices[vertexIndex * 3 + 1];
            if (TryGetPreservedConstraintElevation(x, y, constraints, matchToleranceSquared, out double z))
                vertices[vertexIndex * 3 + 2] = z;
        }
    }

    private static bool TryGetPreservedConstraintElevation(
        double x,
        double y,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double maxDistanceSquared,
        out double z)
    {
        z = 0.0;
        bool found = false;
        double bestDistanceSquared = maxDistanceSquared;
        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            if (!constraint.PreserveInputElevation || constraint.PointCount < 2)
                continue;

            for (int pointIndex = 1; pointIndex < constraint.PointCount; pointIndex++)
            {
                if (!TryProjectToConstraintSegment(
                        constraint,
                        pointIndex - 1,
                        pointIndex,
                        x,
                        y,
                        bestDistanceSquared,
                        out double candidateZ,
                        out double distanceSquared))
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                z = candidateZ;
                found = true;
            }
        }

        return found;
    }

    private static bool TryProjectToConstraintSegment(
        SurfaceRemesher.ConstraintPolyline constraint,
        int startPointIndex,
        int endPointIndex,
        double x,
        double y,
        double maxDistanceSquared,
        out double z,
        out double distanceSquared)
    {
        double ax = constraint.Points[startPointIndex * 3];
        double ay = constraint.Points[startPointIndex * 3 + 1];
        double az = constraint.Points[startPointIndex * 3 + 2];
        double bx = constraint.Points[endPointIndex * 3];
        double by = constraint.Points[endPointIndex * 3 + 1];
        double bz = constraint.Points[endPointIndex * 3 + 2];
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-12)
        {
            z = 0.0;
            distanceSquared = double.PositiveInfinity;
            return false;
        }

        double t = (((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared;
        if (t < 0.0 || t > 1.0)
        {
            z = 0.0;
            distanceSquared = double.PositiveInfinity;
            return false;
        }

        double closestX = ax + (dx * t);
        double closestY = ay + (dy * t);
        double offsetX = x - closestX;
        double offsetY = y - closestY;
        distanceSquared = (offsetX * offsetX) + (offsetY * offsetY);
        if (distanceSquared > maxDistanceSquared)
        {
            z = 0.0;
            return false;
        }

        z = az + ((bz - az) * t);
        return true;
    }

    private static double DistanceSquared2D(Point3d a, Point3d b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static RhinoMesh RebuildMeshWithConstraints(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double requestedEdgeLength,
        double maxArea,
        double minAngle,
        string label,
        TerrainBuildResult build,
        out bool keptInputMesh,
        bool preferReducedInteriorSeed = false,
        bool addReducedInteriorGuideSeeds = true,
        bool addConstraintCorridorSeeds = true,
        double? toleranceOverride = null,
        bool protectSharpEdges = true,
        bool recordDetailedTimings = false)
    {
        keptInputMesh = false;
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var originalVertices, out var originalFaces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? $"Could not extract mesh data for {label.ToLowerInvariant()}.");
            return mesh;
        }

        double tolerance = toleranceOverride ?? GetToleranceProfile(snapshot, terrain).RemeshConstraintTolerance;
        var remeshCoreTimer = Stopwatch.StartNew();
        var remeshResult = SurfaceRemesher.Remesh(
            originalVertices,
            originalFaces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = tolerance,
                RequestedEdgeLength = requestedEdgeLength,
                MaxArea = maxArea,
                MinAngle = minAngle,
                ProtectSharpEdges = protectSharpEdges,
                PreferReducedInteriorSeed = preferReducedInteriorSeed,
                AddReducedInteriorGuideSeeds = addReducedInteriorGuideSeeds,
                AddConstraintCorridorSeeds = addConstraintCorridorSeeds,
                // When no quality params are set, RequestedEdgeLength controls only constraint
                // pre-densification spacing — do not use it to drive Steiner interior refinement.
                ConstraintInsertionOnly = maxArea <= 0 && minAngle <= 0
            });
        remeshCoreTimer.Stop();
        if (recordDetailedTimings)
        {
            build.RecordTiming(
                $"{label} SurfaceRemesher",
                remeshCoreTimer.Elapsed,
                remeshResult.Success ? "success" : "failed",
                StageTimingDiagnosticThresholdMs);
        }

        if (remeshResult.Profile is not null)
        {
            string remeshTimingReport = remeshResult.Profile.FormatReport($"{label} remesh timing");
            build.Diagnostics.Add(remeshTimingReport);
            Debug.WriteLine(remeshTimingReport);
        }

        if (!remeshResult.Success)
        {
            keptInputMesh = remeshResult.ReturnedInputMesh;
            if (remeshResult.ReturnedInputMesh)
            {
                build.Diagnostics.Add(
                    $"{label} remesh attempted constraint insertion but kept the upstream mesh unchanged; grading will continue on the existing topology.");
            }
            build.Diagnostics.Add(remeshResult.Warning ?? $"{label} triangulation failed.");
            return mesh;
        }

        if (remeshResult.AddedProtectedVertices > 0)
            build.Diagnostics.Add($"{label} added {remeshResult.AddedProtectedVertices} protected-edge vertices before triangulation.");

        if (remeshResult.UsedBoundaryAndGuideSeedFallback)
            build.Diagnostics.Add($"{label} retried from boundary, hard-constraint, and coarse interior guide seeds because carried mesh vertices prevented refinement.");

        if (!string.IsNullOrWhiteSpace(remeshResult.Warning))
            build.Diagnostics.Add(remeshResult.Warning);

        var meshBuildTimer = Stopwatch.StartNew();
        RhinoMesh rawMesh = BuildMeshFromArrays(remeshResult.Vertices, remeshResult.Faces);
        meshBuildTimer.Stop();
        if (recordDetailedTimings)
        {
            build.RecordTiming(
                $"{label} Mesh Build",
                meshBuildTimer.Elapsed,
                $"{rawMesh.Vertices.Count:N0} verts, {rawMesh.Faces.Count:N0} faces",
                StageTimingDiagnosticThresholdMs);
        }

        var cleanupTimer = Stopwatch.StartNew();
        RhinoMesh cleanedMesh = CleanTinyFaces(rawMesh, tolerance, label, build);
        cleanupTimer.Stop();
        if (recordDetailedTimings)
        {
            build.RecordTiming(
                $"{label} Tiny Cleanup",
                cleanupTimer.Elapsed,
                ReferenceEquals(cleanedMesh, rawMesh)
                    ? "unchanged"
                    : $"{cleanedMesh.Vertices.Count:N0} verts, {cleanedMesh.Faces.Count:N0} faces",
                StageTimingDiagnosticThresholdMs);
        }

        return cleanedMesh;
    }

    private static string DescribeTopologyCounts(int inputVertexCount, int inputFaceCount, int outputVertexCount, int outputFaceCount)
    {
        return $"{inputVertexCount:N0} verts/{inputFaceCount:N0} faces -> {outputVertexCount:N0} verts/{outputFaceCount:N0} faces";
    }

    private static List<SurfaceRemesher.ConstraintPolyline> CombineConstraints(
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentConstraints,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> additionalConstraints)
    {
        var seen = new HashSet<ConstraintSignature>();
        var result = new List<SurfaceRemesher.ConstraintPolyline>(persistentConstraints.Count + additionalConstraints.Count);
        AppendUniqueConstraints(result, seen, persistentConstraints);
        AppendUniqueConstraints(result, seen, additionalConstraints);
        return result;
    }

    private static void AppendUniqueConstraints(
        List<SurfaceRemesher.ConstraintPolyline> destination,
        HashSet<ConstraintSignature> seen,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        foreach (var constraint in constraints)
        {
            if (constraint.PointCount < 2)
                continue;

            if (seen.Add(CreateConstraintSignature(constraint)))
                destination.Add(constraint);
        }
    }

    private static ConstraintSignature CreateConstraintSignature(SurfaceRemesher.ConstraintPolyline constraint)
    {
        var fingerprint = new FingerprintBuilder();
        fingerprint.Add(constraint.PointCount);
        fingerprint.Add(constraint.IsClosed);
        fingerprint.Add(constraint.PreserveInputElevation);

        int pointValueCount = Math.Min(constraint.Points.Length, constraint.PointCount * 3);
        for (int i = 0; i < pointValueCount; i++)
            fingerprint.Add(constraint.Points[i]);

        return new ConstraintSignature(
            fingerprint.ToUInt64(),
            constraint.PointCount,
            constraint.IsClosed,
            constraint.PreserveInputElevation);
    }

    private static List<SurfaceRemesher.ConstraintPolyline> CreateConstraintPolylines(
        IReadOnlyList<Curve> curves,
        double tolerance,
        bool preserveInputElevation,
        double requestedEdgeLength = 0.0,
        double maxArea = 0.0)
    {
        var result = new List<SurfaceRemesher.ConstraintPolyline>();
        foreach (var curve in curves)
        {
            if (curve == null)
                continue;

            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, requestedEdgeLength, maxArea, out var polyline))
                continue;

            result.Add(ToConstraintPolyline(polyline, curve.IsClosed, preserveInputElevation));
        }

        return result;
    }

    private static List<double[]> CreateFlatPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        return TerrainTriangulationInputBuilder.CreateFlatPolylines(curves, tolerance);
    }

    private static List<double[]> CreateFlatPolylines(IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        var result = new List<double[]>(constraints.Count);
        foreach (var constraint in constraints)
        {
            if (constraint.PointCount < 2)
                continue;

            result.Add((double[])constraint.Points.Clone());
        }

        return result;
    }

    private static SurfaceRemesher.ConstraintPolyline ToConstraintPolyline(Polyline polyline, bool isClosed, bool preserveInputElevation = false)
    {
        var points = new double[polyline.Count * 3];
        for (int i = 0; i < polyline.Count; i++)
        {
            points[i * 3] = polyline[i].X;
            points[i * 3 + 1] = polyline[i].Y;
            points[i * 3 + 2] = polyline[i].Z;
        }

        return new SurfaceRemesher.ConstraintPolyline(points, polyline.Count, isClosed, preserveInputElevation);
    }

    private static double[] ToFlatPolyline(Polyline polyline)
    {
        return TerrainTriangulationInputBuilder.ToFlatPolyline(polyline);
    }

    private static TinBoundaryPreparer.BoundaryPolyline[] CreateBoundaryPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        var result = new List<TinBoundaryPreparer.BoundaryPolyline>();
        foreach (var curve in curves)
        {
            if (curve == null)
                continue;

            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            var flat = new double[polyline.Count * 3];
            for (int i = 0; i < polyline.Count; i++)
            {
                flat[i * 3] = polyline[i].X;
                flat[i * 3 + 1] = polyline[i].Y;
                flat[i * 3 + 2] = polyline[i].Z;
            }

            result.Add(new TinBoundaryPreparer.BoundaryPolyline(flat, polyline.Count, curve.IsClosed));
        }

        return result.ToArray();
    }

    private static TinBoundaryPreparer.BoundaryPolyline[] CreateBoundaryPolylines(RhinoMesh mesh, double tolerance)
    {
        var nakedEdges = mesh.GetNakedEdges();
        if (nakedEdges == null || nakedEdges.Length == 0)
            return Array.Empty<TinBoundaryPreparer.BoundaryPolyline>();

        var result = new List<TinBoundaryPreparer.BoundaryPolyline>(nakedEdges.Length);
        foreach (var polyline in nakedEdges)
        {
            if (polyline.Count < 2)
                continue;

            bool isClosed = polyline.IsClosed || polyline[0].DistanceTo(polyline[^1]) <= tolerance;
            result.Add(new TinBoundaryPreparer.BoundaryPolyline(ToFlatPolyline(polyline), polyline.Count, isClosed));
        }

        return result.ToArray();
    }

    private static TinBoundaryPreparer.BoundaryPolyline[] CombineBoundaryPolylines(
        IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> primary,
        IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> additional)
    {
        if (primary.Count == 0 && additional.Count == 0)
            return Array.Empty<TinBoundaryPreparer.BoundaryPolyline>();

        var result = new TinBoundaryPreparer.BoundaryPolyline[primary.Count + additional.Count];
        for (int i = 0; i < primary.Count; i++)
            result[i] = primary[i];
        for (int i = 0; i < additional.Count; i++)
            result[primary.Count + i] = additional[i];
        return result;
    }

    private static string AppendBuildMessage(string? current, string next)
    {
        return string.IsNullOrWhiteSpace(current)
            ? next
            : $"{current} {next}";
    }

    private static string? AppendCacheHitDetail(string? detail)
    {
        return string.IsNullOrWhiteSpace(detail)
            ? "cache hit"
            : $"{detail}; cache hit";
    }

    private static ulong ComputeModifierStageFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        ModifierDefinition modifier,
        ulong upstreamFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add(upstreamFingerprint);
        builder.Add(modifier.GetType().FullName);
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(terrain.GlobalTolerance);
        AddSerializedFingerprint(ref builder, modifier, modifier.GetType());

        int sourceIndex = 0;
        foreach (var sourceSet in modifier.EnumerateSourceSets())
        {
            builder.Add(sourceIndex++);
            builder.Add(ComputeSourceSetFingerprint(snapshot, sourceSet));
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeTriangulatePreResolutionFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        TriangulateModifierDefinition modifier)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Triangulate");
        builder.Add(TriangulateCacheVersion);
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(terrain.GlobalTolerance);
        AddSerializedFingerprint(ref builder, modifier, modifier.GetType());
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Points));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Breaklines));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Contours));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Boundary));
        return builder.ToUInt64();
    }

    private static ulong ComputeTriangulateResolvedInputFingerprint(
        TerrainDefinition terrain,
        TriangulateModifierDefinition modifier,
        double tolerance,
        double[] xyCoords,
        double[] zValues,
        int[] segments,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints,
        IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> boundaryPolylines)
    {
        var builder = new FingerprintBuilder();
        builder.Add("TriangulateResolved");
        builder.Add(TriangulateCacheVersion);
        builder.Add(terrain.GlobalTolerance);
        builder.Add(tolerance);
        AddSerializedFingerprint(ref builder, modifier, modifier.GetType());
        AddDoubleArrayFingerprint(ref builder, xyCoords);
        AddDoubleArrayFingerprint(ref builder, zValues);
        AddIntArrayFingerprint(ref builder, segments);
        builder.Add(ComputeConstraintsFingerprint(persistentHardConstraints));
        builder.Add(ComputeBoundaryPolylinesFingerprint(boundaryPolylines));
        return builder.ToUInt64();
    }

    private static ulong ComputeGradePadTopologyFingerprint(
        ulong upstreamFingerprint,
        double tolerance,
        GradePadModifierDefinition modifier,
        IReadOnlyList<PadGrader.PadBoundary> pads,
        IReadOnlyList<PadGrader.LockCurve> lockCurves)
    {
        var builder = new FingerprintBuilder();
        builder.Add("GradePadTopologyV4");
        builder.Add(upstreamFingerprint);
        builder.Add(tolerance);
        builder.Add(modifier.SlopeAngle);
        builder.Add(modifier.MaxDistance);
        builder.Add(pads.Count);
        foreach (var pad in pads)
        {
            builder.Add(pad.VertexCount);
            AddDoubleArrayFingerprint(ref builder, pad.XyVertices);
            AddDoubleArrayFingerprint(ref builder, pad.BoundaryVertices);
            builder.Add(pad.PlaneXCoeff);
            builder.Add(pad.PlaneYCoeff);
            builder.Add(pad.PlaneConstant);
            builder.Add(pad.StitchApronDistance);
        }

        builder.Add(lockCurves.Count);
        foreach (var lc in lockCurves)
        {
            builder.Add(lc.VertexCount);
            AddDoubleArrayFingerprint(ref builder, lc.XyVertices);
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeSmoothPreparedFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SmoothModifierDefinition modifier,
        ulong upstreamFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("SmoothPrepared");
        builder.Add(upstreamFingerprint);
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(terrain.GlobalTolerance);
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Boundaries));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Breaklines));
        return builder.ToUInt64();
    }

    private static ulong ComputeGradePadResolvedInputFingerprint(
        ulong topologyOutputFingerprint,
        IReadOnlyList<PadGrader.PadBoundary> pads,
        GradePadModifierDefinition modifier)
    {
        var builder = new FingerprintBuilder();
        builder.Add("GradePadResolved");
        builder.Add(topologyOutputFingerprint);
        builder.Add(modifier.SlopeAngle);
        builder.Add(modifier.MaxDistance);
        builder.Add(pads.Count);
        foreach (var pad in pads)
        {
            builder.Add(pad.VertexCount);
            AddDoubleArrayFingerprint(ref builder, pad.XyVertices);
            AddDoubleArrayFingerprint(ref builder, pad.BoundaryVertices);
            builder.Add(pad.PlaneXCoeff);
            builder.Add(pad.PlaneYCoeff);
            builder.Add(pad.PlaneConstant);
            builder.Add(pad.StitchApronDistance);
        }

        return builder.ToUInt64();
    }

    private static List<GradingPatch> BuildPadPatchSummaries(IReadOnlyList<PadGrader.PadBoundary> pads)
    {
        var patches = new List<GradingPatch>(pads.Count);
        for (int i = 0; i < pads.Count; i++)
        {
            var pad = pads[i];
            double priority = ComputePadOwnershipPriority(pad);
            patches.Add(new GradingPatch
            {
                OwnerKey = $"pad:{i}",
                Kind = GradingPatchKind.Pad,
                Priority = priority,
                OwnedRegionLoopXy = (double[])pad.XyVertices.Clone(),
                DaylightLoopXy = Array.Empty<double>(),
                StitchLoopXy = Array.Empty<double>(),
                DirtyBounds = GradingPatch.ComputeBounds(pad.XyVertices),
                UsesFallbackBand = false
            });
        }

        return patches;
    }

    private static double ComputePadOwnershipPriority(PadGrader.PadBoundary pad)
    {
        double sumX = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < pad.VertexCount; i++)
        {
            sumX += pad.XyVertices[i * 2];
            sumY += pad.XyVertices[i * 2 + 1];
        }

        double cx = sumX / pad.VertexCount;
        double cy = sumY / pad.VertexCount;
        return pad.EvaluateZ(cx, cy);
    }

    private static List<GradingPatch> BuildPathPatchSummaries(IReadOnlyList<PathGrader.PathDefinition> paths)
    {
        var patches = new List<GradingPatch>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            for (int v = 0; v < path.VertexCount; v++)
            {
                double x = path.XyVertices[v * 2];
                double y = path.XyVertices[v * 2 + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            double halfWidth = path.Width * 0.5;
            double shoulderAllowance = path.MaxDistance > 0.0
                ? path.MaxDistance
                : Math.Max(path.Width * 2.0, halfWidth);
            double expansion = halfWidth + shoulderAllowance;
            double[] ownedLoop =
            {
                minX - expansion, minY - expansion,
                maxX + expansion, minY - expansion,
                maxX + expansion, maxY + expansion,
                minX - expansion, maxY + expansion
            };

            patches.Add(new GradingPatch
            {
                OwnerKey = $"path:{i}",
                Kind = GradingPatchKind.Path,
                Priority = i,
                OwnedRegionLoopXy = ownedLoop,
                DaylightLoopXy = Array.Empty<double>(),
                StitchLoopXy = Array.Empty<double>(),
                DirtyBounds = GradingPatch.ComputeBounds(ownedLoop),
                UsesFallbackBand = false
            });
        }

        return patches;
    }

    private static GradingTopologyCacheEntry BuildPathTopologyEntry(
        IReadOnlyList<double> inputVertices,
        int inputVertexCount,
        IReadOnlyList<int> inputFaces,
        int inputFaceCount,
        GradingResult? gradingResult,
        IReadOnlyList<GradingPatch> conservativePatchSummaries,
        IReadOnlyList<string> diagnostics)
    {
        IReadOnlyList<double> vertices = gradingResult?.Vertices ?? inputVertices;
        int vertexCount = gradingResult?.VertexCount ?? inputVertexCount;
        IReadOnlyList<int> faces = gradingResult?.Faces ?? inputFaces;
        int faceCount = gradingResult?.FaceCount ?? inputFaceCount;
        List<GradingPatch> patchSummaries = gradingResult?.PatchSummaries.Count > 0
            ? ClonePatchSummaries(gradingResult.PatchSummaries)
            : ClonePatchSummaries(conservativePatchSummaries);

        return new GradingTopologyCacheEntry
        {
            GraderKind = "Path",
            Fingerprint = 0,
            OutputFingerprint = ComputeGradingTopologyOutputFingerprint("Path", vertices, vertexCount, faces, faceCount),
            Vertices = vertices.ToArray(),
            VertexCount = vertexCount,
            Faces = faces.ToArray(),
            FaceCount = faceCount,
            PatchSummaries = patchSummaries,
            Diagnostics = diagnostics.ToList()
        };
    }

    private static GradingTopologyCacheEntry BuildPathTopologyEntryFromMesh(
        RhinoMesh mesh,
        IReadOnlyList<GradingPatch> conservativePatchSummaries,
        IReadOnlyList<string> diagnostics)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
        {
            return new GradingTopologyCacheEntry
            {
                GraderKind = "Path",
                Fingerprint = 0,
                OutputFingerprint = 0,
                Vertices = Array.Empty<double>(),
                VertexCount = 0,
                Faces = Array.Empty<int>(),
                FaceCount = 0,
                PatchSummaries = ClonePatchSummaries(conservativePatchSummaries),
                Diagnostics = diagnostics.ToList()
            };
        }

        return new GradingTopologyCacheEntry
        {
            GraderKind = "Path",
            Fingerprint = 0,
            OutputFingerprint = ComputeGradingTopologyOutputFingerprint("Path", vertices, mesh.Vertices.Count, faces, mesh.Faces.Count),
            Vertices = vertices,
            VertexCount = mesh.Vertices.Count,
            Faces = faces,
            FaceCount = mesh.Faces.Count,
            PatchSummaries = ClonePatchSummaries(conservativePatchSummaries),
            Diagnostics = diagnostics.ToList()
        };
    }

    private static List<GradingPatch> ClonePatchSummaries(IReadOnlyList<GradingPatch> patchSummaries)
    {
        return patchSummaries
            .Select(static patch => new GradingPatch
            {
                OwnerKey = patch.OwnerKey,
                Kind = patch.Kind,
                Priority = patch.Priority,
                OwnedRegionLoopXy = (double[])patch.OwnedRegionLoopXy.Clone(),
                DaylightLoopXy = patch.DaylightLoopXy != null ? (double[])patch.DaylightLoopXy.Clone() : Array.Empty<double>(),
                StitchLoopXy = patch.StitchLoopXy != null ? (double[])patch.StitchLoopXy.Clone() : Array.Empty<double>(),
                DirtyBounds = patch.DirtyBounds,
                UsesFallbackBand = patch.UsesFallbackBand
            })
            .ToList();
    }

    private static RhinoMesh BuildMeshFromArrays(double[] vertices, int[] faces)
    {
        var mesh = new RhinoMesh();
        mesh.Vertices.Capacity = vertices.Length / 3;
        mesh.Faces.Capacity = faces.Length / 3;

        for (int i = 0; i < vertices.Length / 3; i++)
            mesh.Vertices.Add(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);

        for (int i = 0; i < faces.Length / 3; i++)
            mesh.Faces.AddFace(faces[i * 3], faces[i * 3 + 1], faces[i * 3 + 2]);

        RhinoGeometryConversions.NormalizeMeshInPlace(mesh);
        return mesh;
    }

    private static RhinoMesh FinalizeGradingMesh(RhinoMesh mesh, string sourceLabel, TerrainBuildResult build)
    {
        RhinoGeometryConversions.NormalizeMeshInPlace(mesh);
        build.Diagnostics.Add($"{sourceLabel} skipped tiny-face deletion; grading output must not introduce holes.");
        return mesh;
    }

    private static int CountBoundaryEdgesNearLoop(
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        int faceCount,
        double[] loopXy,
        double distanceTolerance)
    {
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            IncrementEdge(edgeFaceCount, faces[f * 3], faces[f * 3 + 1]);
            IncrementEdge(edgeFaceCount, faces[f * 3 + 1], faces[f * 3 + 2]);
            IncrementEdge(edgeFaceCount, faces[f * 3 + 2], faces[f * 3]);
        }

        int boundaryNearLoop = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double mx = (vertices[a * 3] + vertices[b * 3]) * 0.5;
            double my = (vertices[a * 3 + 1] + vertices[b * 3 + 1]) * 0.5;
            if (GradingGeometry2D.DistanceToPolygon(mx, my, loopXy, loopXy.Length / 2) <= distanceTolerance)
                boundaryNearLoop++;
        }

        return boundaryNearLoop;
    }

    private static int CountBoundaryLoops(IReadOnlyList<int> faces, int faceCount)
    {
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            IncrementEdge(edgeFaceCount, faces[f * 3], faces[f * 3 + 1]);
            IncrementEdge(edgeFaceCount, faces[f * 3 + 1], faces[f * 3 + 2]);
            IncrementEdge(edgeFaceCount, faces[f * 3 + 2], faces[f * 3]);
        }

        var adjacency = new Dictionary<int, HashSet<int>>();
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
        }

        int loops = 0;
        var visited = new HashSet<int>();
        foreach (int vertex in adjacency.Keys)
        {
            if (!visited.Add(vertex))
                continue;

            loops++;
            var queue = new Queue<int>();
            queue.Enqueue(vertex);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in adjacency[current])
                {
                    if (visited.Add(next))
                        queue.Enqueue(next);
                }
            }
        }

        return loops;
    }

    private static void IncrementEdge(Dictionary<long, int> edgeFaceCount, int a, int b)
    {
        long key = IndexedMeshTools.GetEdgeKey(a, b);
        edgeFaceCount.TryGetValue(key, out int value);
        edgeFaceCount[key] = value + 1;
    }

    private static void AddBoundaryNeighbor(Dictionary<int, HashSet<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out HashSet<int>? neighbors))
        {
            neighbors = new HashSet<int>();
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
    }

    private static RhinoMesh CleanTinyFaces(RhinoMesh mesh, double tolerance, string sourceLabel, TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
            return mesh;

        TinyFaceCleanupResult cleanup = ComputeTinyFaceCleanup(vertices, faces, mesh.Faces.Count, tolerance);
        if (!cleanup.HasChanges)
        {
            if (cleanup.BlockedFaceCount > 0)
                build.Diagnostics.Add($"{sourceLabel} kept the pre-cleanup mesh because tiny-face cleanup would create extra boundary loops or open naked-edge chains.");
            return mesh;
        }

        if (cleanup.BlockedFaceCount > 0)
        {
            build.Diagnostics.Add(
                $"{sourceLabel} removed {cleanup.RemovedFaceCount} tiny faces and kept {cleanup.BlockedFaceCount} because removing them would create extra boundary loops or open naked-edge chains.");
        }
        else
        {
            build.Diagnostics.Add($"{sourceLabel} removed {cleanup.RemovedFaceCount} tiny faces.");
        }

        return BuildRemappedMesh(vertices, new List<int>(cleanup.Faces));
    }

    internal static TinyFaceCleanupResult ComputeTinyFaceCleanup(
        double[] vertices,
        int[] faces,
        int faceCount,
        double tolerance)
    {
        var originalTopology = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        double medianEdgeLength = ComputeMedianUndirectedEdgeLength(vertices, faces, faceCount);
        double effectiveCleanupTolerance = Math.Max(
            1e-6,
            Math.Min(
                Math.Max(tolerance, 1e-6),
                medianEdgeLength > 0 ? medianEdgeLength * 0.01 : 0.01));
        double minEdgeLength = Math.Max(effectiveCleanupTolerance * 2.0, 1e-5);
        double minEdgeLengthSquared = minEdgeLength * minEdgeLength;
        double minProjectedArea = Math.Max(effectiveCleanupTolerance * effectiveCleanupTolerance * 2.0, 1e-10);
        Dictionary<long, int> originalEdgeCounts = BuildFaceEdgeCounts(faces, faceCount);

        var candidates = new List<(int FaceIndex, double Area, double SmallestEdgeSquared, double MinProjectedAltitude)>();
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];

            double ax = vertices[a * 3];
            double ay = vertices[a * 3 + 1];
            double az = vertices[a * 3 + 2];
            double bx = vertices[b * 3];
            double by = vertices[b * 3 + 1];
            double bz = vertices[b * 3 + 2];
            double cx = vertices[c * 3];
            double cy = vertices[c * 3 + 1];
            double cz = vertices[c * 3 + 2];

            double abx = bx - ax;
            double aby = by - ay;
            double abz = bz - az;
            double bcx = cx - bx;
            double bcy = cy - by;
            double bcz = cz - bz;
            double cax = ax - cx;
            double cay = ay - cy;
            double caz = az - cz;

            double l0Squared = (abx * abx) + (aby * aby) + (abz * abz);
            double l1Squared = (bcx * bcx) + (bcy * bcy) + (bcz * bcz);
            double l2Squared = (cax * cax) + (cay * cay) + (caz * caz);
            double area = Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) * 0.5;
            double smallestEdgeSquared = Math.Min(l0Squared, Math.Min(l1Squared, l2Squared));
            double projectedL0Squared = (abx * abx) + (aby * aby);
            double projectedL1Squared = (bcx * bcx) + (bcy * bcy);
            double projectedL2Squared = (cax * cax) + (cay * cay);
            double longestProjectedEdgeSquared = Math.Max(projectedL0Squared, Math.Max(projectedL1Squared, projectedL2Squared));
            double shortestProjectedEdgeSquared = Math.Min(projectedL0Squared, Math.Min(projectedL1Squared, projectedL2Squared));
            double longestProjectedEdge = Math.Sqrt(longestProjectedEdgeSquared);
            double minProjectedAltitude = longestProjectedEdge > 1e-12
                ? (2.0 * area) / longestProjectedEdge
                : 0.0;
            double projectedDuplicateTolerance = effectiveCleanupTolerance * 0.5;
            bool projectedDuplicate = shortestProjectedEdgeSquared <= projectedDuplicateTolerance * projectedDuplicateTolerance;
            bool projectedSliver =
                longestProjectedEdge > effectiveCleanupTolerance * 8.0 &&
                minProjectedAltitude < Math.Max(effectiveCleanupTolerance * 0.5, longestProjectedEdge * 0.001);

            bool shortBoundaryEdge = HasShortBoundaryEdge(
                originalEdgeCounts,
                a,
                b,
                c,
                l0Squared,
                l1Squared,
                l2Squared,
                minEdgeLengthSquared);
            bool hasBoundaryEdge = FaceHasBoundaryEdge(originalEdgeCounts, a, b, c);

            if (shortBoundaryEdge ||
                area < minProjectedArea ||
                (projectedDuplicate && shortBoundaryEdge) ||
                (projectedSliver && hasBoundaryEdge))
                candidates.Add((faceIndex, area, smallestEdgeSquared, minProjectedAltitude));
        }

        if (candidates.Count == 0)
            return new TinyFaceCleanupResult(faces, 0, 0);

        candidates.Sort(static (left, right) =>
        {
            int compare = left.Area.CompareTo(right.Area);
            if (compare != 0)
                return compare;

            int altitudeCompare = left.MinProjectedAltitude.CompareTo(right.MinProjectedAltitude);
            if (altitudeCompare != 0)
                return altitudeCompare;

            return left.SmallestEdgeSquared.CompareTo(right.SmallestEdgeSquared);
        });

        bool[] keepFace = new bool[faceCount];
        Array.Fill(keepFace, true);

        int removedCount = 0;
        int blockedCount = 0;
        bool enforceSingleClosedBoundaryLoop = HasSingleClosedBoundaryLoopIgnoringNonManifoldEdges(originalTopology);
        Dictionary<long, int>? edgeCounts = enforceSingleClosedBoundaryLoop
            ? new Dictionary<long, int>(originalEdgeCounts, IndexedMeshTools.EdgeKeyComparer.Instance)
            : null;

        foreach (var candidate in candidates)
        {
            if (enforceSingleClosedBoundaryLoop &&
                FaceHasNoCurrentBoundaryEdges(edgeCounts!, faces, candidate.FaceIndex))
            {
                blockedCount++;
                continue;
            }

            keepFace[candidate.FaceIndex] = false;
            if (enforceSingleClosedBoundaryLoop)
            {
                ApplyFaceEdgeCountDelta(edgeCounts!, faces, candidate.FaceIndex, -1);
                var proposedTopology = AnalyzeBoundaryGraphFromEdgeCounts(edgeCounts!);
                if (!HasSingleClosedBoundaryLoopIgnoringNonManifoldEdges(proposedTopology))
                {
                    ApplyFaceEdgeCountDelta(edgeCounts!, faces, candidate.FaceIndex, 1);
                    keepFace[candidate.FaceIndex] = true;
                    blockedCount++;
                    continue;
                }
            }

            removedCount++;
        }

        if (removedCount == 0)
            return new TinyFaceCleanupResult(faces, 0, blockedCount);

        return new TinyFaceCleanupResult(
            BuildFilteredFaces(faces, faceCount, keepFace, removedCount),
            removedCount,
            blockedCount);
    }

    private static bool HasShortBoundaryEdge(
        IReadOnlyDictionary<long, int> edgeCounts,
        int a,
        int b,
        int c,
        double abLengthSquared,
        double bcLengthSquared,
        double caLengthSquared,
        double minLengthSquared)
    {
        return (abLengthSquared < minLengthSquared && IsBoundaryEdge(edgeCounts, a, b)) ||
               (bcLengthSquared < minLengthSquared && IsBoundaryEdge(edgeCounts, b, c)) ||
               (caLengthSquared < minLengthSquared && IsBoundaryEdge(edgeCounts, c, a));
    }

    private static bool IsBoundaryEdge(IReadOnlyDictionary<long, int> edgeCounts, int a, int b)
    {
        return edgeCounts.TryGetValue(IndexedMeshTools.GetEdgeKey(a, b), out int count) && count == 1;
    }

    private static bool FaceHasBoundaryEdge(IReadOnlyDictionary<long, int> edgeCounts, int a, int b, int c)
    {
        return IsBoundaryEdge(edgeCounts, a, b) ||
               IsBoundaryEdge(edgeCounts, b, c) ||
               IsBoundaryEdge(edgeCounts, c, a);
    }

    private static bool HasSingleClosedBoundaryLoopIgnoringNonManifoldEdges(MeshTopologyValidator.BoundaryGraphAnalysis topology)
    {
        return topology.BoundaryEdgeCount > 0 &&
               topology.BoundaryComponentCount == 1 &&
               !topology.HasOpenBoundaryChains;
    }

    private static Dictionary<long, int> BuildFaceEdgeCounts(int[] faces, int faceCount)
    {
        var edgeCounts = new Dictionary<long, int>(Math.Max(faceCount * 3 / 2, 8), IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            IncrementEdgeCount(edgeCounts, a, b, 1);
            IncrementEdgeCount(edgeCounts, b, c, 1);
            IncrementEdgeCount(edgeCounts, c, a, 1);
        }

        return edgeCounts;
    }

    private static bool FaceHasNoCurrentBoundaryEdges(Dictionary<long, int> edgeCounts, int[] faces, int faceIndex)
    {
        int a = faces[faceIndex * 3];
        int b = faces[faceIndex * 3 + 1];
        int c = faces[faceIndex * 3 + 2];
        return GetEdgeCount(edgeCounts, a, b) != 1 &&
               GetEdgeCount(edgeCounts, b, c) != 1 &&
               GetEdgeCount(edgeCounts, c, a) != 1;
    }

    private static int GetEdgeCount(Dictionary<long, int> edgeCounts, int a, int b)
    {
        edgeCounts.TryGetValue(IndexedMeshTools.GetEdgeKey(a, b), out int count);
        return count;
    }

    private static void ApplyFaceEdgeCountDelta(Dictionary<long, int> edgeCounts, int[] faces, int faceIndex, int delta)
    {
        int a = faces[faceIndex * 3];
        int b = faces[faceIndex * 3 + 1];
        int c = faces[faceIndex * 3 + 2];
        IncrementEdgeCount(edgeCounts, a, b, delta);
        IncrementEdgeCount(edgeCounts, b, c, delta);
        IncrementEdgeCount(edgeCounts, c, a, delta);
    }

    private static void IncrementEdgeCount(Dictionary<long, int> edgeCounts, int a, int b, int delta)
    {
        long key = IndexedMeshTools.GetEdgeKey(a, b);
        edgeCounts.TryGetValue(key, out int count);
        count += delta;
        if (count == 0)
            edgeCounts.Remove(key);
        else
            edgeCounts[key] = count;
    }

    private static MeshTopologyValidator.BoundaryGraphAnalysis AnalyzeBoundaryGraphFromEdgeCounts(
        IReadOnlyDictionary<long, int> edgeCounts)
    {
        var adjacency = new Dictionary<int, List<int>>();
        var degree = new Dictionary<int, int>();
        int boundaryEdgeCount = 0;
        int nonManifoldEdgeCount = 0;

        foreach (var pair in edgeCounts)
        {
            if (pair.Value > 2)
            {
                nonManifoldEdgeCount++;
                continue;
            }

            if (pair.Value != 1)
                continue;

            boundaryEdgeCount++;
            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            AddBoundaryNeighbor(adjacency, degree, a, b);
            AddBoundaryNeighbor(adjacency, degree, b, a);
        }

        if (boundaryEdgeCount == 0)
            return new MeshTopologyValidator.BoundaryGraphAnalysis(0, 0, 0, HasOpenBoundaryChains: true, nonManifoldEdgeCount);

        bool hasOpenBoundaryChains = degree.Values.Any(value => value != 2);
        int boundaryComponentCount = 0;
        var visited = new HashSet<int>();

        foreach (int start in adjacency.Keys)
        {
            if (!visited.Add(start))
                continue;

            boundaryComponentCount++;
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                foreach (int next in adjacency[current])
                {
                    if (visited.Add(next))
                        stack.Push(next);
                }
            }
        }

        return new MeshTopologyValidator.BoundaryGraphAnalysis(
            boundaryEdgeCount,
            degree.Count,
            boundaryComponentCount,
            hasOpenBoundaryChains,
            nonManifoldEdgeCount);
    }

    private static void AddBoundaryNeighbor(
        Dictionary<int, List<int>> adjacency,
        Dictionary<int, int> degree,
        int from,
        int to)
    {
        if (!adjacency.TryGetValue(from, out var neighbors))
        {
            neighbors = new List<int>(2);
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
        degree[from] = degree.GetValueOrDefault(from) + 1;
    }

    private static double ComputeMedianUndirectedEdgeLength(double[] vertices, int[] faces, int faceCount)
    {
        var edgeLengths = new List<double>(faceCount * 3);
        var seen = new HashSet<long>();

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            AddEdgeLength(a, b);
            AddEdgeLength(b, c);
            AddEdgeLength(c, a);
        }

        if (edgeLengths.Count == 0)
            return 0.0;

        edgeLengths.Sort();
        int middle = edgeLengths.Count / 2;
        return edgeLengths.Count % 2 == 0
            ? (edgeLengths[middle - 1] + edgeLengths[middle]) * 0.5
            : edgeLengths[middle];

        void AddEdgeLength(int a, int b)
        {
            long key = a < b
                ? ((long)a << 32) | (uint)b
                : ((long)b << 32) | (uint)a;
            if (!seen.Add(key))
                return;

            double dx = vertices[a * 3] - vertices[b * 3];
            double dy = vertices[a * 3 + 1] - vertices[b * 3 + 1];
            double dz = vertices[a * 3 + 2] - vertices[b * 3 + 2];
            edgeLengths.Add(Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
    }

    private static RhinoMesh BuildRemappedMesh(double[] vertices, List<int> faces)
    {
        var used = faces.Distinct().ToList();
        var remap = new Dictionary<int, int>(used.Count);
        var compactVertices = new double[used.Count * 3];

        for (int i = 0; i < used.Count; i++)
        {
            remap[used[i]] = i;
            compactVertices[i * 3] = vertices[used[i] * 3];
            compactVertices[i * 3 + 1] = vertices[used[i] * 3 + 1];
            compactVertices[i * 3 + 2] = vertices[used[i] * 3 + 2];
        }

        var compactFaces = new int[faces.Count];
        for (int i = 0; i < faces.Count; i++)
            compactFaces[i] = remap[faces[i]];

        return RhinoGeometryConversions.BuildMesh(compactVertices, used.Count, compactFaces, compactFaces.Length / 3);
    }

    private static int[] BuildFilteredFaces(int[] faces, int faceCount, bool[] keepFace, int removedFaceCount)
    {
        var filteredFaces = new int[(faceCount - removedFaceCount) * 3];
        int outputIndex = 0;
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if (!keepFace[faceIndex])
                continue;

            filteredFaces[outputIndex++] = faces[faceIndex * 3];
            filteredFaces[outputIndex++] = faces[faceIndex * 3 + 1];
            filteredFaces[outputIndex++] = faces[faceIndex * 3 + 2];
        }

        return filteredFaces;
    }

    private static T MeasureStage<T>(
        TerrainBuildResult build,
        string stage,
        Func<T> action,
        Func<T, string?> detailFactory,
        int diagnosticThresholdMs = StageTimingDiagnosticThresholdMs)
    {
        var timer = Stopwatch.StartNew();
        T result = action();
        timer.Stop();
        build.RecordTiming(stage, timer.Elapsed, detailFactory(result), diagnosticThresholdMs);
        return result;
    }

    private static string DescribeBuildOutputs(TerrainBuildResult build)
    {
        return $"{DescribeMesh(build.PrimaryMesh) ?? "no mesh"}; " +
               $"{build.ZoneObjects.Count:N0} zone outputs, {build.AuxiliaryObjects.Count:N0} auxiliary outputs, {CountObjectPlacements(build):N0} object placements";
    }

    private static int CountObjectPlacements(TerrainBuildResult build)
    {
        return build.ObjectPlacements.Sum(group => group.Placements.Count);
    }

    private static string DescribeModifierMeshResult(string label, RhinoMesh? mesh)
    {
        string meshDetail = DescribeMesh(mesh) ?? "no mesh";
        return string.IsNullOrWhiteSpace(label)
            ? meshDetail
            : $"modifier '{label}', {meshDetail}";
    }

    private static string? DescribeMesh(RhinoMesh? mesh)
    {
        return mesh == null
            ? null
            : $"{mesh.Vertices.Count:N0} verts, {mesh.Faces.Count:N0} faces";
    }

    private static MarkerBlockTemplate GetMarkerBlockTemplate(MarkerDefinition marker)
    {
        return marker switch
        {
            ElevationMarkerDefinition => MarkerBlockTemplate.Elevation,
            SlopeMarkerDefinition => MarkerBlockTemplate.Slope,
            _ => MarkerBlockTemplate.None
        };
    }

    private static string GetMarkerBlockName(MarkerDefinition marker)
    {
        if (!string.IsNullOrWhiteSpace(marker.BlockDefinitionName))
            return marker.BlockDefinitionName!;

        return marker switch
        {
            ElevationMarkerDefinition => "MoleHill_ElevationMarker",
            SlopeMarkerDefinition => "MoleHill_SlopeMarker",
            _ => "MoleHill_Marker"
        };
    }
}
