using MoleHill.Rhino.Model;
using MoleHill.Core.Engine;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The published result of one terrain build: meshes, generated objects and overlays that the display
/// conduit draws and the render mesh provider publishes. Immutable after a build and swapped atomically.
/// </summary>
internal sealed class TerrainDisplayState
{
    private static long _renderVersionCounter;

    public bool IsPreview { get; set; }

    /// <summary>
    /// Change stamp handed to the RDK custom render mesh cache (see <see cref="TerrainRenderMeshProvider"/>).
    /// A display state is immutable after a build and swapped atomically, so a fresh sequence number per
    /// state is a strictly correct "did the renderable content change" signal — and far cheaper than
    /// hashing megavertex meshes. The sculpt session is the one exception: it mutates
    /// <see cref="PreviewTerrainMesh"/> in place, so it re-stamps via <see cref="InvalidatePreviewBounds"/>.
    /// </summary>
    public uint RenderHash { get; private set; } = NextRenderVersion();

    private static uint NextRenderVersion() =>
        unchecked((uint)Interlocked.Increment(ref _renderVersionCounter));

    /// <summary>
    /// Advances the RDK cache stamp after a display-only setting, material, or mutable preview mesh
    /// changes without replacing this display-state object.
    /// </summary>
    public void InvalidateRenderContent() => RenderHash = NextRenderVersion();

    /// <summary>
    /// True while the dependent outputs on this state are older than its geometry. Already gates bake,
    /// the interop mesh accessors and the Grasshopper bridge, so a partially published build cannot be
    /// mistaken for a completed one. <see cref="OutputsAreStale"/> is the same fact derived from the
    /// revisions; this flag stays because Preview builds set it without advancing a geometry revision.
    /// </summary>
    public bool HasDeferredOutputs { get; set; }

    /// <summary>
    /// The build version that produced the geometry on this state. Advances when a build publishes its
    /// mesh - including an interim publication made before the dependent outputs have finished.
    /// </summary>
    public long GeometryRevision { get; set; }

    /// <summary>
    /// The build version that produced the analyses, zones, markers, objects and scatter on this state.
    /// Lags <see cref="GeometryRevision"/> exactly while an interim publication is on screen, which is
    /// what lets the UI say the drawing is from an earlier edit instead of implying it is current.
    /// </summary>
    public long OutputsRevision { get; set; }

    /// <summary>The outputs on screen describe an earlier geometry than the mesh on screen.</summary>
    public bool OutputsAreStale => OutputsRevision < GeometryRevision;

    /// <summary>
    /// Set only on an interim publication. The worker keeps building against its own mesh after the
    /// interim one is shown, so the displayed copy has to be a copy - otherwise a later stage could
    /// mutate geometry the conduit is already drawing. Deliberately not disposed here: the conduit may
    /// still be mid-draw when a state is replaced, and the existing displaced-mesh machinery
    /// (DisposeDisplacedCacheMeshesWhenSafe) covers build-owned meshes, not this one. See
    /// docs/build-result-ownership.md.
    /// </summary>
    public Mesh? InterimTerrainMesh { get; set; }

    public Mesh? TerrainMesh { get; set; }

    public Mesh? BaseTerrainMesh { get; set; }

    public Mesh? PreviewTerrainMesh { get; set; }

    /// <summary>The Catchments/Ponding preview's last solve, so a colour edit only recolours.</summary>
    internal DrainagePreviewCache DrainagePreview { get; } = new();

    public Guid? ActiveAnalysisId { get; set; }

    public string? ActiveAnalysisLabel { get; set; }

    /// <summary>
    /// The value range the active analysis is currently mapping across its palette, as resolved when the
    /// preview mesh was coloured. The panel legend reads this rather than the last build's
    /// <see cref="TerrainAnalysisSummary"/>, so changing a colour setting — which recolours without
    /// rebuilding — updates the numbers beside the ramp immediately.
    /// </summary>
    public MoleHill.Core.Analysis.AnalysisRange? ActiveAnalysisRange { get; set; }

