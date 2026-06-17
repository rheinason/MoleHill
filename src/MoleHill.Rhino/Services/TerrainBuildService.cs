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
            BuildScatterPlacements(snapshot, terrain, analysisMesh, build, shouldCancel);
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
        builder.Add(modifier.CutSlopeAngle);
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
        builder.Add(modifier.CutSlopeAngle);
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

}
