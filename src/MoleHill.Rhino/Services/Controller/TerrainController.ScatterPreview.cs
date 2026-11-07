using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Native reference instances retain the original block definitions (and asset metadata) for
// renderers which do not consume RDK non-object meshes. Reference objects are never saved to 3dm.
internal sealed partial class TerrainController
{
    private readonly Dictionary<(uint Document, Guid Terrain), List<(Guid Id, string Block, Transform Pose)>> _scatterPreviewInstances = new();

    public IReadOnlyList<ScatterBuildSummary> GetScatterSummaries(RhinoDoc doc, Guid terrainId)
    {
        if (!_runtimeCaches.TryGetValue((doc.RuntimeSerialNumber, terrainId), out var cache) ||
            cache.DisplayState is not { IsPreview: false, HasDeferredOutputs: false } display ||
            display.OutputsAreStale || HasPendingFinalBuild(doc.RuntimeSerialNumber, terrainId) ||
            _unbuiltEdits.Contains((doc.RuntimeSerialNumber, terrainId)))
            return Array.Empty<ScatterBuildSummary>();
        return display.ScatterSummaries;
    }

    private void ClearScatterPreview(RhinoDoc doc, Guid terrainId)
    {
        if (!_scatterPreviewInstances.Remove((doc.RuntimeSerialNumber, terrainId), out var instances))
            return;
        using var suppression = new EventSuppression(this);
        foreach (var instance in instances)
        {
            if (doc.Objects.FindId(instance.Id) is RhinoObject obj)
                doc.Objects.Purge(obj);
        }
    }

    private void SyncScatterPreview(RhinoDoc doc, TerrainDefinition terrain, TerrainDisplayState display)
    {
        var desired = new List<GeneratedRhinoObject>();
        if (terrain.IsVisible)
        {
            foreach (ScatterObjectDefinition definition in terrain.Objects.OfType<ScatterObjectDefinition>())
            {
                if (!definition.IsEnabled || definition.PreviewMode != ScatterPreviewMode.RenderInstances)
                    continue;
                int cap = definition.PreviewCap > 0 ? definition.PreviewCap : int.MaxValue;
                desired.AddRange(display.ScatterObjects.Where(item => item.ScatterDefinitionId == definition.Id).Take(cap));
            }
        }

        desired = desired.GroupBy(item => (item.ScatterDefinitionId, item.InstanceDefinitionName)).SelectMany(group => group).ToList();
        var key = (doc.RuntimeSerialNumber, terrain.TerrainId);
        if (desired.Count == 0 && !_scatterPreviewInstances.ContainsKey(key))
            return;
        if (_scatterPreviewInstances.TryGetValue(key, out var current) && current.Count == desired.Count &&
            current.Select((item, i) => item.Block == desired[i].InstanceDefinitionName &&
                item.Pose == desired[i].InstanceTransform && doc.Objects.FindId(item.Id) != null).All(equal => equal))
            return;

        using var suppression = new EventSuppression(this);
        bool redraw = doc.Views.RedrawEnabled;
        doc.Views.RedrawEnabled = false;
        try
        {
            ClearScatterPreview(doc, terrain.TerrainId);
            var instances = new List<(Guid, string, Transform)>();
            _scatterPreviewInstances[key] = instances;
            foreach (var group in desired.GroupBy(item => (item.ScatterDefinitionId, item.InstanceDefinitionName)))
            {
                GeneratedRhinoObject first = group.First();
                InstanceDefinition? block = doc.InstanceDefinitions.Find(first.InstanceDefinitionName);
                if (block == null)
                    continue;
                using ObjectAttributes attributes = CreateAttributes(doc, terrain, first, trackOwnership: false);
                ApplyScatterAssetAttributes(attributes, block);
                attributes.SetUserString(RhinoSourceResolver.ScatterPreviewKey, terrain.TerrainId.ToString());
                attributes.Mode = ObjectMode.Locked;
                foreach (GeneratedRhinoObject item in group)
                {
                    Guid id = doc.Objects.AddInstanceObject(block.Index, item.InstanceTransform, attributes, null, reference: true);
                    if (id != Guid.Empty)
                        instances.Add((id, block.Name, item.InstanceTransform));
                }
            }
        }
        finally
        {
            doc.Views.RedrawEnabled = redraw;
        }
        display.InvalidateRenderContent();
        NotifyRenderMeshesChanged(doc);
    }

    private static void ApplyScatterAssetAttributes(ObjectAttributes attributes, InstanceDefinition block)
    {
        // Asset identification may also live on the source instance, rather than its definition.
        foreach (InstanceObject source in block.GetReferences(0) ?? Array.Empty<InstanceObject>())
        {
            var strings = source.Attributes.GetUserStrings();
            foreach (string? key in strings.AllKeys)
            {
                if (key != null && key.StartsWith("Enscape", StringComparison.OrdinalIgnoreCase))
                    attributes.SetUserString(key, strings[key]);
            }
        }
    }
}
