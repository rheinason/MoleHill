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

    public bool HasDeferredOutputs { get; set; }

    public Mesh? TerrainMesh { get; set; }

    public Mesh? BaseTerrainMesh { get; set; }

    public Mesh? PreviewTerrainMesh { get; set; }

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
        // The mesh changed under a state object that is otherwise immutable, so the RDK's cached
        // render primitives for this terrain are now stale too.
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

    public TerrainDisplayState Clone()
    {
        var clone = new TerrainDisplayState
        {
            IsPreview = IsPreview,
            HasDeferredOutputs = HasDeferredOutputs,
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
