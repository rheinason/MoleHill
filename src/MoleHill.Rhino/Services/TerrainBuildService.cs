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

internal sealed class TerrainBuildService
{
    private const int StageTimingDiagnosticThresholdMs = 250;
    private const double MinRepresentablePadPlaneNormalZ = 1e-3;
    private const int TriangulateCacheVersion = 2;

    private sealed class ZoneBoundaryEntry
    {
        public required CollageZoneDefinition Zone { get; init; }

        public required MeshAreaSplitter.AreaBoundary Boundary { get; init; }

        public required int ZoneOrder { get; init; }

        public required int SourceOrder { get; init; }

        public required double PriorityZ { get; init; }

        public string? InputLayerPath { get; init; }
    }

    private readonly record struct ReferenceComparisonStats(
        double CutVolume,
        double FillVolume,
        double CutFillDisplayAbsMax,
        bool IsEstimated)
    {
        public double NetVolume => CutVolume - FillVolume;
    }

    private sealed class ResolvedGradePadInputs
    {
        public required PadGrader.PadBoundary[] Pads { get; init; }

        public required PadGrader.LockCurve[] Locks { get; init; }

        public required SurfaceRemesher.ConstraintPolyline[] Constraints { get; init; }

        public required double SuggestedEdgeLength { get; init; }

        public required string[] Diagnostics { get; init; }
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

    internal readonly record struct TinyFaceCleanupResult(
        int[] Faces,
        int RemovedFaceCount,
        int BlockedFaceCount)
    {
        public bool HasChanges => RemovedFaceCount > 0;
    }

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

        foreach (var indexedModifier in terrain.Modifiers.Select((modifier, index) => (modifier, index)).Where(item => item.modifier.IsEnabled))
        {
            ThrowIfCancellationRequested(shouldCancel);
            ModifierDefinition modifier = indexedModifier.modifier;
            string stageKey = CreateModeStageKey(mode, CreateModifierStageKey(indexedModifier.index, modifier));
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
                    usedStageKeys.Add(CreateSmoothPreparedStageKey(stageKey));
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
                    usedStageKeys.Add(CreateGradePadTopologyStageKey(stageKey));
                    currentMesh = BuildGradePadMesh(
                        snapshot,
                        terrain,
                        gradePad,
                        build,
                        runtimeCache,
                        stageKey,
                        currentMesh,
                        currentMeshFingerprint,
                        mode,
                        out currentMeshFingerprint,
                        shouldCancel);
                    break;
                case GradePathModifierDefinition gradePath:
                    currentMesh = ExecuteCachedMeshStage(
                        build,
                        runtimeCache,
                        stageKey,
                        "Grade Path",
                        ComputeModifierStageFingerprint(snapshot, terrain, gradePath, currentMeshFingerprint),
                        () => currentMesh == null ? WarnMissingMesh(build, gradePath.Label) : ApplyGradePath(snapshot, terrain, currentMesh, gradePath, build, mode),
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
            string analysisStageKey = CreateModeStageKey(mode, "analysis");
            usedStageKeys.Add(analysisStageKey);
            build.AnalysisResults.AddRange(ExecuteCachedAnalysisStage(
                build,
                runtimeCache,
                analysisStageKey,
                ComputeAnalysisFingerprint(snapshot, terrain, baselineMesh, analysisMesh, baseMeshFingerprint, currentMeshFingerprint),
                () => BuildAnalyses(snapshot, terrain, baselineMesh, analysisMesh, build, shouldCancel),
                _ => DescribeMesh(analysisMesh),
                shouldCancel));

            string zonesStageKey = CreateModeStageKey(mode, "zones");
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
            List<TerrainAnalysisSummary> cachedAnalysis = TerrainRuntimeCacheCloner.CloneAnalyses(cachedEntry.AnalysisOutput);
            build.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.AuxiliaryObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory(cachedAnalysis)));
            return cachedAnalysis;
        }

        int diagnosticsStart = build.Diagnostics.Count;
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
            AnalysisOutput = TerrainRuntimeCacheCloner.CloneAnalyses(source.AnalysisOutput),
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