    /// <summary>
    /// The distribution of the active analysis's values across that range, as normalized histogram bars.
    /// Lives beside <see cref="ActiveAnalysisRange"/> for the same reason: a colour edit recolours without
    /// rebuilding, and the card's histogram has to move with the range it sits behind.
    /// </summary>
    public double[]? ActiveAnalysisDistribution { get; set; }

    public List<TerrainAnalysisSummary> AnalysisResults { get; } = new();

    public List<GeneratedRhinoObject> ZoneObjects { get; } = new();

    public List<TerrainRegionState> TerrainRegions { get; } = new();

    public List<ZoneAnalysisSummary> ZoneAnalysisResults { get; } = new();

    public List<GeneratedRhinoObject> AuxiliaryObjects { get; } = new();

    public List<GeneratedRhinoObject> MarkerObjects { get; } = new();

    public List<GeneratedRhinoObject> ScatterObjects { get; } = new();

    public Dictionary<Guid, ScatterObjectRange> ScatterObjectRanges { get; } = new();

    public List<RuntimeOverlayItem> RuntimeOverlays { get; } = new();

    public List<SurfaceRemesher.ConstraintPolyline> HardConstraints { get; } = new();

    public List<SurfaceRemesher.ConstraintPolyline> ElevationConstraints { get; } = new();

    public HashSet<RuntimeOverlayOwner> VisibleDiagnosticOwners { get; } = new();

    private BoundingBox? _previewBounds;
    private BoundingBox? _previousPreviewBounds;

    /// <summary>
    /// Drops the cached preview bounds. Only the sculpt session needs this: it mutates
    /// <see cref="PreviewTerrainMesh"/> vertices in place between builds (a deliberate, gated exception
    /// to the swap-only convention), and stale bounds would clip viewport invalidation once strokes
    /// exceed the original bounding box.
    /// </summary>
    public void InvalidatePreviewBounds()
    {
        _previewBounds = null;
        TerrainPresentationMesh.Invalidate(PreviewTerrainMesh);
        TerrainPresentationMesh.Invalidate(TerrainMesh);
        // Same mesh instance, different heights: a drainage solve keyed on the instance is now wrong.
        DrainagePreview.Clear();
        // The mesh changed under a state object that is otherwise immutable, so the RDK's cached
        // render primitives for this terrain are now stale too.
        InvalidateRenderContent();
    }

    /// <summary>
    /// Drops the cached bounds when only what is drawn <em>over</em> the terrain changed, such as which
    /// runtime overlays are shown. The mesh is untouched, so its drainage solve and presentation mesh
    /// stay valid; clearing them here re-solved Catchments/Ponding (~1 s on a 244k-face terrain) for an
    /// overlay toggle.
    /// </summary>
    public void InvalidateOverlayBounds()
    {
        _previewBounds = null;
        InvalidateRenderContent();
    }

    /// <summary>True when the current visibility settings leave at least one mesh-capable render item.</summary>
    internal bool HasRenderableContent(TerrainDefinition terrain)
    {
        if (terrain.ShowTerrainMesh && IsRenderableMesh(PreviewTerrainMesh ?? TerrainMesh))
            return true;

        if (terrain.ShowZoneMeshes && ZoneObjects.Any(IsPotentiallyRenderable))
            return true;

        if (AuxiliaryObjects.Any(item =>
                TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(terrain, item) &&
                IsPotentiallyRenderable(item)))
            return true;

        return MarkerObjects.Any(IsPotentiallyRenderable) ||
               ScatterObjects.Any(IsPotentiallyRenderable);
    }

    private static bool IsRenderableMesh(Mesh? mesh) => mesh is { Faces.Count: > 0 };

    private static bool IsPotentiallyRenderable(GeneratedRhinoObject generated)
    {
        return generated.Geometry switch
        {
            Mesh mesh => IsRenderableMesh(mesh),
            Brep => true,
            Extrusion => true,
            _ => !string.IsNullOrWhiteSpace(generated.InstanceDefinitionName)
        };
    }

