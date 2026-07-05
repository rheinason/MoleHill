using System.Diagnostics;
using System.Text.Json;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
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

            // Registry dispatch (Blender-style): the type's descriptor owns its build step. A null
            // descriptor means an unregistered modifier type — skip it, exactly as the old switch's
            // missing case did.
            ModifierTypeDescriptor? descriptor = TerrainTypeRegistry.ForModifierType(modifier.GetType());
            if (descriptor == null)
            {
                continue;
            }

            var context = new ModifierBuildContext
            {
                Snapshot = snapshot,
                Terrain = terrain,
                Modifier = modifier,
                Index = indexedModifier.index,
                StageKey = stageKey,
                Mode = mode,
                Build = build,
                RuntimeCache = runtimeCache,
                UsedStageKeys = usedStageKeys,
                ShouldCancel = shouldCancel,
                CurrentMesh = currentMesh,
                CurrentMeshFingerprint = currentMeshFingerprint,
                BaseMesh = baseMesh,
                BaseMeshFingerprint = baseMeshFingerprint,
            };

            descriptor.RunBuildStage(context);

            currentMesh = context.CurrentMesh;
            currentMeshFingerprint = context.CurrentMeshFingerprint;
            baseMesh = context.BaseMesh;
            baseMeshFingerprint = context.BaseMeshFingerprint;
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

            string markersStageKey = TerrainStageKey.ForMode(mode, "markers");
            usedStageKeys.Add(markersStageKey);
            ExecuteCachedMarkersStage(
                build,
                runtimeCache,
                markersStageKey,
                ComputeMarkersFingerprint(snapshot, terrain, analysisMesh, currentMeshFingerprint),
                () => BuildMarkers(snapshot, terrain, analysisMesh, build, shouldCancel),
                () => $"{build.MarkerObjects.Count:N0} marker outputs",
                shouldCancel);

            string objectsStageKey = TerrainStageKey.ForMode(mode, "objects");
            usedStageKeys.Add(objectsStageKey);
            ExecuteCachedObjectsStage(
                build,
                runtimeCache,
                objectsStageKey,
                ComputeObjectsFingerprint(snapshot, terrain, analysisMesh, currentMeshFingerprint),
                () => BuildObjectPlacements(snapshot, terrain, analysisMesh, build, shouldCancel),
                () => $"{build.ObjectPlacements.Sum(static group => group.Placements.Count):N0} object placements",
                shouldCancel);

            string scatterStageKey = TerrainStageKey.ForMode(mode, "scatter");
            usedStageKeys.Add(scatterStageKey);
            ExecuteCachedScatterStage(
                build,
                runtimeCache,
                scatterStageKey,
                ComputeScatterFingerprint(snapshot, terrain, analysisMesh, currentMeshFingerprint),
                () => BuildScatterPlacements(snapshot, terrain, analysisMesh, build, shouldCancel),
                () => $"{build.ScatterObjects.Count:N0} scatter outputs",
                shouldCancel);
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

    private static void ExecuteCachedObjectsStage(
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        ulong stageFingerprint,
        Action action,
        Func<string?> detailFactory,
        Func<bool>? shouldCancel)
    {
        const string stageName = "Objects";
        var timer = Stopwatch.StartNew();
        ThrowIfCancellationRequested(shouldCancel);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == stageFingerprint)
        {
            build.Diagnostics.AddRange(cachedEntry.Diagnostics);
            build.StructuredDiagnostics.AddRange(cachedEntry.StructuredDiagnostics);
            build.ObjectPlacements.AddRange(TerrainRuntimeCacheCloner.CloneObjectPlacementGroups(cachedEntry.ObjectPlacements));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int objectStart = build.ObjectPlacements.Count;
        action();
        ThrowIfCancellationRequested(shouldCancel);
        timer.Stop();

        runtimeCache.StageEntries[stageKey] = new StageCacheEntry
        {
            StageName = stageName,
            PreResolutionFingerprint = stageFingerprint,
            ResolvedInputFingerprint = stageFingerprint,
            OutputFingerprint = stageFingerprint,
            ObjectPlacements = TerrainRuntimeCacheCloner.CloneObjectPlacementGroups(build.ObjectPlacements.Skip(objectStart)),
            Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList(),
            StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList()
        };

        build.RecordTiming(stageName, timer.Elapsed, detailFactory());
    }

    private static void ExecuteCachedScatterStage(
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        ulong stageFingerprint,
        Action action,
        Func<string?> detailFactory,
        Func<bool>? shouldCancel)
    {
        const string stageName = "Scatter";
        var timer = Stopwatch.StartNew();
        ThrowIfCancellationRequested(shouldCancel);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == stageFingerprint)
        {
            build.Diagnostics.AddRange(cachedEntry.Diagnostics);
            build.StructuredDiagnostics.AddRange(cachedEntry.StructuredDiagnostics);
            build.ScatterObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.ScatterObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int scatterStart = build.ScatterObjects.Count;
        action();
        ThrowIfCancellationRequested(shouldCancel);
        timer.Stop();

        runtimeCache.StageEntries[stageKey] = new StageCacheEntry
        {
            StageName = stageName,
            PreResolutionFingerprint = stageFingerprint,
            ResolvedInputFingerprint = stageFingerprint,
            OutputFingerprint = stageFingerprint,
            ScatterObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.ScatterObjects.Skip(scatterStart)),
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

    private static ulong ComputeSmoothStageFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SmoothModifierDefinition modifier,
        int modifierIndex,
        ulong upstreamFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add(ComputeModifierStageFingerprint(snapshot, terrain, modifier, upstreamFingerprint));
        builder.Add(ComputeSelectedGradePathRoadBreaklinesFingerprint(snapshot, terrain, modifier, modifierIndex));
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
        int modifierIndex,
        ulong upstreamFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("SmoothPrepared");
        builder.Add(upstreamFingerprint);
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(terrain.GlobalTolerance);
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Boundaries));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Breaklines));
        builder.Add(ComputeSelectedGradePathRoadBreaklinesFingerprint(snapshot, terrain, modifier, modifierIndex));
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
               $"{build.ZoneObjects.Count:N0} zone outputs, {build.AuxiliaryObjects.Count:N0} auxiliary outputs, " +
               $"{build.MarkerObjects.Count:N0} marker outputs, {CountObjectPlacements(build):N0} object placements, " +
               $"{build.ScatterObjects.Count:N0} scatter outputs";
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
