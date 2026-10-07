using System.Security.Cryptography;
using System.Text;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Display state, terrain-object placement sync, visibility/lock, and display/base material management.
internal sealed partial class TerrainController
{
    private void UpdateDisplayState(
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainRuntimeCache runtimeCache,
        TerrainBuildResult build,
        long buildVersion = 0)
    {
        BoundingBox previousPreviewBounds = runtimeCache.DisplayState?.GetContentBounds(doc) ?? BoundingBox.Empty;
        TerrainDisplayState? previousDisplayState = runtimeCache.DisplayState;
        var displayState = new TerrainDisplayState
        {
            IsPreview = build.Mode == TerrainBuildMode.Preview,
            HasDeferredOutputs = build.HasDeferredOutputs,
            TerrainMesh = build.PrimaryMesh,
            BaseTerrainMesh = build.BaseMesh,
            // A final build lands geometry and outputs together, so both revisions advance and nothing
            // is stale. A preview carries the previous outputs forward, so its outputs revision does not.
            GeometryRevision = buildVersion,
            OutputsRevision = build.Mode == TerrainBuildMode.Final
                ? buildVersion
                : previousDisplayState?.OutputsRevision ?? 0
        };
        displayState.RuntimeOverlays.AddRange(TerrainRuntimeCacheCloner.CloneRuntimeOverlays(build.RuntimeOverlays));
        displayState.HardConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(build.PersistentHardConstraints));
        displayState.ElevationConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(build.PersistentElevationConstraints));
        displayState.VisibleDiagnosticOwners.UnionWith(runtimeCache.VisibleDiagnosticOwners);
        if (build.Mode == TerrainBuildMode.Final)
            displayState.AnalysisResults.AddRange(build.AnalysisResults);
        if (build.Mode == TerrainBuildMode.Final)
        {
            displayState.ZoneObjects.AddRange(build.ZoneObjects);
            displayState.TerrainRegions.AddRange(build.TerrainRegions.Select(region => region.Duplicate()));
            displayState.ZoneAnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneZoneAnalyses(build.ZoneAnalysisResults));
            displayState.AuxiliaryObjects.AddRange(build.AuxiliaryObjects);
            displayState.MarkerObjects.AddRange(build.MarkerObjects);
            displayState.ScatterObjects.AddRange(build.ScatterObjects);
            displayState.RebuildScatterObjectRanges();
        }
        else if (previousDisplayState != null)
        {
            displayState.PreserveDeferredOutputsFrom(previousDisplayState);
        }
        displayState.IncludePreviousPreviewBounds(previousPreviewBounds);
        runtimeCache.DisplayState = displayState;
        UpdateRuntimePreview(doc, terrain, runtimeCache);
        NotifyRenderMeshesChanged(doc);
    }

    /// <summary>
    /// Shows a finished terrain mesh before its dependent outputs have been computed, carrying the
    /// previous build's analyses, annotations, markers and scatter forward and marking them stale.
    ///
    /// Runs on the UI thread, posted from the worker. It refuses if the build it belongs to has since
    /// been superseded or reset, so a slow build cannot repaint over a newer one. The published mesh is
    /// a copy taken on the worker, because the stages still to run keep reading the original.
    /// </summary>
    private void PublishInterimGeometry(
        uint docSerial,
        Guid terrainId,
        long buildVersion,
        long buildGeneration,
        Mesh interimMesh,
        Mesh? interimBaseMesh)
    {
        TerrainLatencyTrace.Record(
            docSerial,
            terrainId,
            buildVersion,
            buildGeneration,
            TerrainBuildMode.Final,
            TerrainLatencyPhase.InterimRan);

        RhinoDoc? doc = RhinoDoc.FromRuntimeSerialNumber(docSerial);
        if (doc == null)
            return;

        if (!_rebuildStates.TryGetValue((docSerial, terrainId), out TerrainRebuildState? rebuildState) ||
            rebuildState.BuildGeneration != buildGeneration ||
            rebuildState.RequestedVersion > buildVersion)
        {
            return;
        }

        TerrainDefinition? terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        TerrainRuntimeCache runtimeCache = GetRuntimeCache(docSerial, terrainId);
        TerrainDisplayState? previous = runtimeCache.DisplayState;
        if (previous == null)
            return;

        BoundingBox previousPreviewBounds = previous.GetContentBounds(doc);
        var displayState = new TerrainDisplayState
        {
            IsPreview = false,
            // The outputs on screen are the previous build's, so every existing consumer that refuses
            // deferred output - bake, the interop mesh accessors, the Grasshopper bridge - refuses this
            // state too. That is the freshness contract; the revisions below say how stale it is.
            HasDeferredOutputs = true,
            TerrainMesh = interimMesh,
            InterimTerrainMesh = interimMesh,
            BaseTerrainMesh = interimBaseMesh ?? interimMesh,
            GeometryRevision = buildVersion,
            OutputsRevision = previous.OutputsRevision
        };
        displayState.VisibleDiagnosticOwners.UnionWith(runtimeCache.VisibleDiagnosticOwners);
        displayState.AnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneAnalyses(previous.AnalysisResults));
        displayState.ZoneObjects.AddRange(previous.ZoneObjects);
        displayState.TerrainRegions.AddRange(previous.TerrainRegions.Select(region => region.Duplicate()));
        displayState.ZoneAnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneZoneAnalyses(previous.ZoneAnalysisResults));
        displayState.HardConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(previous.HardConstraints));
        displayState.ElevationConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(previous.ElevationConstraints));
        displayState.PreserveDeferredOutputsFrom(previous);
        displayState.IncludePreviousPreviewBounds(previousPreviewBounds);
        runtimeCache.DisplayState = displayState;
        TerrainLatencyTrace.Record(docSerial, terrainId, buildVersion, buildGeneration, TerrainBuildMode.Final, TerrainLatencyPhase.InterimCloned);
        UpdateRuntimePreview(doc, terrain, runtimeCache);
        TerrainLatencyTrace.Record(docSerial, terrainId, buildVersion, buildGeneration, TerrainBuildMode.Final, TerrainLatencyPhase.InterimPreview);
        NotifyRenderMeshesChanged(doc);
        terrain.LastBuildMessage =
            $"Terrain #{buildVersion:N0} shown; outputs from #{previous.OutputsRevision:N0} still updating.";
        doc.Views.Redraw();
        TerrainLatencyTrace.Record(
            docSerial,
            terrainId,
            buildVersion,
            buildGeneration,
            TerrainBuildMode.Final,
            TerrainLatencyPhase.InterimVisible,
            $"{interimMesh.Faces.Count:N0} faces; outputs stale at #{previous.OutputsRevision:N0}");
        // The outputs, and with them everything the cards show, are carried forward unchanged.
        RaiseStatusChanged();
    }

    /// <summary>
    /// Shows a build that a newer edit overtook, instead of throwing it away.
    ///
    /// This is the frame a gesture is made of. The build ran to completion and its geometry is exact
    /// for the input it was given; what it is not is *current*, so it publishes as a preview and every
    /// consumer that refuses a preview - bake, the interop mesh accessors, the Grasshopper bridge, all
    /// via <c>TerrainSnapshotEligibility</c> - refuses it unchanged.
    ///
    /// Deliberately much less than <see cref="ApplySuccessfulBuild"/>: no worker-cache merge, no
    /// document save, no owned-object sync, and <c>AppliedVersion</c> does not advance. None of that
    /// belongs on a frame that a newer build is already on its way to replace, and doing it would also
    /// record an older version as the applied one. Outputs are carried forward from the previous state
    /// and marked stale, exactly as an interim publication does.
    /// </summary>
    /// <returns>True when the frame was shown.</returns>
    private bool PublishSupersededGeometry(
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainRuntimeCache runtimeCache,
        BackgroundBuildResult result)
    {
        TerrainDisplayState? previous = runtimeCache.DisplayState;
        Mesh? mesh = result.Build?.PrimaryMesh;
        if (previous == null || mesh == null)
            return false;

        if (!TerrainSupersededBuildPolicy.ShouldPublishSupersededResult(
                hasMesh: true,
                result.BuildElapsed,
                result.Version,
                previous.GeometryRevision))
        {
            return false;
        }

        BoundingBox previousPreviewBounds = previous.GetContentBounds(doc);
        var displayState = new TerrainDisplayState
        {
            // Exact geometry, but not for the current input - which is what a preview is.
            IsPreview = true,
            HasDeferredOutputs = true,
            TerrainMesh = mesh,
            InterimTerrainMesh = mesh,
            BaseTerrainMesh = result.Build?.BaseMesh ?? mesh,
            GeometryRevision = result.Version,
            OutputsRevision = previous.OutputsRevision
        };
        displayState.VisibleDiagnosticOwners.UnionWith(runtimeCache.VisibleDiagnosticOwners);
        displayState.AnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneAnalyses(previous.AnalysisResults));
        displayState.ZoneObjects.AddRange(previous.ZoneObjects);
        displayState.TerrainRegions.AddRange(previous.TerrainRegions.Select(region => region.Duplicate()));
        displayState.ZoneAnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneZoneAnalyses(previous.ZoneAnalysisResults));
        displayState.HardConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(previous.HardConstraints));
        displayState.ElevationConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(previous.ElevationConstraints));
        displayState.PreserveDeferredOutputsFrom(previous);
        displayState.IncludePreviousPreviewBounds(previousPreviewBounds);
        runtimeCache.DisplayState = displayState;

        // A superseded build measured this terrain's cost as honestly as an applied one, and both the
        // debounce cadence and the cancel decision read it. Without this a gesture whose builds are all
        // superseded keeps steering by whatever the last *applied* build cost - which, after a slow
        // first build, means cancelling cheap builds for the rest of the session.
        runtimeCache.LastFinalDuration = result.BuildElapsed;

        UpdateRuntimePreview(doc, terrain, runtimeCache);
        NotifyRenderMeshesChanged(doc);
        terrain.LastBuildMessage =
            $"Terrain #{result.Version:N0} shown while editing; outputs from #{previous.OutputsRevision:N0}.";
        terrain.LastStructuredDiagnostics.Clear();
        doc.Views.Redraw();
        TerrainLatencyTrace.Record(
            doc.RuntimeSerialNumber,
            terrain.TerrainId,
            result.Version,
            result.Generation,
            result.Mode,
            TerrainLatencyPhase.SupersededPublished,
            $"{mesh.Faces.Count:N0} faces in {result.BuildElapsed.TotalMilliseconds:N0} ms");
        // Mid-gesture frame: outputs carried forward, so only the status moved. A full panel refresh here
        // relaid the card stack out (66 ms) in front of the build that superseded this one.
        RaiseStatusChanged();
        return true;
    }

    /// <summary>
    /// Tells the RDK that <see cref="TerrainRenderMeshProvider"/>'s cached primitives are stale, so an
    /// active render (Raytraced, V-Ray, …) picks up the new terrain instead of showing the previous
    /// build until it is restarted.
    ///
    /// The notification statics still live on the deprecated <c>CustomRenderMeshProvider</c> class —
    /// they were not carried over to <c>Rhino.Render.CustomRenderMeshes.RenderMeshProvider</c> in
    /// Rhino 8, and there is no replacement, so calling the obsolete API here is deliberate.
    /// </summary>
    private void InvalidateTerrainRenderMeshes(RhinoDoc doc, Guid terrainId)
    {
        GetRuntimeCache(doc.RuntimeSerialNumber, terrainId).DisplayState?.InvalidateRenderContent();
        NotifyRenderMeshesChanged(doc);
    }

    private void InvalidateDocumentRenderMeshes(RhinoDoc doc)
    {
        foreach (TerrainPreviewView view in GetPreviewViews(doc))
            view.DisplayState.InvalidateRenderContent();

        NotifyRenderMeshesChanged(doc);
    }

    internal static void NotifyRenderMeshesChanged(RhinoDoc doc)
    {
#pragma warning disable CS0612 // Type or member is obsolete
        global::Rhino.Render.CustomRenderMeshProvider.AllObjectsChanged(doc);
#pragma warning restore CS0612
    }

    private void UpdateRuntimePreview(RhinoDoc doc, TerrainDefinition terrain, TerrainRuntimeCache runtimeCache)
    {
        if (runtimeCache.DisplayState == null)
            return;

        if (runtimeCache.DisplayState.IsPreview)
        {
            runtimeCache.DisplayState.PreviewTerrainMesh = runtimeCache.DisplayState.TerrainMesh;
            runtimeCache.DisplayState.ActiveAnalysisId = null;
            runtimeCache.DisplayState.ActiveAnalysisLabel = null;
            runtimeCache.DisplayState.ActiveAnalysisRange = null;
            runtimeCache.DisplayState.ActiveAnalysisDistribution = null;
            RefreshLegends(doc, terrain, runtimeCache.DisplayState);
            terrain.LastAnalysisResults.Clear();
            return;
        }

        if (runtimeCache.DisplayState.OutputsAreStale)
        {
            // An interim publication shows new geometry beside the previous build's analyses. There is
            // no analysis for *this* mesh yet, and the previous one was computed against a different
            // face set - colouring these faces from it is meaningless as well as expensive (measured
            // 692 ms on 244k faces, on the UI thread, in the publication whose whole point is to be
            // quick). So preview plain until the real analysis lands, which is also the honest picture:
            // the colouring is not stale, it is absent.
            runtimeCache.DisplayState.PreviewTerrainMesh = runtimeCache.DisplayState.TerrainMesh;
            runtimeCache.DisplayState.ActiveAnalysisId = null;
            runtimeCache.DisplayState.ActiveAnalysisLabel = null;
            runtimeCache.DisplayState.ActiveAnalysisRange = null;
            runtimeCache.DisplayState.ActiveAnalysisDistribution = null;
            RefreshLegends(doc, terrain, runtimeCache.DisplayState);
            return;
        }

        TerrainAnalysisPreviewBuilder.UpdatePreviewMesh(
            doc,
            terrain,
            runtimeCache.DisplayState,
            referenceTerrainId => GetFinalTerrainMesh(doc, referenceTerrainId));
        terrain.LastAnalysisResults = TerrainRuntimeCacheCloner.CloneAnalyses(runtimeCache.DisplayState.AnalysisResults);
        StampActiveAnalysisRange(terrain, runtimeCache.DisplayState);
        RefreshLegends(doc, terrain, runtimeCache.DisplayState);
    }

    /// <summary>
    /// Redraws every Legend annotation from the colouring just applied. Legends are not built with the
    /// terrain: a ramp edit recolours without a rebuild, and a key drawn by the build would go stale on
    /// the first stop dragged. Regenerating here — after the range is stamped, from the same range and
    /// palette — keeps the key describing the colours on screen, and bake takes it from the same display
    /// state. With no colouring (a preview publication, an interim mesh, colours hidden) the legends are
    /// removed rather than left describing a colouring that is no longer shown.
    /// </summary>
    private static void RefreshLegends(RhinoDoc doc, TerrainDefinition terrain, TerrainDisplayState displayState)
    {
        displayState.AuxiliaryObjects.RemoveAll(item => item.Role == LayerRole.Legend);

        List<LegendAnnotationDefinition> legends = terrain.Annotations
            .OfType<LegendAnnotationDefinition>()
            .Where(item => item.IsEnabled)
            .ToList();
        if (legends.Count == 0 || displayState.TerrainMesh == null)
            return;

        LayerRoleTable layerRoles = LayerRoleService.GetTable(doc, terrain);
        AnnotationStyleSnapshot? style = null;
        BoundingBox bounds = displayState.TerrainMesh.GetBoundingBox(true);
        foreach (LegendAnnotationDefinition legend in legends)
        {
            TerrainLegendContent? content = TerrainLegendBuilder.ResolveContent(
                terrain, displayState, doc.ModelUnitSystem, legend, out _);
            if (content == null)
                continue;

            // Always the style's height: a legend has no size of its own (see LegendAnnotationDefinition).
            double textHeight = (style ??= AnnotationStyleService.Capture(
                doc, LayerRoleService.ResolveAnnotationStyleName(doc, terrain))).TextHeight;
            displayState.AuxiliaryObjects.AddRange(
                TerrainLegendBuilder.Build(legend, content, bounds, textHeight, layerRoles));
        }
    }

    /// <summary>
    /// Copies the range the preview mesh was just coloured with onto that analysis's summary. Colour
    /// settings recolour without scheduling a rebuild, so without this the panel legend would keep
    /// labelling the ramp with the range from the last full build.
    /// </summary>
    private static void StampActiveAnalysisRange(TerrainDefinition terrain, TerrainDisplayState displayState)
    {
        if (!displayState.ActiveAnalysisId.HasValue || displayState.ActiveAnalysisRange is not { } range)
            return;

        TerrainAnalysisSummary? summary = terrain.LastAnalysisResults
            .FirstOrDefault(item => item.AnalysisId == displayState.ActiveAnalysisId.Value);
        if (summary == null)
            return;

        summary.DisplayRangeLow = range.Low;
        summary.DisplayRangeHigh = range.High;
        summary.DistributionBins = displayState.ActiveAnalysisDistribution;
    }

    private static string DescribeDisplayState(TerrainDisplayState? displayState)
    {
        if (displayState == null)
            return "no preview";

        int outputCount = (displayState.PreviewTerrainMesh != null ? 1 : 0) +
                          displayState.ZoneObjects.Count +
                          displayState.AuxiliaryObjects.Count +
                          displayState.MarkerObjects.Count +
                          displayState.ScatterObjects.Count;
        return displayState.IsPreview
            ? $"{outputCount:N0} preview outputs; deferred exact outputs"
            : $"{outputCount:N0} preview outputs";
    }

    private void ClearLegacyOutputState(RhinoDoc doc, TerrainDefinition terrain)
    {
        if (AllOwnedIds(terrain).Any())
            DeleteOwnedObjects(doc, terrain);

        terrain.OutputObjectIds.Clear();
        terrain.ZoneObjectIds.Clear();
        terrain.AuxiliaryObjectIds.Clear();
        terrain.MarkerObjectIds.Clear();
    }

    private void SyncTerrainObjects(RhinoDoc doc, TerrainDefinition terrain, TerrainBuildResult build)
    {
        if (terrain.Objects.Count == 0)
            return;

        var placementsByDefinition = build.ObjectPlacements.ToDictionary(group => group.DefinitionId);
        var crossTerrainConflicts = CollectCrossTerrainObjectConflicts(
            doc,
            terrain.TerrainId,
            build.ObjectPlacements.SelectMany(group => group.Placements).Select(placement => placement.ObjectId));

        foreach (var objectId in crossTerrainConflicts.OrderBy(id => id))
            build.Diagnostics.Add($"Objects skipped {FormatObjectRef(objectId)} because it is matched by another terrain.");

        using var _ = new EventSuppression(this);
        foreach (var definition in terrain.Objects)
        {
            var currentStates = definition.PlacementStates
                .Where(state => state.ObjectId != Guid.Empty)
                .ToDictionary(state => state.ObjectId, state => state);

            var targetPlacements = placementsByDefinition.TryGetValue(definition.Id, out var placementGroup)
                ? placementGroup.Placements
                    .Where(placement => !crossTerrainConflicts.Contains(placement.ObjectId))
                    .GroupBy(placement => placement.ObjectId)
                    .ToDictionary(group => group.Key, group => group.Last())
                : new Dictionary<Guid, TerrainObjectPlacement>();

            foreach (var state in definition.PlacementStates.ToList())
            {
                if (targetPlacements.ContainsKey(state.ObjectId))
                    continue;

                TryRestorePlacementState(doc, state);
                definition.PlacementStates.Remove(state);
            }

            foreach (var pair in targetPlacements)
            {
                var placement = pair.Value;
                currentStates.TryGetValue(placement.ObjectId, out var state);
                Transform previousTransform = state?.GetLastAppliedTransform() ?? Transform.Identity;
                if (!TryCreatePlacementDelta(previousTransform, placement.AppliedTransform, out Transform delta))
                {
                    build.Diagnostics.Add($"Objects skipped {FormatObjectRef(placement.ObjectId)} because its placement delta could not be computed.");
                    continue;
                }

                Guid liveObjectId = placement.ObjectId;
                if (!TryTransformSourceObject(doc, liveObjectId, delta, out Guid newObjectId))
                {
                    build.Diagnostics.Add($"Objects skipped {FormatObjectRef(placement.ObjectId)} because Rhino could not apply the placement transform.");
                    continue;
                }

                if (newObjectId != liveObjectId)
                    ReplaceSourceObjectReferences(doc, liveObjectId, newObjectId);

                if (state == null)
                {
                    state = new TerrainObjectPlacementState
                    {
                        ObjectId = newObjectId
                    };
                    definition.PlacementStates.Add(state);
                }
                else
                {
                    state.ObjectId = newObjectId;
                }

                if (IsIdentityTransform(placement.AppliedTransform))
                    definition.PlacementStates.Remove(state);
                else
                    state.SetLastAppliedTransform(placement.AppliedTransform);
            }
        }
    }

    private HashSet<Guid> CollectCrossTerrainObjectConflicts(RhinoDoc doc, Guid terrainId, IEnumerable<Guid> candidateObjectIds)
    {
        var candidates = candidateObjectIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();

        if (candidates.Count == 0)
            return new HashSet<Guid>();

        var conflicts = new HashSet<Guid>();
        foreach (var otherTerrain in GetState(doc).Terrains)
        {
            if (otherTerrain.TerrainId == terrainId)
                continue;

            foreach (var definition in otherTerrain.Objects.Where(item => item.IsEnabled))
            {
                foreach (var obj in RhinoSourceResolver.ResolveObjects(doc, definition.Sources))
                {
                    if (candidates.Contains(obj.Id))
                        conflicts.Add(obj.Id);
                }
            }
        }

        return conflicts;
    }

    private bool TryRestorePlacementState(RhinoDoc doc, TerrainObjectPlacementState state)
    {
        Transform appliedTransform = state.GetLastAppliedTransform();
        if (IsIdentityTransform(appliedTransform))
            return true;

        if (!TryGetInverse(appliedTransform, out Transform inverse))
            return false;

        Guid liveObjectId = state.ObjectId;
        if (!TryTransformSourceObject(doc, liveObjectId, inverse, out Guid newObjectId))
            return false;

        if (newObjectId != liveObjectId)
            ReplaceSourceObjectReferences(doc, liveObjectId, newObjectId);

        state.ObjectId = newObjectId;
        state.SetLastAppliedTransform(Transform.Identity);
        return true;
    }

    private static bool TryCreatePlacementDelta(Transform previousTransform, Transform targetTransform, out Transform delta)
    {
        delta = Transform.Identity;
        if (!TryGetInverse(previousTransform, out Transform inversePrevious))
            return false;

        delta = targetTransform * inversePrevious;
        return true;
    }

    private static bool TryTransformSourceObject(RhinoDoc doc, Guid objectId, Transform transform, out Guid newObjectId)
    {
        newObjectId = objectId;
        if (objectId == Guid.Empty || IsIdentityTransform(transform))
            return objectId != Guid.Empty;

        newObjectId = doc.Objects.Transform(objectId, transform, deleteOriginal: true);
        return newObjectId != Guid.Empty;
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

    private static string FormatObjectRef(Guid objectId)
    {
        return objectId == Guid.Empty
            ? "object"
            : objectId.ToString("N")[..8];
    }

    private void ApplyVisibilityAndLock(RhinoDoc doc, TerrainDefinition terrain)
    {
        foreach (var id in terrain.OutputObjectIds)
        {
            if (!terrain.IsVisible || !terrain.ShowTerrainMesh)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }

        foreach (var id in terrain.ZoneObjectIds)
        {
            if (!terrain.IsVisible || !terrain.ShowZoneMeshes)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }

        foreach (var id in terrain.AuxiliaryObjectIds)
        {
            var obj = doc.Objects.FindId(id);
            bool shouldHide = !terrain.IsVisible ||
                              (!terrain.ShowSlopePreview && IsSlopePreviewObject(obj)) ||
                              (!terrain.ShowAnalysisOutputs && IsSlopePreviewObject(obj)) ||
                              !ShouldDisplayOwnedContentOutput(terrain, obj?.Attributes);
            if (shouldHide)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }

        foreach (var id in terrain.MarkerObjectIds)
        {
            if (!terrain.IsVisible)
                doc.Objects.Hide(id, ignoreLayerMode: true);
            else if (terrain.IsLocked)
                doc.Objects.Lock(id, ignoreLayerMode: true);
        }
    }

    private void ApplyDisplayState(RhinoDoc doc, TerrainDefinition terrain)
    {
        using var _ = new EventSuppression(this);
        ResetOwnedObjectModes(doc, terrain);
        ApplyOwnedDisplayMaterials(doc, terrain);
        ApplyVisibilityAndLock(doc, terrain);
        UnselectProtectedOwnedObjects(doc, terrain);
    }

    private void ResetOwnedObjectModes(RhinoDoc doc, TerrainDefinition terrain)
    {
        foreach (var id in AllOwnedIds(terrain))
        {
            doc.Objects.Show(id, ignoreLayerMode: true);
            doc.Objects.Unlock(id, ignoreLayerMode: true);
        }
    }

    private static IEnumerable<Guid> AllOwnedIds(TerrainDefinition terrain) =>
        terrain.OutputObjectIds
            .Concat(terrain.ZoneObjectIds)
            .Concat(terrain.AuxiliaryObjectIds)
            .Concat(terrain.MarkerObjectIds);

    /// <summary>
    /// Notices that a generated object has been dragged to another layer, and says why it will not
    /// stay there.
    ///
    /// This used to write the new layer back onto the terrain, which worked while each terrain owned
    /// its own output layer paths. Routing now lives in the layer template, which is shared by every
    /// terrain using it and, once pushed, by every future document — so silently rewriting it from a
    /// drag would be far more than the user asked for. The object goes back where the template says
    /// on the next rebuild, and the message points at the two ways to move it for real.
    /// </summary>
    private bool TrySyncOwnedObjectLayer(RhinoDoc doc, Guid objectId, int newLayerIndex)
    {
        string? newLayerPath = RhinoDocumentHelpers.GetLayerPath(doc, newLayerIndex);
        if (string.IsNullOrWhiteSpace(newLayerPath))
            return false;

        var state = GetState(doc);
        foreach (var terrain in state.Terrains)
        {
            if (!AllOwnedIds(terrain).Contains(objectId))
                continue;

            RhinoApp.WriteLine(
                $"MoleHill: '{terrain.Name}' output is placed by its layer template, so this move will "
                    + "not survive a rebuild. Rebind the role in the template editor, or untrack the "
                    + "object first to keep it where you put it.");
            return true;
        }

        return false;
    }

    private void OnRestoreStateUndo(object? sender, global::Rhino.Commands.CustomUndoEventArgs e)
    {
        if (e.Tag is not TerrainUndoSnapshot snapshot)
            return;

        TerrainUndoSnapshot inverse = CaptureUndoState(GetState(e.Document)).WithDescription(snapshot.Description);
        try
        {
            RestoreUndoState(e.Document, snapshot);
        }
        finally
        {
            // Registered even if the restore fails: without it Redo is lost for the rest of the
            // session, and the inverse snapshot is absolute, so it also repairs a partial restore.
            e.Document.AddCustomUndoEvent(snapshot.Description, OnRestoreStateUndo, inverse);
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void ApplyOwnedDisplayMaterials(RhinoDoc doc, TerrainDefinition terrain)
    {
        foreach (var objectId in AllOwnedIds(terrain).Distinct())
        {
            var obj = doc.Objects.FindId(objectId);
            if (obj?.Attributes == null)
                continue;

            var attributes = obj.Attributes.Duplicate();
            ApplyOutputRenderAttributes(doc, terrain, attributes);
            doc.Objects.ModifyAttributes(objectId, attributes, quiet: true);
        }
    }

    private void ApplyOutputRenderAttributes(RhinoDoc doc, TerrainDefinition terrain, ObjectAttributes attributes)
    {
        ApplyOutputWireAttributes(terrain, attributes);
        string? baseMaterialName = attributes.GetUserString(OutputBaseMaterialNameKey);
        if (string.IsNullOrWhiteSpace(baseMaterialName))
        {
            baseMaterialName = TryInferBaseMaterialName(doc, attributes);
            if (!string.IsNullOrWhiteSpace(baseMaterialName))
                attributes.SetUserString(OutputBaseMaterialNameKey, baseMaterialName);
        }

        double transparency = Math.Clamp(terrain.OutputTransparencyPercent, 0, 100) / 100.0;
        if (!string.IsNullOrWhiteSpace(baseMaterialName))
        {
            if (transparency <= 0)
            {
                int materialIndex = EnsureBaseMaterial(doc, baseMaterialName);
                if (materialIndex >= 0)
                {
                    attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                    attributes.MaterialIndex = materialIndex;
                }

                return;
            }

            int displayMaterialIndex = EnsureDisplayMaterial(
                doc,
                terrain.TerrainId,
                $"material:{baseMaterialName}",
                GetEffectiveDisplayColor(doc, attributes),
                transparency,
                baseMaterialName);
            if (displayMaterialIndex >= 0)
            {
                attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                attributes.MaterialIndex = displayMaterialIndex;
            }

            return;
        }

        attributes.DeleteUserString(OutputBaseMaterialNameKey);
        if (transparency <= 0)
        {
            attributes.MaterialSource = ObjectMaterialSource.MaterialFromLayer;
            return;
        }

        var effectiveColor = GetEffectiveDisplayColor(doc, attributes);
        int colorMaterialIndex = EnsureDisplayMaterial(
            doc,
            terrain.TerrainId,
            $"color:{effectiveColor.ToArgb():X8}",
            effectiveColor,
            transparency,
            null);
        if (colorMaterialIndex >= 0)
        {
            attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
            attributes.MaterialIndex = colorMaterialIndex;
        }
    }

    private static void ApplyOutputWireAttributes(TerrainDefinition terrain, ObjectAttributes attributes)
    {
        attributes.WireDensity = terrain.ShowMeshWires ? 1 : -1;
    }

    private static string? TryInferBaseMaterialName(RhinoDoc doc, ObjectAttributes attributes)
    {
        if (attributes.MaterialSource != ObjectMaterialSource.MaterialFromObject || attributes.MaterialIndex < 0)
            return null;

        if (attributes.MaterialIndex >= doc.Materials.Count)
            return null;

        var material = doc.Materials[attributes.MaterialIndex];
        if (material == null || string.IsNullOrWhiteSpace(material.Name) || IsOwnedDisplayMaterialName(material.Name))
            return null;

        return material.Name;
    }

    private static bool IsSlopePreviewObject(global::Rhino.DocObjects.RhinoObject? obj)
    {
        return GetGeneratedObjectKind(obj?.Attributes) == GeneratedObjectKind.SlopePreview;
    }

    /// <summary>Whether a baked output owned by an analysis or an annotation should be shown.</summary>
    private static bool ShouldDisplayOwnedContentOutput(TerrainDefinition terrain, ObjectAttributes? attributes)
    {
        Guid? ownerId = GetGeneratedAnalysisId(attributes);
        return !ownerId.HasValue || TerrainContentVisibility.IsOwnerVisible(terrain, ownerId.Value);
    }

    private static GeneratedObjectKind GetGeneratedObjectKind(ObjectAttributes? attributes)
    {
        if (attributes == null)
            return GeneratedObjectKind.Default;

        string? kind = attributes.GetUserString(OutputKindKey);
        return Enum.TryParse(kind, ignoreCase: true, out GeneratedObjectKind parsed)
            ? parsed
            : GeneratedObjectKind.Default;
    }

    private static Guid? GetGeneratedAnalysisId(ObjectAttributes? attributes)
    {
        if (attributes == null)
            return null;

        string? analysisId = attributes.GetUserString(OutputAnalysisIdKey);
        return Guid.TryParse(analysisId, out Guid parsed)
            ? parsed
            : null;
    }

    private int EnsureDisplayMaterial(
        RhinoDoc doc,
        Guid terrainId,
        string baseKey,
        System.Drawing.Color effectiveColor,
        double transparency,
        string? baseMaterialName)
    {
        string displayMaterialName = GetDisplayMaterialName(terrainId, baseKey);
        Material material = CreateDisplayMaterial(doc, effectiveColor, transparency, baseMaterialName);
        material.Name = displayMaterialName;

        int materialIndex = doc.Materials.Find(displayMaterialName, true);
        if (materialIndex >= 0)
        {
            doc.Materials.Modify(material, materialIndex, quiet: true);
            return materialIndex;
        }

        return doc.Materials.Add(material);
    }

    private Material CreateDisplayMaterial(
        RhinoDoc doc,
        System.Drawing.Color effectiveColor,
        double transparency,
        string? baseMaterialName)
    {
        Material material;
        if (!string.IsNullOrWhiteSpace(baseMaterialName))
        {
            int baseMaterialIndex = EnsureBaseMaterial(doc, baseMaterialName);
            material = baseMaterialIndex >= 0
                ? new Material(doc.Materials[baseMaterialIndex])
                : new Material();
            material.Transparency = Math.Max(material.Transparency, transparency);
            return material;
        }

        var opaqueColor = GetOpaqueColor(effectiveColor);
        material = new Material
        {
            DiffuseColor = opaqueColor,
            Transparency = transparency
        };
        return material;
    }

    private static System.Drawing.Color GetEffectiveDisplayColor(RhinoDoc doc, ObjectAttributes attributes)
    {
        if (attributes.ColorSource == ObjectColorSource.ColorFromObject)
            return GetOpaqueColor(attributes.ObjectColor);

        if (attributes.LayerIndex >= 0 && attributes.LayerIndex < doc.Layers.Count)
            return GetOpaqueColor(doc.Layers[attributes.LayerIndex].Color);

        return System.Drawing.Color.FromArgb(180, 180, 180);
    }

    private static System.Drawing.Color GetOpaqueColor(System.Drawing.Color color)
    {
        return System.Drawing.Color.FromArgb(color.R, color.G, color.B);
    }

    private int EnsureBaseMaterial(RhinoDoc doc, string materialName)
    {
        int materialIndex = doc.Materials.Find(materialName, true);
        if (materialIndex >= 0)
            return materialIndex;

        var material = new Material { Name = materialName };
        return doc.Materials.Add(material);
    }

    private void UnselectProtectedOwnedObjects(RhinoDoc doc, TerrainDefinition terrain)
    {
        if (!terrain.ProtectOutput)
            return;

        foreach (var objectId in AllOwnedIds(terrain).Distinct())
            doc.Objects.Select(objectId, false, true);
    }

    private bool ShouldBlockSelection(RhinoDoc doc, RhinoObject obj)
    {
        string? ownerValue = obj.Attributes.GetUserString(OutputOwnerKey);
        if (!Guid.TryParse(ownerValue, out var terrainId))
            return false;

        var terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        return terrain?.ProtectOutput == true;
    }

    private static bool IsOwnedDisplayMaterialName(string? materialName)
    {
        return !string.IsNullOrWhiteSpace(materialName) &&
            materialName.StartsWith(OutputDisplayMaterialPrefix, StringComparison.Ordinal);
    }

    private static string GetDisplayMaterialName(Guid terrainId, string baseKey)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseKey)));
        return $"{OutputDisplayMaterialPrefix}{terrainId:N}_{hash[..16]}";
    }

    public void SetTerrainVisible(RhinoDoc doc, Guid terrainId, bool visible)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(
            doc, visible ? "Show MoleHill Terrain" : "Hide MoleHill Terrain");
        terrain.IsVisible = visible;
        Save(doc, state);
        // The render mesh provider gates on IsVisible, so hiding/showing changes what renders too.
        InvalidateTerrainRenderMeshes(doc, terrainId);
        doc.Views.Redraw();
    }

    public void SetTerrainLocked(RhinoDoc doc, Guid terrainId, bool locked)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(
            doc, locked ? "Lock MoleHill Terrain" : "Unlock MoleHill Terrain");
        terrain.IsLocked = locked;
        Save(doc, state);
        doc.Views.Redraw();
    }

    public void RefreshTerrainDisplay(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return;

        UpdateRuntimePreview(doc, terrain, GetRuntimeCache(doc.RuntimeSerialNumber, terrainId));
        InvalidateTerrainRenderMeshes(doc, terrainId);
        ApplyDisplayState(doc, terrain);
        doc.Views.Redraw();
    }
}