    /// <summary>
    /// Union of the extents this state draws through the display conduit (terrain mesh, generated
    /// objects, scatter instance origins). Cached once — the state is immutable after a build and is
    /// swapped atomically. The conduit feeds this to <c>CalculateBoundingBox</c> so Rhino invalidates
    /// and redraws the scatter region on incremental operations; without it, partial redraws can leave
    /// ghost pixels of the previous frame (looks like duplicate instances, though bake is unaffected).
    /// </summary>
    public void IncludePreviousPreviewBounds(BoundingBox bounds)
    {
        if (!bounds.IsValid)
            return;

        if (_previousPreviewBounds.HasValue)
        {
            BoundingBox combined = _previousPreviewBounds.Value;
            combined.Union(bounds);
            _previousPreviewBounds = combined;
        }
        else
        {
            _previousPreviewBounds = bounds;
        }

        _previewBounds = null;
    }

    public BoundingBox GetPreviewBounds(global::Rhino.RhinoDoc? doc = null)
    {
        if (_previewBounds.HasValue)
            return _previewBounds.Value;

        var bounds = BoundingBox.Empty;
        var definitionBoundsCache = new Dictionary<string, BoundingBox?>(StringComparer.Ordinal);
        if (_previousPreviewBounds.HasValue)
            bounds.Union(_previousPreviewBounds.Value);
        UnionMesh(ref bounds, PreviewTerrainMesh);
        UnionMesh(ref bounds, TerrainMesh);

        foreach (var generated in ZoneObjects)
            UnionGeneratedObject(ref bounds, generated, doc, definitionBoundsCache);
        foreach (var generated in AuxiliaryObjects)
            UnionGeneratedObject(ref bounds, generated, doc, definitionBoundsCache);
        foreach (var generated in MarkerObjects)
            UnionGeneratedObject(ref bounds, generated, doc, definitionBoundsCache);

        foreach (var scatter in ScatterObjects)
            UnionGeneratedObject(ref bounds, scatter, doc, definitionBoundsCache);

        foreach (RuntimeOverlayItem overlay in RuntimeOverlays)
        {
            bool isVisible = overlay.Channel == RuntimeOverlayChannel.Guide ||
                             VisibleDiagnosticOwners.Contains(overlay.Owner);
            if (!isVisible)
                continue;

            BoundingBox overlayBounds = overlay.GetBounds();
            if (overlayBounds.IsValid)
                bounds.Union(overlayBounds);
        }

        if (bounds.IsValid)
        {
            // Scatter instances (and markers/text) extend beyond their origin point; pad the box so
            // their full footprint stays inside the invalidated/redrawn region.
            double diagonal = bounds.Diagonal.Length;
            double margin = Math.Max(diagonal * 0.05, 1.0);
            bounds.Inflate(margin);
        }

        _previewBounds = bounds;
        return bounds;
    }

    private static void UnionMesh(ref BoundingBox bounds, Mesh? mesh)
    {
        if (mesh == null)
            return;

        BoundingBox meshBounds = mesh.GetBoundingBox(true);
        if (meshBounds.IsValid)
            bounds.Union(meshBounds);
    }