    private static double GetTerrainTolerance(TerrainBuildSnapshot snapshot, TerrainDefinition terrain)
    {
        return terrain.GlobalTolerance > 0
            ? terrain.GlobalTolerance
            : snapshot.ModelAbsoluteTolerance;
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
                out outputFingerprint);
        }

        double tolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : GetTerrainTolerance(snapshot, terrain);

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
        var polylines = TerrainTriangulationInputBuilder.CreateTriangulationPolylines(
            breaklineCurves,
            contourCurves,
            tolerance);
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
            build.Diagnostics.Add($"{merged.DescribeInvalidPoints()} during triangulation.");
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

        double tolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : GetTerrainTolerance(snapshot, terrain);

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
        polylines.AddRange(TerrainTriangulationInputBuilder.CreateTriangulationPolylines(
            breaklineCurves,
            contourCurves,
            tolerance));

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
            build.Diagnostics.Add($"{merged.DescribeInvalidPoints()} during add geometry.");
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

    private static RhinoMesh ApplyRemesh(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RemeshModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode)
    {
        double tolerance = GetTerrainTolerance(snapshot, terrain);
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
            out _);

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

        double tolerance = GetTerrainTolerance(snapshot, terrain);
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

        var breaklines = new List<(double[] xyPts, int ptCount)>();
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Breaklines))
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

        string preparedStageKey = CreateSmoothPreparedStageKey(stageKey);
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
        double tolerance = GetTerrainTolerance(snapshot, terrain);
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
        double wallTolerance = Math.Max(GetTerrainTolerance(snapshot, terrain), modifier.Tolerance);
        var wallCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.WallCurves);
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
            if (!IsWallStripUsable(wall.Strip, snapshot.ModelAbsoluteTolerance, out var stripMessage))
            {
                build.Diagnostics.Add($"Retaining wall pair ({wall.CurveA}, {wall.CurveB}) skipped: {stripMessage}");
                continue;
            }

            if (mode == TerrainBuildMode.Final && wall.Brep != null)
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
            snapshot,
            terrain,
            mesh,
            CombineConstraints(build.PersistentHardConstraints, wallConstraints),
            0.0,
            0.0,
            0.0,
            "Retaining Wall",
            build,
            out _);

        if (!ReferenceEquals(remeshed, mesh))
            build.PersistentHardConstraints.AddRange(wallConstraints);

        return remeshed;
    }

    private static RhinoMesh? BuildGradePadMesh(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        GradePadModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        RhinoMesh? mesh,
        ulong upstreamFingerprint,
        TerrainBuildMode mode,
        out ulong outputFingerprint,
        Func<bool>? shouldCancel = null)
    {
        const string stageName = "Grade Pad";
        string topologyStageKey = CreateGradePadTopologyStageKey(stageKey);
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

        double tolerance = GetTerrainTolerance(snapshot, terrain);
        ResolvedGradePadInputs resolvedInputs = ResolveGradePadInputs(
            snapshot,
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            modifier,
            tolerance);
        build.Diagnostics.AddRange(resolvedInputs.Diagnostics);
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

        var effectiveLocks = CombinePadLockCurves(resolvedInputs.Locks, build.PersistentHardConstraints);
        ulong topologyFingerprint = ComputeGradePadTopologyFingerprint(
            upstreamFingerprint,
            tolerance,
            modifier,
            resolvedInputs.Pads,
            effectiveLocks);

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
            var topologyDiagnostics = new List<string>();
            if (mode == TerrainBuildMode.Preview)
            {
                const string previewDiagnostic = "Grade Pad preview used direct grading without topology rebuild.";
                topologyDiagnostics.Add(previewDiagnostic);
                build.Diagnostics.Add(previewDiagnostic);
                topologyTimer.Stop();

                topologyEntry = new PadTopologyCacheEntry
                {
                    Fingerprint = topologyFingerprint,
                    OutputFingerprint = ComputePadTopologyOutputFingerprint(vertices, mesh.Vertices.Count, faces, mesh.Faces.Count),
                    Vertices = (double[])vertices.Clone(),
                    VertexCount = mesh.Vertices.Count,
                    Faces = (int[])faces.Clone(),
                    FaceCount = mesh.Faces.Count,
                    Diagnostics = topologyDiagnostics
                };
                runtimeCache.PadTopologyEntries[topologyStageKey] = TerrainRuntimeCacheCloner.ClonePadTopologyEntry(topologyEntry);
                build.RecordTiming(
                    "Grade Pad Topology",
                    topologyTimer.Elapsed,
                    "Preview used existing mesh topology.",
                    StageTimingDiagnosticThresholdMs);
            }
            else
            {
                // Focused CDT insertion: add pad boundary vertices to the existing mesh and
                // re-triangulate — same approach as Grade Path. Does NOT do a global quality
                // remesh via SurfaceRemesher, so the upstream path topology is preserved.
                // Do NOT forward MaxArea/MinAngle here — those are global Triangle.NET quality
                // constraints that would refine the entire terrain mesh, not just the pad area.
                // Focused insertion uses plain CDT (no quality refinement).
                var gradeResult = PadGrader.Grade(
                    vertices,
                    mesh.Vertices.Count,
                    faces,
                    mesh.Faces.Count,
                    resolvedInputs.Pads,
                    effectiveLocks.Length > 0 ? effectiveLocks : null,
                    maxArea: 0,
                    minAngle: 0,
                    out var gradeWarning);
                ThrowIfCancellationRequested(shouldCancel);

                if (!string.IsNullOrWhiteSpace(gradeWarning))
                    topologyDiagnostics.Add(gradeWarning!);

                double[] topologyVertices;
                int topologyVertexCount;
                int[] topologyFaces;
                int topologyFaceCount;
                if (gradeResult == null)
                {
                    topologyDiagnostics.Add(gradeWarning ?? "Grade Pad focused insertion failed; using upstream mesh.");
                    build.Diagnostics.AddRange(topologyDiagnostics);
                    topologyVertices = (double[])vertices.Clone();
                    topologyVertexCount = mesh.Vertices.Count;
                    topologyFaces = (int[])faces.Clone();
                    topologyFaceCount = mesh.Faces.Count;
                }
                else
                {
                    build.Diagnostics.AddRange(topologyDiagnostics);
                    AddOutputPolylinesAsBreaklines(gradeResult.OutputPolylines, build);
                    topologyVertices = gradeResult.Vertices;
                    topologyVertexCount = gradeResult.VertexCount;
                    topologyFaces = gradeResult.Faces;
                    topologyFaceCount = gradeResult.FaceCount;
                }

                topologyTimer.Stop();

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
                    "Grade Pad",
                    topologyTimer.Elapsed,
                    $"{topologyVertexCount:N0} verts, {topologyFaceCount:N0} faces",
                    StageTimingDiagnosticThresholdMs);
            }
        }

        if (mode != TerrainBuildMode.Preview)
        {
            build.Diagnostics.Add(
                $"Grade Pad focused insertion ({DescribeTopologyCounts(mesh.Vertices.Count, mesh.Faces.Count, topologyEntry.VertexCount, topologyEntry.FaceCount)}).");
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

        return CleanTinyFaces(
            RhinoGeometryConversions.BuildMesh(result.Vertices, result.VertexCount, result.Faces, result.FaceCount),
            tolerance,
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
        double tolerance)
    {
        var pads = new List<PadGrader.PadBoundary>();
        var diagnostics = new List<string>();
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundaries))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out var polyline))
                continue;

            int count = polyline.Count;
            if (count > 1 && polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;
            if (count < 3)
                continue;

            if (!TryCreatePlanarPadBoundary(polyline, count, modifier.SlopeAngle, modifier.MaxDistance, out var pad, out string? diagnostic))
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

        PadGrader.PadBoundary[] padArray = pads.ToArray();
        PadGrader.LockCurve[] lockArray = locks.ToArray();
        PadGrader.ConstraintSet constraintSet = padArray.Length == 0
            ? new PadGrader.ConstraintSet
            {
                Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0,
                Diagnostics = Array.Empty<string>()
            }
            : PadGrader.CreateConstraints(
                vertices,
                vertexCount,
                faces,
                faceCount,
                padArray,
                lockArray.Length == 0 ? null : lockArray);
        var allDiagnostics = new List<string>(diagnostics.Count + constraintSet.Diagnostics.Length);
        allDiagnostics.AddRange(diagnostics);
        allDiagnostics.AddRange(constraintSet.Diagnostics);

        return new ResolvedGradePadInputs
        {
            Pads = padArray,
            Locks = lockArray,
            Constraints = constraintSet.Constraints,
            SuggestedEdgeLength = constraintSet.SuggestedEdgeLength,
            Diagnostics = allDiagnostics.ToArray()
        };
    }

    private static bool TryCreatePlanarPadBoundary(
        Polyline polyline,
        int vertexCount,
        double slopeAngle,
        double maxDistance,
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
            maxDistance);
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

        double tolerance = GetTerrainTolerance(snapshot, terrain);
        ResolvedGradePathInputs resolvedInputs = ResolveGradePathInputs(snapshot, vertices, mesh.Vertices.Count, faces, mesh.Faces.Count, modifier, tolerance);
        if (resolvedInputs.Paths.Length == 0)
        {
            build.Diagnostics.Add("Grade Path has no valid paths.");
            return mesh;
        }

        if (build.PersistentHardConstraints.Count > 0 && resolvedInputs.Constraints.Length > 0)
        {
            var pathConstraintData = resolvedInputs.Constraints
                .Select(static constraint => new ConstraintConflictDiagnostics.PolylineData(constraint.Points, constraint.PointCount, constraint.IsClosed))
                .ToArray();
            var hardConstraintData = build.PersistentHardConstraints
                .Select(static constraint => new ConstraintConflictDiagnostics.PolylineData(constraint.Points, constraint.PointCount, constraint.IsClosed))
                .ToArray();
            var conflictSummary = ConstraintConflictDiagnostics.Analyze(pathConstraintData, hardConstraintData, tolerance);
            build.Diagnostics.Add(conflictSummary.CreateSummaryMessage());
            if (conflictSummary.CreateSampleMessage() is string sampleMessage)
                build.Diagnostics.Add(sampleMessage);
        }

        var combinedConstraints = CombineConstraints(build.PersistentHardConstraints, resolvedInputs.Constraints);
        bool hasPersistentHardConstraints = build.PersistentHardConstraints.Count > 0;
        if (TerrainBuildHeuristics.ShouldUseLegacyPathTriangulation(
            mode,
            hasPersistentHardConstraints,
            mesh.Vertices.Count,
            mesh.Faces.Count))
        {
            RhinoMesh? legacyMesh = ApplyGradePathLegacy(mesh, vertices, faces, resolvedInputs, tolerance, build, build.PersistentHardConstraints);
            if (legacyMesh != null)
            {
                bool apronTopologyAdded =
                    resolvedInputs.Constraints.Length == 0 ||
                    legacyMesh.Vertices.Count > mesh.Vertices.Count ||
                    legacyMesh.Faces.Count > mesh.Faces.Count;

                if (apronTopologyAdded)
                    return legacyMesh;

                build.Diagnostics.Add("Grade Path legacy insertion did not add apron topology; retrying with remesh topology mode.");
            }
        }
        else if (hasPersistentHardConstraints)
        {
            build.Diagnostics.Add("Grade Path used remesh topology mode to preserve persistent hard constraints.");
        }
        else
        {
            build.Diagnostics.Add(
                $"Grade Path used remesh topology mode on a dense upstream mesh ({mesh.Vertices.Count:N0} verts, {mesh.Faces.Count:N0} faces) to avoid a full point-insertion re-triangulation stall.");
        }

        var topologyTimer = Stopwatch.StartNew();
        bool keptInputTopology = false;
        RhinoMesh topologyMesh = mesh;
        if (resolvedInputs.Constraints.Length > 0)
        {
            topologyMesh = RebuildMeshWithConstraints(
                snapshot,
                terrain,
                mesh,
                combinedConstraints,
                resolvedInputs.SuggestedEdgeLength,
                0,
                0,
                "Grade Path",
                build,
                out keptInputTopology);
        }
        topologyTimer.Stop();

        if (!RhinoGeometryConversions.TryExtractMeshData(topologyMesh, out var topologyVertices, out var topologyFaces, out errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade path topology.");
            return topologyMesh;
        }

        string topologyMode = resolvedInputs.Constraints.Length > 0
            ? (keptInputTopology ? "remesh attempted, input mesh kept" : "remesh")
            : "existing mesh";
        build.Diagnostics.Add(
            $"Grade Path topology mode: {topologyMode} ({DescribeTopologyCounts(mesh.Vertices.Count, mesh.Faces.Count, topologyMesh.Vertices.Count, topologyMesh.Faces.Count)}; edge {resolvedInputs.SuggestedEdgeLength:0.###}, max area 0, min angle 0).");
        build.RecordTiming(
            "Grade Path Topology",
            topologyTimer.Elapsed,
            $"{DescribeTopologyCounts(mesh.Vertices.Count, mesh.Faces.Count, topologyMesh.Vertices.Count, topologyMesh.Faces.Count)} ({topologyMode})",
            StageTimingDiagnosticThresholdMs);

        var filterTimer = Stopwatch.StartNew();
        double[] gradedVertices = PathGrader.ApplyGradingZ(
            topologyVertices,
            topologyMesh.Vertices.Count,
            topologyFaces,
            topologyMesh.Faces.Count,
            resolvedInputs.Paths,
            combinedConstraints,
            out int changedVertexCount);
        filterTimer.Stop();
        build.RecordTiming(
            "Grade Path Filter",
            filterTimer.Elapsed,
            $"{topologyMesh.Vertices.Count:N0} verts",
            StageTimingDiagnosticThresholdMs);
        if (changedVertexCount == 0)
        {
            build.Diagnostics.Add(
                "Grade Path did not change any mesh vertices. Curve Z defines the finished road elevation; a path already lying on the terrain, or a topology rebuild that could not add road-band vertices, can leave the result visually unchanged.");
        }

        return CleanTinyFaces(
            RhinoGeometryConversions.BuildMesh(gradedVertices, topologyMesh.Vertices.Count, topologyFaces, topologyMesh.Faces.Count),
            tolerance,
            "Grade Path",
            build);
    }

    private static RhinoMesh? ApplyGradePathLegacy(
        RhinoMesh mesh,
        double[] vertices,
        int[] faces,
        ResolvedGradePathInputs resolvedInputs,
        double tolerance,
        TerrainBuildResult build,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints)
    {
        var legacyTimer = Stopwatch.StartNew();
        var result = PathGrader.Grade(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            resolvedInputs.Paths,
            persistentHardConstraints,
            out var warning);
        legacyTimer.Stop();

        if (result == null)
        {
            build.RecordTiming("Grade Path Legacy", legacyTimer.Elapsed, "failed", StageTimingDiagnosticThresholdMs);
            build.Diagnostics.Add(warning ?? "Grade Path failed.");
            return null;
        }

        build.Diagnostics.Add(
            $"Grade Path topology mode: legacy point insertion ({DescribeTopologyCounts(mesh.Vertices.Count, mesh.Faces.Count, result.VertexCount, result.FaceCount)}).");
        build.RecordTiming(
            "Grade Path Legacy",
            legacyTimer.Elapsed,
            DescribeTopologyCounts(mesh.Vertices.Count, mesh.Faces.Count, result.VertexCount, result.FaceCount),
            StageTimingDiagnosticThresholdMs);

        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);

        AddOutputPolylinesAsBreaklines(result.OutputPolylines, build);

        return CleanTinyFaces(
            RhinoGeometryConversions.BuildMesh(result.Vertices, result.VertexCount, result.Faces, result.FaceCount),
            tolerance,
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

    private static ResolvedGradePathInputs ResolveGradePathInputs(
        TerrainBuildSnapshot snapshot,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        GradePathModifierDefinition modifier,
        double tolerance)
    {
        var paths = new List<PathGrader.PathDefinition>();
        double requestedEdgeLength = TerrainBuildHeuristics.GetGradePathCurveSamplingLength(modifier.Width);
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Paths))
        {
            if (!RhinoSourceResolver.TryGetPolyline(
                    curve,
                    tolerance,
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
            : PathGrader.CreateConstraints(vertices, vertexCount, faces, faceCount, pathArray, tolerance);

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

        var referenceMeshes = TerrainBuildSnapshotResolver.ResolveMeshes(snapshot, modifier.ReferenceSurface);
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

        double tolerance = GetTerrainTolerance(snapshot, terrain);

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
                build.PersistentHardConstraints,
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

        double zMargin = Math.Max(snapshot.ModelAbsoluteTolerance * 10.0, 1.0);
        double rayStartZ = Math.Max(samplePoint.Z, meshBounds.Max.Z) + zMargin;
        var ray = new Ray3d(new Point3d(samplePoint.X, samplePoint.Y, rayStartZ), -Vector3d.ZAxis);
        double rayDistance = Intersection.MeshRay(mesh, ray);
        if (rayDistance < 0.0)
        {
            diagnostic = "Objects skipped a source object because it is outside the terrain footprint.";
            return false;
        }

        terrainPoint = ray.PointAt(rayDistance);
        var meshPoint = mesh.ClosestMeshPoint(terrainPoint, Math.Max(snapshot.ModelAbsoluteTolerance * 4.0, 1e-4));
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
                    shouldCancel),
                ProjectedElevationLabelAnalysisDefinition projectedElevation => TerrainAnalysisAnnotationBuilder.BuildProjectedElevationSummary(
                    snapshot,
                    currentMesh,
                    projectedElevation,
                    build,
                    shouldCancel),
                PointSlopeLabelAnalysisDefinition pointSlope => TerrainAnalysisAnnotationBuilder.BuildPointSlopeSummary(
                    snapshot,
                    currentMesh,
                    pointSlope,
                    build,
                    shouldCancel),
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
        var (objects, summary) = BuildContourCore(currentMesh, analysis, elevMinZ, elevMaxZ);
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
        double elevMaxZ)
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
                    LayerPath = analysis.OutputLayerPath
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
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double requestedEdgeLength,
        double maxArea,
        double minAngle,
        string label,
        TerrainBuildResult build,
        out bool keptInputMesh)
    {
        keptInputMesh = false;
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var originalVertices, out var originalFaces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? $"Could not extract mesh data for {label.ToLowerInvariant()}.");
            return mesh;
        }

        double tolerance = GetTerrainTolerance(snapshot, terrain);
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
                ProtectSharpEdges = true,
                // When no quality params are set, RequestedEdgeLength controls only constraint
                // pre-densification spacing — do not use it to drive Steiner interior refinement.
                ConstraintInsertionOnly = maxArea <= 0 && minAngle <= 0
            });

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

        return CleanTinyFaces(BuildMeshFromArrays(remeshResult.Vertices, remeshResult.Faces), tolerance, label, build);
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

    private static string CreateModifierStageKey(int modifierIndex, ModifierDefinition modifier)
    {
        return $"modifier:{modifierIndex}:{modifier.GetType().Name}:{modifier.Id:N}";
    }

    private static string CreateModeStageKey(TerrainBuildMode mode, string stageKey)
    {
        return TerrainRuntimeCache.GetStagePrefix(mode) + stageKey;
    }

    private static string CreateGradePadTopologyStageKey(string stageKey)
    {
        return $"{stageKey}:topology";
    }

    private static string CreateSmoothPreparedStageKey(string stageKey)
    {
        return $"{stageKey}:prepared";
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
        builder.Add("GradePadTopologyV3");
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
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeAnalysisFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh baseMesh,
        RhinoMesh currentMesh,
        ulong baseMeshFingerprint,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Analysis");
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(baseMeshFingerprint != 0 ? baseMeshFingerprint : ComputeMeshFingerprint(baseMesh));
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(currentMesh));
        foreach (var analysis in terrain.Analyses)
        {
            AddSerializedFingerprint(ref builder, analysis, analysis.GetType());
            foreach (var sourceSet in analysis.EnumerateSourceSets())
                builder.Add(ComputeSourceSetFingerprint(snapshot, sourceSet));
        }
        return builder.ToUInt64();
    }

    private static ulong ComputeZonesFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> persistentHardConstraints,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Zones");
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(GetTerrainTolerance(snapshot, terrain));
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(mesh));
        builder.Add(ComputeConstraintsFingerprint(persistentHardConstraints));

        foreach (var zone in terrain.Zones.Where(zone => zone.IsEnabled))
        {
            AddSerializedFingerprint(ref builder, zone, zone.GetType());
            builder.Add(ComputeSourceSetFingerprint(snapshot, zone.Boundaries));
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeMarkersFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        ulong currentMeshFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Markers");
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(currentMeshFingerprint != 0 ? currentMeshFingerprint : ComputeMeshFingerprint(mesh));

        foreach (var marker in terrain.Markers.Where(marker => marker.IsEnabled))
        {
            AddSerializedFingerprint(ref builder, marker, marker.GetType());
            builder.Add(ComputeSourceSetFingerprint(snapshot, marker.Sources));
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeSourceSetFingerprint(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        return TerrainBuildSnapshotResolver.GetSourceSetFingerprint(snapshot, sourceSet);
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
        double minProjectedArea = Math.Max(effectiveCleanupTolerance * effectiveCleanupTolerance * 2.0, 1e-10);

        var candidates = new List<(int FaceIndex, double Area, double SmallestEdge)>();
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];

            var pa = new Point3d(vertices[a * 3], vertices[a * 3 + 1], vertices[a * 3 + 2]);
            var pb = new Point3d(vertices[b * 3], vertices[b * 3 + 1], vertices[b * 3 + 2]);
            var pc = new Point3d(vertices[c * 3], vertices[c * 3 + 1], vertices[c * 3 + 2]);

            double l0 = pa.DistanceTo(pb);
            double l1 = pb.DistanceTo(pc);
            double l2 = pc.DistanceTo(pa);
            double area = Math.Abs((pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X)) * 0.5;
            double smallestEdge = Math.Min(l0, Math.Min(l1, l2));

            if (smallestEdge < minEdgeLength || area < minProjectedArea)
                candidates.Add((faceIndex, area, smallestEdge));
        }

        if (candidates.Count == 0)
            return new TinyFaceCleanupResult((int[])faces.Clone(), 0, 0);

        candidates.Sort(static (left, right) =>
        {
            int compare = left.Area.CompareTo(right.Area);
            if (compare != 0)
                return compare;

            return left.SmallestEdge.CompareTo(right.SmallestEdge);
        });

        bool[] keepFace = new bool[faceCount];
        Array.Fill(keepFace, true);

        int removedCount = 0;
        int blockedCount = 0;
        bool enforceSingleClosedBoundaryLoop = originalTopology.HasSingleClosedBoundaryLoop;

        foreach (var candidate in candidates)
        {
            keepFace[candidate.FaceIndex] = false;
            if (enforceSingleClosedBoundaryLoop)
            {
                int[] proposedFaces = BuildFilteredFaces(faces, faceCount, keepFace, removedCount + 1);
                var proposedTopology = MeshTopologyValidator.AnalyzeBoundaryGraph(proposedFaces, proposedFaces.Length / 3);
                if (!proposedTopology.HasSingleClosedBoundaryLoop)
                {
                    keepFace[candidate.FaceIndex] = true;
                    blockedCount++;
                    continue;
                }
            }

            removedCount++;
        }

        if (removedCount == 0)
            return new TinyFaceCleanupResult((int[])faces.Clone(), 0, blockedCount);

        return new TinyFaceCleanupResult(
            BuildFilteredFaces(faces, faceCount, keepFace, removedCount),
            removedCount,
            blockedCount);
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
