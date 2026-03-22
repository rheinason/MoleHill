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
using TriangleNet.Meshing;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainBuildService
{
    private const int StageTimingDiagnosticThresholdMs = 250;
    private const int MaxLegacyPathTriangulationVertices = 25_000;
    private const int MaxLegacyPathTriangulationFaces = 50_000;

    private sealed class ZoneBoundaryEntry
    {
        public required CollageZoneDefinition Zone { get; init; }

        public required MeshAreaSplitter.AreaBoundary Boundary { get; init; }

        public required int ZoneOrder { get; init; }

        public required int SourceOrder { get; init; }

        public required double PriorityZ { get; init; }

        public string? InputLayerPath { get; init; }
    }

    private sealed class ResolvedGradePadInputs
    {
        public required PadGrader.PadBoundary[] Pads { get; init; }

        public required PadGrader.LockCurve[] Locks { get; init; }

        public required SurfaceRemesher.ConstraintPolyline[] Constraints { get; init; }
    }

    private sealed class ResolvedGradePathInputs
    {
        public required PathGrader.PathDefinition[] Paths { get; init; }

        public required SurfaceRemesher.ConstraintPolyline[] Constraints { get; init; }

        public required double SuggestedEdgeLength { get; init; }
    }

    private readonly record struct ConstraintSignature(
        ulong Fingerprint,
        int PointCount,
        bool IsClosed,
        bool PreserveInputElevation);

    public TerrainBuildResult Build(RhinoDoc doc, TerrainDefinition terrain, TerrainRuntimeCache runtimeCache, Func<bool>? shouldCancel = null)
    {
        var totalTimer = Stopwatch.StartNew();
        var build = new TerrainBuildResult();
        var usedStageKeys = new HashSet<string>(StringComparer.Ordinal);
        RhinoMesh? currentMesh = null;
        RhinoMesh? baseMesh = null;
        ulong currentMeshFingerprint = 0;
        ulong baseMeshFingerprint = 0;

        foreach (var indexedModifier in terrain.Modifiers.Select((modifier, index) => (modifier, index)).Where(item => item.modifier.IsEnabled))
        {
            ThrowIfCancellationRequested(shouldCancel);
            ModifierDefinition modifier = indexedModifier.modifier;
            string stageKey = CreateModifierStageKey(indexedModifier.index, modifier);
            usedStageKeys.Add(stageKey);

            switch (modifier)
            {
                case TriangulateModifierDefinition triangulate:
                    currentMesh = BuildTinMesh(doc, terrain, triangulate, build, runtimeCache, stageKey, out currentMeshFingerprint, shouldCancel);
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
                        ComputeModifierStageFingerprint(doc, terrain, addGeometry, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, addGeometry.Label) : ApplyAddGeometry(doc, terrain, currentMesh, addGeometry, build, runtimeCache, shouldCancel),
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
                        ComputeModifierStageFingerprint(doc, terrain, remesh, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, remesh.Label) : ApplyRemesh(doc, terrain, currentMesh, remesh, build),
                        result => DescribeModifierMeshResult(remesh.Label, result),
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case SmoothModifierDefinition smooth:
                    currentMesh = ExecuteCachedMeshStage(
                        build,
                        runtimeCache,
                        stageKey,
                        "Smooth",
                        ComputeModifierStageFingerprint(doc, terrain, smooth, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, smooth.Label) : ApplySmooth(doc, terrain, currentMesh, smooth, build),
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
                        ComputeModifierStageFingerprint(doc, terrain, retainingWall, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, retainingWall.Label) : ApplyRetainingWalls(doc, terrain, currentMesh, retainingWall, build),
                        result => DescribeModifierMeshResult(retainingWall.Label, result),
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case GradePadModifierDefinition gradePad:
                    usedStageKeys.Add(CreateGradePadTopologyStageKey(stageKey));
                    currentMesh = BuildGradePadMesh(
                        doc,
                        terrain,
                        gradePad,
                        build,
                        runtimeCache,
                        stageKey,
                        currentMesh,
                        currentMeshFingerprint,
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case GradePathModifierDefinition gradePath:
                    currentMesh = ExecuteCachedMeshStage(
                        build,
                        runtimeCache,
                        stageKey,
                        "Grade Path",
                        ComputeModifierStageFingerprint(doc, terrain, gradePath, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, gradePath.Label) : ApplyGradePath(doc, terrain, currentMesh, gradePath, build),
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
                        ComputeModifierStageFingerprint(doc, terrain, inSituStair, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, inSituStair.Label) : ApplyInSituStair(doc, terrain, currentMesh, inSituStair, build),
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
        if (currentMesh != null)
        {
            ThrowIfCancellationRequested(shouldCancel);
            RhinoMesh analysisMesh = currentMesh;
            RhinoMesh baselineMesh = baseMesh ?? analysisMesh;
            string analysisStageKey = "analysis";
            usedStageKeys.Add(analysisStageKey);
            build.Analysis = ExecuteCachedAnalysisStage(
                build,
                runtimeCache,
                analysisStageKey,
                ComputeAnalysisFingerprint(doc, terrain, baselineMesh, analysisMesh, baseMeshFingerprint, currentMeshFingerprint),
                () => BuildAnalysis(doc, terrain, baselineMesh, analysisMesh, build, shouldCancel),
                _ => DescribeMesh(analysisMesh),
                shouldCancel);

            string zonesStageKey = "zones";
            usedStageKeys.Add(zonesStageKey);
            ExecuteCachedZonesStage(
                build,
                runtimeCache,
                zonesStageKey,
                ComputeZonesFingerprint(doc, terrain, analysisMesh, build.PersistentHardConstraints, currentMeshFingerprint),
                () => BuildTerrainZones(doc, analysisMesh, terrain, build),
                () => $"{build.ZoneObjects.Count:N0} zone outputs",
                shouldCancel);
        }

        ThrowIfCancellationRequested(shouldCancel);
        runtimeCache.PruneUnused(usedStageKeys);
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
            shouldCancel);
    }

    private static TerrainAnalysisSummary? ExecuteCachedAnalysisStage(
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        ulong stageFingerprint,
        Func<TerrainAnalysisSummary?> action,
        Func<TerrainAnalysisSummary?, string?> detailFactory,
        Func<bool>? shouldCancel)
    {
        const string stageName = "Analysis";
        var timer = Stopwatch.StartNew();
        ThrowIfCancellationRequested(shouldCancel);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == stageFingerprint)
        {
            build.Diagnostics.AddRange(cachedEntry.Diagnostics);
            TerrainAnalysisSummary? cachedAnalysis = TerrainRuntimeCacheCloner.CloneAnalysis(cachedEntry.AnalysisOutput);
            build.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.AuxiliaryObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory(cachedAnalysis)));
            return cachedAnalysis;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int auxiliaryStart = build.AuxiliaryObjects.Count;
        TerrainAnalysisSummary? analysis = action();
        ThrowIfCancellationRequested(shouldCancel);
        timer.Stop();

        runtimeCache.StageEntries[stageKey] = new StageCacheEntry
        {
            StageName = stageName,
            PreResolutionFingerprint = stageFingerprint,
            ResolvedInputFingerprint = stageFingerprint,
            OutputFingerprint = stageFingerprint,
            AnalysisOutput = TerrainRuntimeCacheCloner.CloneAnalysis(analysis),
            AuxiliaryObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.AuxiliaryObjects.Skip(auxiliaryStart)),
            Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList()
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
            build.ZoneObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.ZoneObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
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
            Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList()
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
            build.MarkerObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.MarkerObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
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
            Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList()
        };

        build.RecordTiming(stageName, timer.Elapsed, detailFactory());
    }

    private static RhinoMesh? RestoreCachedMeshStage(TerrainBuildResult build, StageCacheEntry cachedEntry, out ulong outputFingerprint)
    {
        build.Diagnostics.AddRange(cachedEntry.Diagnostics);
        build.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.AuxiliaryObjects));
        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(cachedEntry.PersistentHardConstraints));
        outputFingerprint = cachedEntry.OutputFingerprint;
        return TerrainRuntimeCacheCloner.CloneMesh(cachedEntry.MeshOutput);
    }

    private static RhinoMesh? StoreMeshStageCache(
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        string stageName,
        ulong preResolutionFingerprint,
        ulong resolvedInputFingerprint,
        RhinoMesh? mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints,
        IEnumerable<GeneratedRhinoObject> auxiliaryObjects,
        IEnumerable<string> diagnostics,
        string? detail,
        Stopwatch timer,
        out ulong outputFingerprint,
        Func<bool>? shouldCancel = null)
    {
        timer.Stop();
        ThrowIfCancellationRequested(shouldCancel);
        outputFingerprint = ComputeMeshStageOutputFingerprint(mesh, persistentHardConstraints);
        runtimeCache.StageEntries[stageKey] = new StageCacheEntry
        {
            StageName = stageName,
            PreResolutionFingerprint = preResolutionFingerprint,
            ResolvedInputFingerprint = resolvedInputFingerprint,
            OutputFingerprint = outputFingerprint,
            MeshOutput = TerrainRuntimeCacheCloner.CloneMesh(mesh),
            AuxiliaryObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(auxiliaryObjects),
            PersistentHardConstraints = TerrainRuntimeCacheCloner.CloneConstraints(persistentHardConstraints),
            Diagnostics = diagnostics.ToList()
        };

        build.RecordTiming(stageName, timer.Elapsed, detail);
        return mesh;
    }

    private static StageCacheEntry CloneStageCacheEntry(
        StageCacheEntry source,
        ulong preResolutionFingerprint,
        ulong resolvedInputFingerprint)
    {
        return new StageCacheEntry
        {
            StageName = source.StageName,
            PreResolutionFingerprint = preResolutionFingerprint,
            ResolvedInputFingerprint = resolvedInputFingerprint,
            OutputFingerprint = source.OutputFingerprint,
            MeshOutput = source.MeshOutput,
            AnalysisOutput = source.AnalysisOutput,
            ZoneObjects = source.ZoneObjects,
            AuxiliaryObjects = source.AuxiliaryObjects,
            MarkerObjects = source.MarkerObjects,
            PersistentHardConstraints = source.PersistentHardConstraints,
            Diagnostics = source.Diagnostics,
            StairSurfaceCount = source.StairSurfaceCount,
            StairTreadDepthSummary = source.StairTreadDepthSummary,
            StairStepCountSummary = source.StairStepCountSummary
        };
    }

    private static RhinoMesh? WarnMissingMesh(TerrainBuildResult build, string modifierLabel)
    {
        build.Diagnostics.Add($"{modifierLabel} requires a terrain mesh generated earlier in the stack.");
        return null;
    }

    private static double GetTerrainTolerance(RhinoDoc doc, TerrainDefinition terrain)
    {
        return terrain.GlobalTolerance > 0
            ? terrain.GlobalTolerance
            : doc.ModelAbsoluteTolerance;
    }

    private static RhinoMesh? BuildTinMesh(
        RhinoDoc doc,
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
        ulong preResolutionFingerprint = ComputeTriangulatePreResolutionFingerprint(doc, terrain, modifier);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == preResolutionFingerprint)
        {
            RhinoMesh? cachedMesh = RestoreCachedMeshStage(build, cachedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, cachedMesh)));
            return cachedMesh;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        ThrowIfCancellationRequested(shouldCancel);
        var points = RhinoSourceResolver.ResolvePoints(doc, modifier.Points);
        var breaklineCurves = RhinoSourceResolver.ResolveCurves(doc, modifier.Breaklines);
        var contourCurves = RhinoSourceResolver.ResolveCurves(doc, modifier.Contours);
        var boundaryCurves = RhinoSourceResolver.ResolveCurves(doc, modifier.Boundary);

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
                out outputFingerprint);
        }

        double tolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : GetTerrainTolerance(doc, terrain);

        var spotXyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            spotXyz[i * 3] = points[i].X;
            spotXyz[i * 3 + 1] = points[i].Y;
            spotXyz[i * 3 + 2] = points[i].Z;
        }

        var persistentHardConstraints = CreateConstraintPolylines(
            breaklineCurves,
            tolerance,
            preserveInputElevation: true);

        ThrowIfCancellationRequested(shouldCancel);
        var polylines = CreateFlatPolylines(breaklineCurves, tolerance);
        polylines.AddRange(CreateFlatPolylines(contourCurves, tolerance));
        var boundaryPolylines = CreateBoundaryPolylines(boundaryCurves, tolerance);

        var breaklineData = BreaklineDiscretizer.Process(polylines, shouldCancel);
        var merged = PointCloudProcessor.Merge(spotXyz, points.Count, breaklineData, tolerance, shouldCancel);
        ThrowIfCancellationRequested(shouldCancel);
        ulong resolvedInputFingerprint = ComputeTriangulateResolvedInputFingerprint(
            terrain,
            modifier,
            tolerance,
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
                out outputFingerprint);
        }

        if (merged.InvalidsSkipped > 0)
            build.Diagnostics.Add($"{merged.InvalidsSkipped} invalid points skipped during triangulation.");
        if (merged.DuplicatesRemoved > 0)
            build.Diagnostics.Add($"{merged.DuplicatesRemoved} duplicate points merged during triangulation.");

        if (TryBuildValidatedTinMesh(
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            boundaryPolylines,
            tolerance,
            runtimeCache.TinEngine,
            out var exactMesh,
            out var exactMessage,
            shouldCancel))
        {
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(persistentHardConstraints);
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

        var cleanup = TinInputCleaner.Clean(merged, tolerance);
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
            tolerance,
            runtimeCache.TinEngine,
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
        RhinoDoc doc,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        AddGeometryModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        Func<bool>? shouldCancel = null)
    {
        ThrowIfCancellationRequested(shouldCancel);
        var points = RhinoSourceResolver.ResolvePoints(doc, modifier.Points);
        var breaklineCurves = RhinoSourceResolver.ResolveCurves(doc, modifier.Breaklines);
        var contourCurves = RhinoSourceResolver.ResolveCurves(doc, modifier.Contours);
        var boundaryCurves = RhinoSourceResolver.ResolveCurves(doc, modifier.Boundary);

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

        double tolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : GetTerrainTolerance(doc, terrain);

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
            tolerance,
            preserveInputElevation: true);
        var persistentHardConstraints = CombineConstraints(build.PersistentHardConstraints, newHardConstraints);

        ThrowIfCancellationRequested(shouldCancel);
        var polylines = CreateFlatPolylines(build.PersistentHardConstraints);
        polylines.AddRange(CreateFlatPolylines(breaklineCurves, tolerance));
        polylines.AddRange(CreateFlatPolylines(contourCurves, tolerance));

        var boundaryPolylines = CombineBoundaryPolylines(
            CreateBoundaryPolylines(mesh, tolerance),
            CreateBoundaryPolylines(boundaryCurves, tolerance));

        var breaklineData = BreaklineDiscretizer.Process(polylines, shouldCancel);
        var merged = PointCloudProcessor.Merge(spotXyz, existingPointCount + points.Count, breaklineData, tolerance, shouldCancel);
        if (merged.VertexCount < 3)
        {
            build.Diagnostics.Add("Add Geometry needs at least three unique points after deduplication.");
            return mesh;
        }

        if (merged.InvalidsSkipped > 0)
            build.Diagnostics.Add($"{merged.InvalidsSkipped} invalid points skipped during add geometry.");
        if (merged.DuplicatesRemoved > 0)
            build.Diagnostics.Add($"{merged.DuplicatesRemoved} duplicate points merged during add geometry.");

        if (TryBuildValidatedTinMesh(
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            boundaryPolylines,
            tolerance,
            runtimeCache.TinEngine,
            out var exactMesh,
            out var exactMessage,
            shouldCancel))
        {
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(persistentHardConstraints);
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

        var cleanup = TinInputCleaner.Clean(merged, tolerance);
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
            tolerance,
            runtimeCache.TinEngine,
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
        TinEngine engine,
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
            maxBoundaryEdgeLength: 0,
            shouldCancel: shouldCancel);
        ThrowIfCancellationRequested(shouldCancel);

        if (!string.IsNullOrWhiteSpace(prepared.WarningMessage))
            message = AppendBuildMessage(message, prepared.WarningMessage);
        if (!string.IsNullOrWhiteSpace(prepared.InfoMessage))
            message = AppendBuildMessage(message, prepared.InfoMessage);

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

    private static RhinoMesh ApplyRemesh(RhinoDoc doc, TerrainDefinition terrain, RhinoMesh mesh, RemeshModifierDefinition modifier, TerrainBuildResult build)
    {
        double tolerance = GetTerrainTolerance(doc, terrain);
        var localConstraints = CreateConstraintPolylines(
            RhinoSourceResolver.ResolveCurves(doc, modifier.Constraints),
            tolerance,
            preserveInputElevation: false);

        if (modifier.EdgeLength <= 0 && modifier.MaxArea <= 0 && modifier.MinAngle <= 0 && localConstraints.Count == 0)
            return mesh.DuplicateMesh();

        var constraints = CombineConstraints(build.PersistentHardConstraints, localConstraints);
        var remeshed = RebuildMeshWithConstraints(
            doc,
            terrain,
            mesh,
            constraints,
            modifier.EdgeLength,
            modifier.MaxArea,
            modifier.MinAngle,
            "Remesh",
            build);

        if (!ReferenceEquals(remeshed, mesh) && localConstraints.Count > 0)
            build.PersistentHardConstraints.AddRange(localConstraints);

        return remeshed;
    }

    private static RhinoMesh ApplySmooth(RhinoDoc doc, TerrainDefinition terrain, RhinoMesh mesh, SmoothModifierDefinition modifier, TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for smoothing.");
            return mesh;
        }

        double tolerance = GetTerrainTolerance(doc, terrain);
        double effectiveStrength = Math.Clamp(modifier.Strength, 0.0, 1.0);
        var boundaries = new List<(double[] xyVerts, int vertCount, double strength)>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.Boundaries))
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

            boundaries.Add((xyVerts, count, effectiveStrength));
        }

        var breaklines = new List<(double[] xyPts, int ptCount)>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.Breaklines))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            int count = polyline.Count;
            if (curve.IsClosed && polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;

            var xyPts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xyPts[i * 2] = polyline[i].X;
                xyPts[i * 2 + 1] = polyline[i].Y;
            }

            breaklines.Add((xyPts, count));
        }

        var smoothed = MeshSmoother.Smooth(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            boundaries.ToArray(),
            effectiveStrength,
            breaklines.ToArray(),
            Math.Clamp(modifier.BreaklineFixity, 0.0, 1.0),
            tolerance,
            Math.Max(1, modifier.Iterations));

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

    private static void BuildTerrainZones(RhinoDoc doc, RhinoMesh mesh, TerrainDefinition terrain, TerrainBuildResult build)
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
        double tolerance = GetTerrainTolerance(doc, terrain);
        var resolveTimer = Stopwatch.StartNew();
        for (int zoneIndex = 0; zoneIndex < terrain.Zones.Count; zoneIndex++)
        {
            var zone = terrain.Zones[zoneIndex];
            if (!zone.IsEnabled)
                continue;

            var zoneEntries = ResolveZoneBoundaries(doc, zone, zoneIndex, tolerance);
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
        var result = MeshAreaSplitter.Split(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            boundaries,
            build.PersistentHardConstraints,
            tolerance,
            0,
            0,
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
            $"{entries.Count} boundaries over {mesh.Faces.Count:N0} source faces; resolve {resolveTimer.Elapsed.TotalSeconds:0.##} s, split {splitTimer.Elapsed.TotalSeconds:0.##} s, output {outputTimer.Elapsed.TotalSeconds:0.##} s",
            StageTimingDiagnosticThresholdMs);
    }

    private static RhinoMesh ApplyRetainingWalls(RhinoDoc doc, TerrainDefinition terrain, RhinoMesh mesh, RetainingWallModifierDefinition modifier, TerrainBuildResult build)
    {
        double wallTolerance = Math.Max(GetTerrainTolerance(doc, terrain), modifier.Tolerance);
        var wallCurves = RhinoSourceResolver.ResolveCurves(doc, modifier.WallCurves);
        if (wallCurves.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no curve inputs.");
            return mesh;
        }

        var plan = RhinoRetainingWallPlanner.Plan(wallCurves, wallTolerance);
        foreach (var entry in plan.Report)
            build.Diagnostics.Add(entry.ToString());

        var wallConstraints = new List<SurfaceRemesher.ConstraintPolyline>(plan.Walls.Count * 2);
        foreach (var wall in plan.Walls)
        {
            if (!IsWallStripUsable(wall.Strip, doc.ModelAbsoluteTolerance, out var stripMessage))
            {
                build.Diagnostics.Add($"Retaining wall pair ({wall.CurveA}, {wall.CurveB}) skipped: {stripMessage}");
                continue;
            }

            if (wall.Brep != null)
            {
                build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                {
                    Geometry = wall.Brep,
                    Name = $"Wall {wall.CurveA}-{wall.CurveB}",
                    LayerPath = modifier.OutputLayerPath
                });
            }

            AddWallConstraintCurves(wallConstraints, wall.Strip);
        }

        if (wallConstraints.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall has no usable wall pairs to insert as breaklines.");
            return mesh;
        }

        var remeshed = RebuildMeshWithConstraints(
            doc,
            terrain,
            mesh,
            CombineConstraints(build.PersistentHardConstraints, wallConstraints),
            0.0,
            0.0,
            0.0,
            "Retaining Wall",
            build);

        if (!ReferenceEquals(remeshed, mesh))
            build.PersistentHardConstraints.AddRange(wallConstraints);

        return remeshed;
    }

    private static RhinoMesh? BuildGradePadMesh(
        RhinoDoc doc,
        TerrainDefinition terrain,
        GradePadModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        RhinoMesh? mesh,
        ulong upstreamFingerprint,
        out ulong outputFingerprint,
        Func<bool>? shouldCancel = null)
    {
        const string stageName = "Grade Pad";
        string topologyStageKey = CreateGradePadTopologyStageKey(stageKey);
        var timer = Stopwatch.StartNew();
        ulong preResolutionFingerprint = ComputeModifierStageFingerprint(doc, terrain, modifier, upstreamFingerprint);

        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == preResolutionFingerprint)
        {
            RhinoMesh? cachedMesh = RestoreCachedMeshStage(build, cachedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, cachedMesh)));
            return cachedMesh;
        }

        int diagnosticsStart = build.Diagnostics.Count;
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
                out outputFingerprint);
        }

        double tolerance = GetTerrainTolerance(doc, terrain);
        ResolvedGradePadInputs resolvedInputs = ResolveGradePadInputs(doc, modifier, tolerance);
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
                out outputFingerprint);
        }

        ulong topologyFingerprint = ComputeGradePadTopologyFingerprint(
            upstreamFingerprint,
            tolerance,
            modifier,
            resolvedInputs.Pads,
            resolvedInputs.Locks);

        PadTopologyCacheEntry? topologyEntry;
        var topologyTimer = Stopwatch.StartNew();
        if (runtimeCache.PadTopologyEntries.TryGetValue(topologyStageKey, out var cachedTopologyEntry) &&
            cachedTopologyEntry.Fingerprint == topologyFingerprint)
        {
            topologyEntry = TerrainRuntimeCacheCloner.ClonePadTopologyEntry(cachedTopologyEntry);
            build.Diagnostics.AddRange(topologyEntry.Diagnostics);
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
            bool topologySucceeded = PadGrader.TryTriangulateTopology(
                vertices,
                mesh.Vertices.Count,
                faces,
                mesh.Faces.Count,
                resolvedInputs.Pads,
                resolvedInputs.Locks.Length == 0 ? null : resolvedInputs.Locks,
                modifier.MaxArea,
                modifier.MinAngle,
                out var topologyVertices,
                out var topologyVertexCount,
                out var topologyFaces,
                out var topologyFaceCount,
                out var topologyWarning);

            topologyTimer.Stop();
            if (!topologySucceeded)
            {
                build.RecordTiming("Grade Pad Topology", topologyTimer.Elapsed, topologyWarning, StageTimingDiagnosticThresholdMs);
                build.Diagnostics.Add(string.IsNullOrWhiteSpace(topologyWarning)
                    ? "Grade Pad optimized topology path failed. Falling back to full grade."
                    : $"Grade Pad optimized topology path failed ({topologyWarning}). Falling back to full grade.");

                RhinoMesh fallbackMesh = ApplyGradePadLegacy(mesh, vertices, faces, modifier, resolvedInputs, tolerance, build);
                return StoreMeshStageCache(
                    build,
                    runtimeCache,
                    stageKey,
                    stageName,
                    preResolutionFingerprint,
                    preResolutionFingerprint,
                    fallbackMesh,
                    build.PersistentHardConstraints,
                    Array.Empty<GeneratedRhinoObject>(),
                    build.Diagnostics.Skip(diagnosticsStart),
                    DescribeModifierMeshResult(modifier.Label, fallbackMesh),
                    timer,
                    out outputFingerprint);
            }

            var topologyDiagnostics = new List<string>();
            if (!string.IsNullOrWhiteSpace(topologyWarning))
            {
                topologyDiagnostics.Add(topologyWarning!);
                build.Diagnostics.Add(topologyWarning!);
            }

            topologyEntry = new PadTopologyCacheEntry
            {
                Fingerprint = topologyFingerprint,
                OutputFingerprint = ComputePadTopologyOutputFingerprint(topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount),
                Vertices = topologyVertices,
                VertexCount = topologyVertexCount,
                Faces = topologyFaces,
                FaceCount = topologyFaceCount,
                Diagnostics = topologyDiagnostics
            };
            runtimeCache.PadTopologyEntries[topologyStageKey] = TerrainRuntimeCacheCloner.ClonePadTopologyEntry(topologyEntry);
            build.RecordTiming(
                "Grade Pad Topology",
                topologyTimer.Elapsed,
                $"{topologyVertexCount:N0} verts, {topologyFaceCount:N0} faces",
                StageTimingDiagnosticThresholdMs);
        }

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
        double[] gradedVertices = PadGrader.ApplyGradingZ(topologyEntry.Vertices, topologyEntry.VertexCount, resolvedInputs.Pads);
        filterTimer.Stop();
        ThrowIfCancellationRequested(shouldCancel);
        build.RecordTiming(
            "Grade Pad Filter",
            filterTimer.Elapsed,
            $"{topologyEntry.VertexCount:N0} verts",
            StageTimingDiagnosticThresholdMs);

        var combinedConstraints = CombineConstraints(build.PersistentHardConstraints, resolvedInputs.Constraints);
        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(combinedConstraints);

        RhinoMesh resultMesh = CleanTinyFaces(
            RhinoGeometryConversions.BuildMesh(gradedVertices, topologyEntry.VertexCount, topologyEntry.Faces, topologyEntry.FaceCount),
            tolerance,
            "Grade Pad",
            build);

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
            out outputFingerprint);
    }

    private static RhinoMesh ApplyGradePadLegacy(
        RhinoMesh mesh,
        double[] vertices,
        int[] faces,
        GradePadModifierDefinition modifier,
        ResolvedGradePadInputs resolvedInputs,
        double tolerance,
        TerrainBuildResult build)
    {
        var result = PadGrader.Grade(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            resolvedInputs.Pads,
            resolvedInputs.Locks.Length == 0 ? null : resolvedInputs.Locks,
            modifier.MaxArea,
            modifier.MinAngle,
            out var warning);

        if (result == null)
        {
            build.Diagnostics.Add(warning ?? "Grade Pad failed.");
            return mesh;
        }

        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);

        var combinedConstraints = CombineConstraints(build.PersistentHardConstraints, resolvedInputs.Constraints);
        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(combinedConstraints);

        return CleanTinyFaces(
            RhinoGeometryConversions.BuildMesh(result.Vertices, result.VertexCount, result.Faces, result.FaceCount),
            tolerance,
            "Grade Pad",
            build);
    }

    private static ResolvedGradePadInputs ResolveGradePadInputs(
        RhinoDoc doc,
        GradePadModifierDefinition modifier,
        double tolerance)
    {
        var pads = new List<PadGrader.PadBoundary>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.Boundaries))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out var polyline))
                continue;

            int count = polyline.Count;
            if (count > 1 && polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;
            if (count < 3)
                continue;

            var xyVerts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xyVerts[i * 2] = polyline[i].X;
                xyVerts[i * 2 + 1] = polyline[i].Y;
            }

            var bbox = curve.GetBoundingBox(false);
            double targetZ = (bbox.Min.Z + bbox.Max.Z) * 0.5;
            pads.Add(new PadGrader.PadBoundary(xyVerts, count, targetZ, modifier.SlopeAngle, modifier.MaxDistance));
        }

        var locks = new List<PadGrader.LockCurve>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.LockCurves))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
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

        return new ResolvedGradePadInputs
        {
            Pads = pads.ToArray(),
            Locks = locks.ToArray(),
            Constraints = CreateGradePadConstraints(pads, locks)
        };
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
            var points = new double[pad.VertexCount * 3];
            for (int i = 0; i < pad.VertexCount; i++)
            {
                points[i * 3] = pad.XyVertices[i * 2];
                points[i * 3 + 1] = pad.XyVertices[i * 2 + 1];
                points[i * 3 + 2] = pad.TargetZ;
            }

            constraints.Add(new SurfaceRemesher.ConstraintPolyline(points, pad.VertexCount, IsClosed: true, PreserveInputElevation: false));
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

    private static RhinoMesh ApplyGradePath(RhinoDoc doc, TerrainDefinition terrain, RhinoMesh mesh, GradePathModifierDefinition modifier, TerrainBuildResult build)
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

        double tolerance = GetTerrainTolerance(doc, terrain);
        ResolvedGradePathInputs resolvedInputs = ResolveGradePathInputs(doc, vertices, mesh.Vertices.Count, faces, mesh.Faces.Count, modifier, tolerance);
        if (resolvedInputs.Paths.Length == 0)
        {
            build.Diagnostics.Add("Grade Path has no valid paths.");
            return mesh;
        }

        var combinedConstraints = CombineConstraints(build.PersistentHardConstraints, resolvedInputs.Constraints);
        if (CanUseLegacyPathTriangulation(mesh))
        {
            RhinoMesh? legacyMesh = ApplyGradePathLegacy(mesh, vertices, faces, resolvedInputs, tolerance, build);
            if (legacyMesh != null)
            {
                bool apronTopologyAdded =
                    resolvedInputs.Constraints.Length == 0 ||
                    legacyMesh.Vertices.Count > mesh.Vertices.Count ||
                    legacyMesh.Faces.Count > mesh.Faces.Count;

                if (apronTopologyAdded)
                {
                    build.PersistentHardConstraints.Clear();
                    build.PersistentHardConstraints.AddRange(combinedConstraints);
                    return legacyMesh;
                }

                build.Diagnostics.Add("Grade Path legacy insertion did not add apron topology; retrying with remesh topology mode.");
            }
        }
        else
        {
            build.Diagnostics.Add(
                $"Grade Path used remesh topology mode on a dense upstream mesh ({mesh.Vertices.Count:N0} verts, {mesh.Faces.Count:N0} faces) to avoid a full point-insertion re-triangulation stall.");
        }

        RhinoMesh topologyMesh = mesh;
        if (resolvedInputs.Constraints.Length > 0)
        {
            topologyMesh = RebuildMeshWithConstraints(
                doc,
                terrain,
                mesh,
                combinedConstraints,
                resolvedInputs.SuggestedEdgeLength,
                0,
                0,
                "Grade Path",
                build);

            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(combinedConstraints);
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(topologyMesh, out var topologyVertices, out var topologyFaces, out errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade path topology.");
            return topologyMesh;
        }

        double[] gradedVertices = PathGrader.ApplyGradingZ(topologyVertices, topologyMesh.Vertices.Count, resolvedInputs.Paths);

        return CleanTinyFaces(
            RhinoGeometryConversions.BuildMesh(gradedVertices, topologyMesh.Vertices.Count, topologyFaces, topologyMesh.Faces.Count),
            tolerance,
            "Grade Path",
            build);
    }

    private static bool CanUseLegacyPathTriangulation(RhinoMesh mesh)
    {
        return mesh.Vertices.Count <= MaxLegacyPathTriangulationVertices &&
               mesh.Faces.Count <= MaxLegacyPathTriangulationFaces;
    }

    private static RhinoMesh? ApplyGradePathLegacy(
        RhinoMesh mesh,
        double[] vertices,
        int[] faces,
        ResolvedGradePathInputs resolvedInputs,
        double tolerance,
        TerrainBuildResult build)
    {
        var result = PathGrader.Grade(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            resolvedInputs.Paths,
            out var warning);

        if (result == null)
        {
            build.Diagnostics.Add(warning ?? "Grade Path failed.");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);

        return CleanTinyFaces(
            RhinoGeometryConversions.BuildMesh(result.Vertices, result.VertexCount, result.Faces, result.FaceCount),
            tolerance,
            "Grade Path",
            build);
    }

    private static ResolvedGradePathInputs ResolveGradePathInputs(
        RhinoDoc doc,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        GradePathModifierDefinition modifier,
        double tolerance)
    {
        var paths = new List<PathGrader.PathDefinition>();
        foreach (var curve in RhinoSourceResolver.ResolveCurves(doc, modifier.Paths))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

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
            : PathGrader.CreateConstraints(vertices, vertexCount, faces, faceCount, pathArray, tolerance);

        return new ResolvedGradePathInputs
        {
            Paths = pathArray,
            Constraints = constraintSet.Constraints,
            SuggestedEdgeLength = constraintSet.SuggestedEdgeLength
        };
    }

    private static RhinoMesh ApplyInSituStair(RhinoDoc doc, TerrainDefinition terrain, RhinoMesh mesh, InSituStairModifierDefinition modifier, TerrainBuildResult build)
    {
        modifier.ComputedSurfaceCount = null;
        modifier.ComputedTreadDepthSummary = null;
        modifier.ComputedStepCountSummary = null;

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

        var referenceMeshes = RhinoSourceResolver.ResolveMeshes(doc, modifier.ReferenceSurface);
        if (referenceMeshes.Count == 0)
        {
            build.Diagnostics.Add("In-Situ Stair has no valid reference surface.");
            return mesh;
        }

        if (!InSituStairReferenceBuilder.TryBuild(
                referenceMeshes,
                modifier.RiserHeight,
                modifier.SlopeAngle,
                modifier.MaxDistance,
                out var stairBuild,
                out errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "In-Situ Stair could not interpret the reference surface.");
            return mesh;
        }

        double tolerance = GetTerrainTolerance(doc, terrain);

        modifier.ComputedSurfaceCount = stairBuild!.SurfaceCount;
        modifier.ComputedTreadDepthSummary = stairBuild.TreadDepthSummary;
        modifier.ComputedStepCountSummary = stairBuild.StepCountSummary;

        double[] currentVertices = vertices;
        int currentVertexCount = mesh.Vertices.Count;
        int[] currentFaces = faces;
        int currentFaceCount = mesh.Faces.Count;
        var gradingWarnings = new List<string>();

        foreach (var stairReference in stairBuild.References)
        {
            var result = SurfaceStripGrader.Grade(
                currentVertices,
                currentVertexCount,
                currentFaces,
                currentFaceCount,
                stairReference.SupportSurface,
                out var gradingWarning);

            if (result == null)
            {
                build.Diagnostics.Add(gradingWarning ?? "In-Situ Stair grading failed.");
                return mesh;
            }

            currentVertices = result.Vertices;
            currentVertexCount = result.VertexCount;
            currentFaces = result.Faces;
            currentFaceCount = result.FaceCount;
            if (!string.IsNullOrWhiteSpace(gradingWarning))
                gradingWarnings.Add(gradingWarning);
        }

        foreach (var stairReference in stairBuild.References)
        {
            foreach (var stairBrep in stairReference.StairBreps)
            {
                build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                {
                    Geometry = stairBrep,
                    Name = "Stair",
                    LayerPath = terrain.AuxiliaryLayerPath
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

        build.Diagnostics.Add(stairBuild.StatusSummary);
        foreach (var warning in stairBuild.Warnings)
            build.Diagnostics.Add(warning);
        foreach (var gradingWarning in gradingWarnings)
            build.Diagnostics.Add(gradingWarning);

        return CleanTinyFaces(
            RhinoGeometryConversions.BuildMesh(currentVertices, currentVertexCount, currentFaces, currentFaceCount),
            tolerance,
            "In-Situ Stair",
            build);
    }

    private static List<ZoneBoundaryEntry> ResolveZoneBoundaries(RhinoDoc doc, CollageZoneDefinition zone, int zoneOrder, double tolerance)
    {
        var result = new List<ZoneBoundaryEntry>();
        int sourceOrder = 0;

        string? inputLayerPath = zone.Boundaries.LayerPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        foreach (var obj in RhinoSourceResolver.ResolveObjects(doc, zone.Boundaries))
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
                            InputLayerPath = inputLayerPath ?? GetObjectLayerPath(doc, obj)
                        });
                    }

                    break;
                case Brep brep:
                    AppendPlanarBrepBoundaries(result, zone, zoneOrder, ref sourceOrder, brep, tolerance, inputLayerPath ?? GetObjectLayerPath(doc, obj));
                    break;
                case Extrusion extrusion:
                    var extrusionBrep = extrusion.ToBrep();
                    if (extrusionBrep != null)
                        AppendPlanarBrepBoundaries(result, zone, zoneOrder, ref sourceOrder, extrusionBrep, tolerance, inputLayerPath ?? GetObjectLayerPath(doc, obj));
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

    private static string? GetObjectLayerPath(RhinoDoc doc, global::Rhino.DocObjects.RhinoObject obj)
    {
        int layerIndex = obj.Attributes.LayerIndex;
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return null;

        return doc.Layers[layerIndex].FullPath;
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

    private static void BuildMarkers(RhinoDoc doc, TerrainDefinition terrain, RhinoMesh mesh, TerrainBuildResult build, Func<bool>? shouldCancel)
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
            var samplePoints = RhinoSourceResolver.ResolveMarkerSamplePoints(doc, marker.Sources);
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

    private static TerrainAnalysisSummary? BuildAnalysis(
        RhinoDoc doc,
        TerrainDefinition terrain,
        RhinoMesh fallbackBaseMesh,
        RhinoMesh currentMesh,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        ThrowIfCancellationRequested(shouldCancel);
        if (!RhinoGeometryConversions.TryExtractMeshData(currentMesh, out var currentVertices, out var currentFaces, out _))
            return null;

        var palette = SlopePreviewPaletteCatalog.Resolve(terrain.SlopePalettePreset);
        var slope = SlopeAnalyzer.Analyze(
            currentVertices,
            currentMesh.Vertices.Count,
            currentFaces,
            currentMesh.Faces.Count,
            SlopeAnalyzer.SlopeUnit.Percent,
            0.0,
            0.0,
            palette.Stops);

        RhinoMesh baseMesh = ResolveEarthworkReferenceMesh(doc, terrain) ?? fallbackBaseMesh;
        var boundaries = RhinoSourceResolver.ResolveCurves(doc, terrain.EarthworkBoundary);
        EstimateEarthworks(baseMesh, currentMesh, boundaries, out double cutVolume, out double fillVolume, shouldCancel);

        return new TerrainAnalysisSummary
        {
            SurfaceArea = AreaMassProperties.Compute(currentMesh)?.Area ?? 0.0,
            SlopeMinPercent = slope.Min,
            SlopeMaxPercent = slope.Max,
            SlopeAveragePercent = slope.Average,
            SlopeDisplayLowPercent = slope.ColorLow,
            SlopeDisplayHighPercent = slope.ColorHigh,
            CutVolume = cutVolume,
            FillVolume = fillVolume,
            NetVolume = cutVolume - fillVolume,
            EarthworkIsEstimated = !terrain.EarthworkReference.HasReferences
        };
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

    private static void EstimateEarthworks(RhinoMesh baseMesh, RhinoMesh currentMesh, IReadOnlyList<Curve> boundaries, out double cutVolume, out double fillVolume, Func<bool>? shouldCancel)
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

            if (!IsInsideBoundaries(centroid, boundaries))
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

    private static RhinoMesh? ResolveEarthworkReferenceMesh(RhinoDoc doc, TerrainDefinition terrain)
    {
        var meshes = RhinoSourceResolver.ResolveMeshes(doc, terrain.EarthworkReference);
        if (meshes.Count == 0)
            return null;

        if (meshes.Count == 1)
            return meshes[0];

        var combined = new RhinoMesh();
        foreach (var mesh in meshes)
            combined.Append(mesh);

        combined.Normals.ComputeNormals();
        combined.UnifyNormals();
        combined.Compact();
        return combined;
    }

    private static void ThrowIfCancellationRequested(Func<bool>? shouldCancel)
    {
        if (shouldCancel?.Invoke() == true)
            throw new OperationCanceledException("Terrain rebuild cancelled.");
    }

    private static bool IsInsideBoundaries(Point3d point, IReadOnlyList<Curve> boundaries)
    {
        if (boundaries.Count == 0)
            return true;

        foreach (var curve in boundaries)
        {
            var containment = curve.Contains(new Point3d(point.X, point.Y, curve.PointAtStart.Z), Plane.WorldXY, RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6);
            if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                return true;
        }

        return false;
    }

    private static bool IsWallStripUsable(RetainingWallMeshGrader.WallStripDefinition strip, double tolerance, out string message)
    {
        message = string.Empty;
        if (strip.StationCount < 2)
        {
            message = "too few stations.";
            return false;
        }

        double minWidth = double.MaxValue;
        for (int i = 0; i < strip.StationCount; i++)
        {
            double dx = strip.TopXy[i * 2] - strip.ToeXy[i * 2];
            double dy = strip.TopXy[i * 2 + 1] - strip.ToeXy[i * 2 + 1];
            minWidth = Math.Min(minWidth, Math.Sqrt(dx * dx + dy * dy));
        }

        if (minWidth < Math.Max(tolerance * 2.0, 0.05))
        {
            message = "strip width collapses too tightly.";
            return false;
        }

        return true;
    }

    private static void AddWallConstraintCurves(List<SurfaceRemesher.ConstraintPolyline> curves, RetainingWallMeshGrader.WallStripDefinition strip)
    {
        Polyline? toeCurve = CreateWallRailCurve(strip.ToeXy, strip.ToeZ, strip.StationCount);
        if (toeCurve != null)
            curves.Add(ToConstraintPolyline(toeCurve, isClosed: false, preserveInputElevation: true));

        Polyline? topCurve = CreateWallRailCurve(strip.TopXy, strip.TopZ, strip.StationCount);
        if (topCurve != null)
            curves.Add(ToConstraintPolyline(topCurve, isClosed: false, preserveInputElevation: true));
    }

    private static Polyline? CreateWallRailCurve(double[] xy, double[] z, int count)
    {
        if (count < 2)
            return null;

        var points = new Point3d[count];
        for (int i = 0; i < count; i++)
            points[i] = new Point3d(xy[i * 2], xy[i * 2 + 1], z[i]);

        return new Polyline(points);
    }

    private static RhinoMesh RebuildMeshWithConstraints(
        RhinoDoc doc,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double requestedEdgeLength,
        double maxArea,
        double minAngle,
        string label,
        TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var originalVertices, out var originalFaces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? $"Could not extract mesh data for {label.ToLowerInvariant()}.");
            return mesh;
        }

        double tolerance = GetTerrainTolerance(doc, terrain);
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
                ProtectSharpEdges = true
            });

        if (!remeshResult.Success)
        {
            build.Diagnostics.Add(remeshResult.Warning ?? $"{label} triangulation failed.");
            return mesh;
        }

        if (remeshResult.AddedProtectedVertices > 0)
            build.Diagnostics.Add($"{label} added {remeshResult.AddedProtectedVertices} protected-edge vertices before triangulation.");

        if (remeshResult.UsedReducedSeedFallback)
            build.Diagnostics.Add($"{label} retried from boundary and hard-constraint seeds because carried mesh vertices prevented refinement.");

        if (!string.IsNullOrWhiteSpace(remeshResult.Warning))
            build.Diagnostics.Add(remeshResult.Warning);

        return CleanTinyFaces(BuildMeshFromArrays(remeshResult.Vertices, remeshResult.Faces), tolerance, label, build);
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
        bool preserveInputElevation)
    {
        var result = new List<SurfaceRemesher.ConstraintPolyline>();
        foreach (var curve in curves)
        {
            if (curve == null)
                continue;

            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            result.Add(ToConstraintPolyline(polyline, curve.IsClosed, preserveInputElevation));
        }

        return result;
    }

    private static List<double[]> CreateFlatPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        var result = new List<double[]>();
        foreach (var curve in curves)
        {
            if (curve == null)
                continue;

            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            result.Add(ToFlatPolyline(polyline));
        }

        return result;
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
        var flat = new double[polyline.Count * 3];
        for (int i = 0; i < polyline.Count; i++)
        {
            flat[i * 3] = polyline[i].X;
            flat[i * 3 + 1] = polyline[i].Y;
            flat[i * 3 + 2] = polyline[i].Z;
        }

        return flat;
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

    private static string CreateModifierStageKey(int modifierIndex, ModifierDefinition modifier)
    {
        return $"modifier:{modifierIndex}:{modifier.GetType().Name}:{modifier.Id:N}";
    }

    private static string CreateGradePadTopologyStageKey(string stageKey)
    {
        return $"{stageKey}:topology";
    }

    private static string? AppendCacheHitDetail(string? detail)
    {
        return string.IsNullOrWhiteSpace(detail)
            ? "cache hit"
            : $"{detail}; cache hit";
    }

    private static ulong ComputeModifierStageFingerprint(
        RhinoDoc doc,
        TerrainDefinition terrain,
        ModifierDefinition modifier,
        ulong upstreamFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add(upstreamFingerprint);
        builder.Add(modifier.GetType().FullName);
        builder.Add(doc.ModelAbsoluteTolerance);
        builder.Add(terrain.GlobalTolerance);
        AddSerializedFingerprint(ref builder, modifier, modifier.GetType());

        int sourceIndex = 0;
        foreach (var sourceSet in modifier.EnumerateSourceSets())
        {
            builder.Add(sourceIndex++);
            builder.Add(ComputeSourceSetFingerprint(doc, sourceSet));
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeTriangulatePreResolutionFingerprint(
        RhinoDoc doc,
        TerrainDefinition terrain,
        TriangulateModifierDefinition modifier)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Triangulate");
        builder.Add(doc.ModelAbsoluteTolerance);
        builder.Add(terrain.GlobalTolerance);
        AddSerializedFingerprint(ref builder, modifier, modifier.GetType());
        builder.Add(ComputeSourceSetFingerprint(doc, modifier.Points));
        builder.Add(ComputeSourceSetFingerprint(doc, modifier.Breaklines));
        builder.Add(ComputeSourceSetFingerprint(doc, modifier.Contours));
        builder.Add(ComputeSourceSetFingerprint(doc, modifier.Boundary));
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
        IReadOnlyList<PadGrader.LockCurve> locks)
    {
        var builder = new FingerprintBuilder();
        builder.Add("GradePadTopology");
        builder.Add(upstreamFingerprint);
        builder.Add(tolerance);
        builder.Add(modifier.MaxArea);
        builder.Add(modifier.MinAngle);
        builder.Add(pads.Count);
        foreach (var pad in pads)
        {
            builder.Add(pad.VertexCount);
            AddDoubleArrayFingerprint(ref builder, pad.XyVertices);
        }

        builder.Add(locks.Count);
        foreach (var lockCurve in locks)
        {
            builder.Add(lockCurve.VertexCount);
            AddDoubleArrayFingerprint(ref builder, lockCurve.XyVertices);
        }

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
            builder.Add(pad.TargetZ);
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeAnalysisFingerprint(
        RhinoDoc doc,
        TerrainDefinition terrain,
        RhinoMesh baseMesh,
        RhinoMesh currentMesh,
        ulong baseMeshFingerprint,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Analysis");
        builder.Add(doc.ModelAbsoluteTolerance);
        builder.Add(baseMeshFingerprint != 0 ? baseMeshFingerprint : ComputeMeshFingerprint(baseMesh));
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(currentMesh));
        builder.Add(ComputeSourceSetFingerprint(doc, terrain.EarthworkReference));
        builder.Add(ComputeSourceSetFingerprint(doc, terrain.EarthworkBoundary));
        return builder.ToUInt64();
    }

    private static ulong ComputeZonesFingerprint(
        RhinoDoc doc,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Zones");
        builder.Add(doc.ModelAbsoluteTolerance);
        builder.Add(GetTerrainTolerance(doc, terrain));
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(mesh));
        builder.Add(ComputeConstraintsFingerprint(persistentHardConstraints));

        foreach (var zone in terrain.Zones.Where(zone => zone.IsEnabled))
        {
            AddSerializedFingerprint(ref builder, zone, zone.GetType());
            builder.Add(ComputeSourceSetFingerprint(doc, zone.Boundaries));
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeMarkersFingerprint(
        RhinoDoc doc,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Markers");
        builder.Add(doc.ModelAbsoluteTolerance);
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(mesh));

        foreach (var marker in terrain.Markers.Where(marker => marker.IsEnabled))
        {
            AddSerializedFingerprint(ref builder, marker, marker.GetType());
            builder.Add(ComputeSourceSetFingerprint(doc, marker.Sources));
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeSourceSetFingerprint(RhinoDoc doc, SourceReferenceSet sourceSet)
    {
        var builder = new FingerprintBuilder();

        foreach (Guid objectId in sourceSet.ObjectIds.OrderBy(id => id))
            builder.Add(objectId);

        foreach (string layerPath in sourceSet.LayerPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            builder.Add(layerPath);

        var objects = RhinoSourceResolver.ResolveObjects(doc, sourceSet)
            .OrderBy(obj => obj.Id)
            .ToList();
        builder.Add(objects.Count);
        foreach (var obj in objects)
        {
            builder.Add(obj.Id);
            builder.Add((int)obj.ObjectType);
            builder.Add(obj.Attributes.LayerIndex);
            builder.Add(GetObjectLayerPath(doc, obj));
            builder.Add(obj.Geometry?.DataCRC(0u) ?? 0u);
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeMeshStageOutputFingerprint(
        RhinoMesh? mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints)
    {
        var builder = new FingerprintBuilder();
        builder.Add(ComputeMeshFingerprint(mesh));
        builder.Add(ComputeConstraintsFingerprint(persistentHardConstraints));
        return builder.ToUInt64();
    }

    private static ulong ComputePadTopologyOutputFingerprint(
        IReadOnlyList<double> vertices,
        int vertexCount,
        IReadOnlyList<int> faces,
        int faceCount)
    {
        var builder = new FingerprintBuilder();
        builder.Add("GradePadTopologyOutput");
        builder.Add(vertexCount);
        AddDoubleArrayFingerprint(ref builder, vertices);
        builder.Add(faceCount);
        AddIntArrayFingerprint(ref builder, faces);
        return builder.ToUInt64();
    }

    private static ulong ComputeMeshFingerprint(RhinoMesh? mesh)
    {
        return mesh == null ? 0UL : mesh.DataCRC(0u);
    }

    private static ulong ComputeConstraintsFingerprint(IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        var builder = new FingerprintBuilder();
        builder.Add(constraints.Count);
        foreach (var constraint in constraints)
        {
            builder.Add(constraint.PointCount);
            builder.Add(constraint.IsClosed);
            builder.Add(constraint.PreserveInputElevation);
            AddDoubleArrayFingerprint(ref builder, constraint.Points);
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeBoundaryPolylinesFingerprint(IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> boundaries)
    {
        var builder = new FingerprintBuilder();
        builder.Add(boundaries.Count);
        foreach (var boundary in boundaries)
        {
            builder.Add(boundary.PointCount);
            builder.Add(boundary.IsClosed);
            AddDoubleArrayFingerprint(ref builder, boundary.Points);
        }

        return builder.ToUInt64();
    }

    private static void AddSerializedFingerprint(ref FingerprintBuilder builder, object value, Type type)
    {
        builder.AddBytes(JsonSerializer.SerializeToUtf8Bytes(value, type));
    }

    private static void AddDoubleArrayFingerprint(ref FingerprintBuilder builder, IReadOnlyList<double> values)
    {
        builder.Add(values.Count);
        for (int i = 0; i < values.Count; i++)
            builder.Add(values[i]);
    }

    private static void AddIntArrayFingerprint(ref FingerprintBuilder builder, IReadOnlyList<int> values)
    {
        builder.Add(values.Count);
        for (int i = 0; i < values.Count; i++)
            builder.Add(values[i]);
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

    private static RhinoMesh CleanTinyFaces(RhinoMesh mesh, double tolerance, string sourceLabel, TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
            return mesh;

        double medianEdgeLength = ComputeMedianUndirectedEdgeLength(vertices, faces, mesh.Faces.Count);
        double effectiveCleanupTolerance = Math.Max(
            1e-6,
            Math.Min(
                Math.Max(tolerance, 1e-6),
                medianEdgeLength > 0 ? medianEdgeLength * 0.01 : 0.01));
        double minEdgeLength = Math.Max(effectiveCleanupTolerance * 2.0, 1e-5);
        double minProjectedArea = Math.Max(effectiveCleanupTolerance * effectiveCleanupTolerance * 2.0, 1e-10);
        var keptFaces = new List<int>(faces.Length);
        int removed = 0;

        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            int a = faces[i * 3];
            int b = faces[i * 3 + 1];
            int c = faces[i * 3 + 2];

            var pa = new Point3d(vertices[a * 3], vertices[a * 3 + 1], vertices[a * 3 + 2]);
            var pb = new Point3d(vertices[b * 3], vertices[b * 3 + 1], vertices[b * 3 + 2]);
            var pc = new Point3d(vertices[c * 3], vertices[c * 3 + 1], vertices[c * 3 + 2]);

            double l0 = pa.DistanceTo(pb);
            double l1 = pb.DistanceTo(pc);
            double l2 = pc.DistanceTo(pa);
            double area = Math.Abs((pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X)) * 0.5;

            if (Math.Min(l0, Math.Min(l1, l2)) < minEdgeLength || area < minProjectedArea)
            {
                removed++;
                continue;
            }

            keptFaces.Add(a);
            keptFaces.Add(b);
            keptFaces.Add(c);
        }

        if (removed == 0)
            return mesh;

        build.Diagnostics.Add($"{sourceLabel} removed {removed} tiny faces.");
        return BuildRemappedMesh(vertices, keptFaces);
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
               $"{build.ZoneObjects.Count:N0} zone outputs, {build.AuxiliaryObjects.Count:N0} auxiliary outputs";
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
