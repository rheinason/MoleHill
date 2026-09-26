using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using Rhino;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    private const int StageTimingDiagnosticThresholdMs = 250;

    /// <summary>
    /// How much of the upstream vertex count a Retaining Wall constrained rebuild must retain to be
    /// accepted. Breakline insertion adds vertices, so any real loss means the rebuild reseeded from
    /// the boundary and discarded the interior. Generous, because legitimate tiny-face cleanup trims
    /// a handful: this is a floor against wholesale detail loss, not a quality measure.
    /// </summary>
    private const double RetainingWallRebuildMinimumVertexRatio = 0.90;
    private const double MinRepresentablePadPlaneNormalZ = 1e-3;
    private const int TriangulateCacheVersion = 5;
    private const int InSituStairTreadDepthWarningColorArgb = unchecked((int)0xFFFF0000);

    public TerrainBuildResult Build(
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainRuntimeCache runtimeCache,
        TerrainBuildMode mode = TerrainBuildMode.Final,
        Func<bool>? shouldCancel = null,
        Action<TerrainBuildProgress>? reportProgress = null,
        TerrainLatencyScope? latency = null,
        Action<RhinoMesh, RhinoMesh?>? publishInterimGeometry = null)
    {
        TerrainBuildSnapshot snapshot = TerrainBuildSnapshotBuilder.Create(doc, terrain);
        return Build(snapshot, runtimeCache, mode, shouldCancel, reportProgress, latency, publishInterimGeometry);
    }

    public TerrainBuildResult Build(
        TerrainBuildSnapshot snapshot,
        TerrainRuntimeCache runtimeCache,
        TerrainBuildMode mode = TerrainBuildMode.Final,
        Func<bool>? shouldCancel = null,
        Action<TerrainBuildProgress>? reportProgress = null,
        TerrainLatencyScope? latency = null,
        Action<RhinoMesh, RhinoMesh?>? publishInterimGeometry = null)
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
                ReportProgress = reportProgress,
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

        TriangulateModifierDefinition? boundaryOwner = GetBoundaryOwner(terrain);
        bool hasShapeBoundaries = boundaryOwner != null &&
            (boundaryOwner.OuterBoundaries.HasReferences || boundaryOwner.HideBoundaries.HasReferences || boundaryOwner.ShowBoundaries.HasReferences);
        if (currentMesh != null && hasShapeBoundaries)
        {
            ThrowIfCancellationRequested(shouldCancel);
            string trimCurrentKey = TerrainStageKey.ForMode(mode, "boundary-roles:current");
            usedStageKeys.Add(trimCurrentKey);
            RhinoMesh currentInput = currentMesh;
            currentMesh = ExecuteCachedMeshStage(
                build,
                runtimeCache,
                trimCurrentKey,
                "Boundary Roles",
                ComputeBoundaryRoleStageFingerprint(snapshot, terrain, currentMeshFingerprint),
                () => ApplyTerrainBoundaryRoles(snapshot, terrain, currentInput, build, shouldCancel),
                result => DescribeModifierMeshResult("Boundary Roles", result),
                out currentMeshFingerprint,
                shouldCancel);
            if (baseMesh != null)
            {
                string trimBaseKey = TerrainStageKey.ForMode(mode, "boundary-roles:base");
                usedStageKeys.Add(trimBaseKey);
                RhinoMesh baseInput = baseMesh;
                baseMesh = ExecuteCachedMeshStage(
                    build,
                    runtimeCache,
                    trimBaseKey,
                    "Boundary Roles Baseline",
                    ComputeBoundaryRoleStageFingerprint(snapshot, terrain, baseMeshFingerprint),
                    () => ApplyTerrainBoundaryRoles(snapshot, terrain, baseInput, build, shouldCancel),
                    result => DescribeModifierMeshResult("Boundary Roles Baseline", result),
                    out baseMeshFingerprint,
                    shouldCancel);
            }
        }

        build.PrimaryMesh = currentMesh;
        build.BaseMesh = baseMesh ?? currentMesh;
        latency?.Mark(
            TerrainLatencyPhase.GeometryReady,
            currentMesh == null ? "no mesh" : $"{currentMesh.Vertices.Count:N0} verts, {currentMesh.Faces.Count:N0} faces");

        // The mesh the user is waiting to see is finished here, but the result is not returned until the
        // final-only output stages below have run - measured at 5.1 s of a 7.5 s wait on a 244k-face
        // terrain with drainage analyses enabled. Hand it over now when the caller asked for that. The
        // caller copies before displaying, because the stages below keep reading `currentMesh`.
        var dependentOutputsTimer = new Stopwatch();
        if (publishInterimGeometry != null && mode == TerrainBuildMode.Final && currentMesh != null)
        {
            ThrowIfCancellationRequested(shouldCancel);
            publishInterimGeometry(currentMesh, baseMesh);
            latency?.Mark(TerrainLatencyPhase.InterimPublished);
        }

        dependentOutputsTimer.Start();
        if (currentMesh != null && mode == TerrainBuildMode.Final)
        {
            ThrowIfCancellationRequested(shouldCancel);
            RhinoMesh analysisMesh = currentMesh;
            RhinoMesh baselineMesh = baseMesh ?? analysisMesh;

            // One reference projector per reference, for the whole build. A projection context depends
            // only on the reference geometry, so terrain-level analyses and zone outputs that compare
            // against the same reference share it instead of indexing that mesh twice. Statistics stay
            // on their own stricter key, which includes the current geometry, so different meshes still
            // produce independent results.
            var referenceProjectionCache = new Dictionary<ReferenceProjectionCacheKey, ReferenceProjectionContext>();

            build.AnalysisResults.AddRange(BuildAnalyses(
                snapshot,
                terrain,
                baselineMesh,
                analysisMesh,
                baseMeshFingerprint,
                currentMeshFingerprint,
                build,
                runtimeCache,
                usedStageKeys,
                referenceProjectionCache,
                shouldCancel));
            latency?.Mark($"{TerrainLatencyPhase.OutputFamilyPrefix}analyses", $"{build.AnalysisResults.Count:N0} analyses");

            string zonesStageKey = TerrainStageKey.ForMode(mode, "zones");
            usedStageKeys.Add(zonesStageKey);
            ExecuteCachedZonesStage(
                build,
                runtimeCache,
                zonesStageKey,
                ComputeZonesFingerprint(
                    snapshot,
                    terrain,
                    analysisMesh,
                    baselineMesh,
                    build.PersistentHardConstraints,
                    currentMeshFingerprint,
                    baseMeshFingerprint),
                () => BuildTerrainZones(snapshot, analysisMesh, terrain, build, referenceProjectionCache, shouldCancel),
                () => $"{build.ZoneObjects.Count:N0} zone outputs",
                shouldCancel);
            latency?.Mark($"{TerrainLatencyPhase.OutputFamilyPrefix}zones", $"{build.ZoneObjects.Count:N0} zone outputs");

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
            latency?.Mark($"{TerrainLatencyPhase.OutputFamilyPrefix}markers", $"{build.MarkerObjects.Count:N0} marker outputs");

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
            latency?.Mark($"{TerrainLatencyPhase.OutputFamilyPrefix}objects", $"{build.ObjectPlacements.Sum(static group => group.Placements.Count):N0} placements");

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

            // Report tables run last, and uncached, because their input is every other stage's output —
            // the zone schedule is not measured until the zones stage has run, so a report built with the
            // analyses would draw the previous build's zone figures, or none at all on a first build. A
            // cache key would have to fingerprint every stage's results to be correct, which costs more
            // than laying out a few hundred text entities.
            latency?.Mark($"{TerrainLatencyPhase.OutputFamilyPrefix}scatter", $"{build.ScatterObjects.Count:N0} scatter outputs");
            BuildReportTables(snapshot, terrain, analysisMesh, build, shouldCancel);
            latency?.Mark($"{TerrainLatencyPhase.OutputFamilyPrefix}reports");
        }

        latency?.Mark(TerrainLatencyPhase.OutputsEnd);

        ThrowIfCancellationRequested(shouldCancel);
        dependentOutputsTimer.Stop();
        build.DependentOutputsElapsed = dependentOutputsTimer.Elapsed;
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
        int runtimeOverlayStart = build.RuntimeOverlays.Count;
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
            structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart),
            runtimeOverlays: build.RuntimeOverlays.Skip(runtimeOverlayStart));
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
            RestoreCachedDiagnostics(build, cachedEntry);
            build.ZoneObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.ZoneObjects));
            build.TerrainRegions.AddRange(cachedEntry.TerrainRegions.Select(region => region.Duplicate()));
            build.ZoneAnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneZoneAnalyses(cachedEntry.ZoneAnalysisOutput));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int zoneStart = build.ZoneObjects.Count;
        int terrainRegionStart = build.TerrainRegions.Count;
        int zoneAnalysisStart = build.ZoneAnalysisResults.Count;
        int zoneOverlayStart = build.RuntimeOverlays.Count;
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
            TerrainRegions = build.TerrainRegions.Skip(terrainRegionStart).Select(region => region.Duplicate()).ToList(),
            ZoneAnalysisOutput = TerrainRuntimeCacheCloner.CloneZoneAnalyses(build.ZoneAnalysisResults.Skip(zoneAnalysisStart)),
            Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList(),
            StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList(),
            RuntimeOverlays = TerrainRuntimeCacheCloner.CloneRuntimeOverlays(build.RuntimeOverlays.Skip(zoneOverlayStart))
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
            RestoreCachedDiagnostics(build, cachedEntry);
            build.MarkerObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.MarkerObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int markerStart = build.MarkerObjects.Count;
        int markerOverlayStart = build.RuntimeOverlays.Count;
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
            StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList(),
            RuntimeOverlays = TerrainRuntimeCacheCloner.CloneRuntimeOverlays(build.RuntimeOverlays.Skip(markerOverlayStart))
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
            RestoreCachedDiagnostics(build, cachedEntry);
            build.ObjectPlacements.AddRange(TerrainRuntimeCacheCloner.CloneObjectPlacementGroups(cachedEntry.ObjectPlacements));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int objectStart = build.ObjectPlacements.Count;
        int objectOverlayStart = build.RuntimeOverlays.Count;
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
            StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList(),
            RuntimeOverlays = TerrainRuntimeCacheCloner.CloneRuntimeOverlays(build.RuntimeOverlays.Skip(objectOverlayStart))
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
            RestoreCachedDiagnostics(build, cachedEntry);
            build.ScatterObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.ScatterObjects));
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(detailFactory()));
            return;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        int scatterStart = build.ScatterObjects.Count;
        int scatterOverlayStart = build.RuntimeOverlays.Count;
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
            StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList(),
            RuntimeOverlays = TerrainRuntimeCacheCloner.CloneRuntimeOverlays(build.RuntimeOverlays.Skip(scatterOverlayStart))
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
            snapshot.ResolvedUnitContext);
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

        if (modifier is AddGeometryModifierDefinition && GetBoundaryOwner(terrain) is { } boundaryOwner)
            builder.Add(ComputeSourceSetFingerprint(snapshot, boundaryOwner.DataClipBoundaries));

        if (modifier is ProjectToModifierDefinition { TargetTerrainId: { } targetTerrainId })
        {
            builder.Add(targetTerrainId);
            if (snapshot.SectionTerrains.TryGetValue(targetTerrainId, out TerrainSectionReferenceSnapshot? targetTerrain))
                builder.Add(targetTerrain.MeshFingerprint);
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

    private static ulong ComputeSimplifyStageFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SimplifyModifierDefinition modifier,
        ulong upstreamFingerprint,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> effectiveConstraints)
    {
        var builder = new FingerprintBuilder();
        builder.Add("SurfaceSimplifierV1");
        builder.Add(ComputeModifierStageFingerprint(snapshot, terrain, modifier, upstreamFingerprint));
        builder.Add(ComputeConstraintsFingerprint(effectiveConstraints));
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
        AddTriangulationSettingsFingerprint(ref builder, modifier);
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.TinMesh));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.DemSurface));
        builder.Add(snapshot.DemFingerprints.GetValueOrDefault(modifier.Id));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Points));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Breaklines));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.Contours));
        builder.Add(ComputeSourceSetFingerprint(snapshot, modifier.DataClipBoundaries));
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
        AddTriangulationSettingsFingerprint(ref builder, modifier);
        AddDoubleArrayFingerprint(ref builder, xyCoords);
        AddDoubleArrayFingerprint(ref builder, zValues);
        AddIntArrayFingerprint(ref builder, segments);
        builder.Add(ComputeConstraintsFingerprint(persistentHardConstraints));
        builder.Add(ComputeBoundaryPolylinesFingerprint(boundaryPolylines));
        return builder.ToUInt64();
    }

    private static void AddTriangulationSettingsFingerprint(ref FingerprintBuilder builder, TriangulateModifierDefinition modifier)
    {
        builder.Add(modifier.Id);
        builder.Add(modifier.IsEnabled);
        builder.Add(modifier.Tolerance);
        builder.Add(modifier.PeelBoundaryTriangles);
        builder.Add(modifier.MaxBoundaryEdgeLength);
        builder.Add(modifier.MaxBoundaryAngleDegrees);
        builder.Add(modifier.MaxBoundarySlopeDegrees);
        builder.Add(modifier.ContourMode);
        builder.Add(modifier.DemElevationScale);
        builder.Add(modifier.DemSourceFileName);
    }

    private static ulong ComputeBoundaryRoleStageFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        ulong upstreamFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("BoundaryRolesV1");
        builder.Add(upstreamFingerprint);
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(terrain.GlobalTolerance);
        if (GetBoundaryOwner(terrain) is { } owner)
        {
            builder.Add(ComputeSourceSetFingerprint(snapshot, owner.OuterBoundaries));
            builder.Add(ComputeSourceSetFingerprint(snapshot, owner.HideBoundaries));
            builder.Add(ComputeSourceSetFingerprint(snapshot, owner.ShowBoundaries));
        }
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