    private static void UnionGeneratedObject(
        ref BoundingBox bounds,
        GeneratedRhinoObject generated,
        global::Rhino.RhinoDoc? doc,
        IDictionary<string, BoundingBox?> definitionBoundsCache)
    {
        if (generated.Geometry != null)
            UnionTransformedBounds(ref bounds, generated.Geometry.GetBoundingBox(true), generated.InstanceTransform);

        if (doc == null || string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
            return;

        string definitionName = generated.InstanceDefinitionName!;
        if (!definitionBoundsCache.TryGetValue(definitionName, out BoundingBox? definitionBounds))
        {
            var definition = doc.InstanceDefinitions.Find(definitionName);
            if (definition == null)
            {
                definitionBoundsCache[definitionName] = null;
                return;
            }

            BoundingBox calculatedBounds = BoundingBox.Empty;
            foreach (var instanceObject in definition.GetObjects())
            {
                GeometryBase? geometry = instanceObject?.Geometry;
                if (geometry == null)
                    continue;

                BoundingBox geometryBounds = geometry.GetBoundingBox(true);
                if (geometryBounds.IsValid)
                    calculatedBounds.Union(geometryBounds);
            }

            definitionBounds = calculatedBounds.IsValid ? calculatedBounds : null;
            definitionBoundsCache[definitionName] = definitionBounds;
        }

        if (definitionBounds.HasValue)
            UnionTransformedBounds(ref bounds, definitionBounds.Value, generated.InstanceTransform);
    }

    private static void UnionTransformedBounds(
        ref BoundingBox bounds,
        BoundingBox sourceBounds,
        Transform transform)
    {
        if (!sourceBounds.IsValid)
            return;

        foreach (Point3d corner in sourceBounds.GetCorners())
        {
            Point3d transformed = corner;
            transformed.Transform(transform);
            if (transformed.IsValid)
                bounds.Union(transformed);
        }
    }

    public void RebuildScatterObjectRanges()
    {
        ScatterObjectRanges.Clear();
        for (int i = 0; i < ScatterObjects.Count; i++)
        {
            Guid key = ScatterObjects[i].ScatterDefinitionId ?? Guid.Empty;
            if (ScatterObjectRanges.TryGetValue(key, out var range))
            {
                ScatterObjectRanges[key] = range.ExtendToInclude(i);
                continue;
            }

            ScatterObjectRanges[key] = new ScatterObjectRange(i, 1);
        }
    }

    /// <summary>
    /// Keeps the last completed annotation and auxiliary outputs visible while a fast preview build is
    /// waiting for its deferred final pass. Preview builds intentionally do not regenerate these objects;
    /// dropping the previous set here makes every live edit make annotations vanish for the duration of
    /// the preview, even though the last completed drawing is still valid enough to show.
    /// </summary>
    internal void PreserveDeferredOutputsFrom(TerrainDisplayState previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        AuxiliaryObjects.AddRange(previous.AuxiliaryObjects);
        MarkerObjects.AddRange(previous.MarkerObjects);
        ScatterObjects.AddRange(previous.ScatterObjects);
        RebuildScatterObjectRanges();
    }

    public TerrainDisplayState Clone()
    {
        var clone = new TerrainDisplayState
        {
            IsPreview = IsPreview,
            HasDeferredOutputs = HasDeferredOutputs,
            GeometryRevision = GeometryRevision,
            OutputsRevision = OutputsRevision,
            TerrainMesh = TerrainRuntimeCacheCloner.CloneMesh(TerrainMesh),
            BaseTerrainMesh = TerrainRuntimeCacheCloner.CloneMesh(BaseTerrainMesh),
            PreviewTerrainMesh = TerrainRuntimeCacheCloner.CloneMesh(PreviewTerrainMesh),
            ActiveAnalysisId = ActiveAnalysisId,
            ActiveAnalysisLabel = ActiveAnalysisLabel,
            ActiveAnalysisRange = ActiveAnalysisRange,
            ActiveAnalysisDistribution = ActiveAnalysisDistribution?.ToArray()
        };
        if (_previousPreviewBounds.HasValue)
            clone._previousPreviewBounds = _previousPreviewBounds.Value;
        clone.AnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneAnalyses(AnalysisResults));
        clone.ZoneObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(ZoneObjects));
        clone.TerrainRegions.AddRange(TerrainRegions.Select(region => region.Duplicate()));
        clone.ZoneAnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneZoneAnalyses(ZoneAnalysisResults));
        clone.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(AuxiliaryObjects));
        clone.MarkerObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(MarkerObjects));
        clone.ScatterObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(ScatterObjects));
        clone.RuntimeOverlays.AddRange(TerrainRuntimeCacheCloner.CloneRuntimeOverlays(RuntimeOverlays));
        clone.HardConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(HardConstraints));
        clone.ElevationConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(ElevationConstraints));
        clone.VisibleDiagnosticOwners.UnionWith(VisibleDiagnosticOwners);
        clone.RebuildScatterObjectRanges();
        return clone;
    }
}

internal readonly record struct ScatterObjectRange(int StartIndex, int Count)
{
    public int EndExclusive => StartIndex + Count;

    public ScatterObjectRange ExtendToInclude(int index)
    {
        int start = Math.Min(StartIndex, index);
        int end = Math.Max(EndExclusive, index + 1);
        return new ScatterObjectRange(start, end - start);
    }
}
