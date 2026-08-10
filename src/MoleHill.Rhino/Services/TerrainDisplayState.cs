using MoleHill.Rhino.Model;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainDisplayState
{
    public bool IsPreview { get; set; }

    public bool HasDeferredOutputs { get; set; }

    public Mesh? TerrainMesh { get; set; }

    public Mesh? BaseTerrainMesh { get; set; }

    public Mesh? PreviewTerrainMesh { get; set; }

    public Guid? ActiveAnalysisId { get; set; }

    public string? ActiveAnalysisLabel { get; set; }

    public List<TerrainAnalysisSummary> AnalysisResults { get; } = new();

    public List<GeneratedRhinoObject> ZoneObjects { get; } = new();

    public List<GeneratedRhinoObject> AuxiliaryObjects { get; } = new();

    public List<GeneratedRhinoObject> MarkerObjects { get; } = new();

    public List<GeneratedRhinoObject> ScatterObjects { get; } = new();

    public Dictionary<Guid, ScatterObjectRange> ScatterObjectRanges { get; } = new();

    public List<RuntimeOverlayItem> RuntimeOverlays { get; } = new();

    public HashSet<RuntimeOverlayOwner> VisibleDiagnosticOwners { get; } = new();

    private BoundingBox? _previewBounds;

    /// <summary>
    /// Drops the cached preview bounds. Only the sculpt session needs this: it mutates
    /// <see cref="PreviewTerrainMesh"/> vertices in place between builds (a deliberate, gated exception
    /// to the swap-only convention), and stale bounds would clip viewport invalidation once strokes
    /// exceed the original bounding box.
    /// </summary>
    public void InvalidatePreviewBounds() => _previewBounds = null;

    /// <summary>
    /// Union of the extents this state draws through the display conduit (terrain mesh, generated
    /// objects, scatter instance origins). Cached once — the state is immutable after a build and is
    /// swapped atomically. The conduit feeds this to <c>CalculateBoundingBox</c> so Rhino invalidates
    /// and redraws the scatter region on incremental operations; without it, partial redraws can leave
    /// ghost pixels of the previous frame (looks like duplicate instances, though bake is unaffected).
    /// </summary>
    public BoundingBox GetPreviewBounds()
    {
        if (_previewBounds.HasValue)
            return _previewBounds.Value;

        var bounds = BoundingBox.Empty;
        UnionMesh(ref bounds, PreviewTerrainMesh);
        UnionMesh(ref bounds, TerrainMesh);

        foreach (var generated in ZoneObjects)
            UnionGeometry(ref bounds, generated.Geometry);
        foreach (var generated in AuxiliaryObjects)
            UnionGeometry(ref bounds, generated.Geometry);
        foreach (var generated in MarkerObjects)
            UnionGeometry(ref bounds, generated.Geometry);

        foreach (var scatter in ScatterObjects)
        {
            Point3d origin = Point3d.Origin;
            origin.Transform(scatter.InstanceTransform);
            if (origin.IsValid)
                bounds.Union(origin);
        }

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

    private static void UnionGeometry(ref BoundingBox bounds, GeometryBase? geometry)
    {
        if (geometry == null)
            return;

        BoundingBox geometryBounds = geometry.GetBoundingBox(true);
        if (geometryBounds.IsValid)
            bounds.Union(geometryBounds);
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
            ActiveAnalysisLabel = ActiveAnalysisLabel
        };
        clone.AnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneAnalyses(AnalysisResults));
        clone.ZoneObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(ZoneObjects));
        clone.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(AuxiliaryObjects));
        clone.MarkerObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(MarkerObjects));
        clone.ScatterObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(ScatterObjects));
        clone.RuntimeOverlays.AddRange(TerrainRuntimeCacheCloner.CloneRuntimeOverlays(RuntimeOverlays));
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
