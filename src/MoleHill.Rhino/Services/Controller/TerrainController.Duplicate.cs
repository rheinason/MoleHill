using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

// Duplicating a terrain copies what the terrain owns, so the copy can be edited without moving the
// original: its input layers and the geometry on them, and the objects its Objects cards place.
// Sources outside the terrain's layer root stay shared by reference.
internal sealed partial class TerrainController
{
    /// <summary>
    /// Copies the original's owned input layers and the objects on them into the clone's layer root,
    /// and copies every object an Objects card names from its un-placed pose. Returns the map from
    /// each original object id to its copy, for repointing the clone's sources.
    /// </summary>
    private Dictionary<Guid, Guid> CopyOwnedInputs(
        RhinoDoc doc,
        TerrainDefinition original,
        TerrainDefinition clone,
        LayerRoleTable oldTable,
        LayerRoleTable newTable)
    {
        var map = new Dictionary<Guid, Guid>();
        var generated = new HashSet<Guid>(AllOwnedIds(original).Concat(original.BakedObjectIds));
        var inverses = PlacementInverses(original);

        using var _ = new EventSuppression(this);

        var ownedLayers = doc.Layers
            .Where(layer => !layer.IsDeleted && TerrainOwnership.IsOwnedInputLayer(layer.FullPath, original.Name, oldTable))
            .OrderBy(layer => layer.FullPath.Length)
            .ToList();

        foreach (Layer oldLayer in ownedLayers)
        {
            string? newPath = TerrainLayerNaming.Rebase(oldLayer.FullPath, original.Name, clone.Name);
            if (newPath == null)
                continue;

            bool existed = doc.Layers.FindByFullPath(newPath, -1) >= 0;
            int newIndex = LayerCreationService.EnsureLayerPath(doc, newPath, newTable);
            if (!existed && newIndex >= 0)
                CopyLayerLook(doc, oldLayer, newIndex);

            RhinoObject[]? objects = doc.Objects.FindByLayer(oldLayer);
            if (objects == null)
                continue;

            foreach (RhinoObject source in objects)
            {
                if (generated.Contains(source.Id) || map.ContainsKey(source.Id))
                    continue;

                Guid copy = CopyObject(doc, source.Id, inverses, newIndex);
                if (copy != Guid.Empty)
                    map[source.Id] = copy;
            }
        }

        foreach (TerrainObjectDefinition definition in original.Objects)
        {
            foreach (Guid sourceId in definition.Sources.ObjectIds)
            {
                if (map.ContainsKey(sourceId) || generated.Contains(sourceId))
                    continue;

                Guid copy = CopyObject(doc, sourceId, inverses, layerIndex: -1);
                if (copy != Guid.Empty)
                    map[sourceId] = copy;
            }
        }

        return map;
    }

    private static Dictionary<Guid, Transform> PlacementInverses(TerrainDefinition terrain)
    {
        var inverses = new Dictionary<Guid, Transform>();
        foreach (TerrainObjectDefinition definition in terrain.Objects)
        {
            foreach (TerrainObjectPlacementState state in definition.PlacementStates)
            {
                if (TryGetInverse(state.GetLastAppliedTransform(), out Transform inverse))
                    inverses[state.ObjectId] = inverse;
            }
        }

        return inverses;
    }

    private static Guid CopyObject(RhinoDoc doc, Guid objectId, IReadOnlyDictionary<Guid, Transform> inverses, int layerIndex)
    {
        if (doc.Objects.FindId(objectId) == null)
            return Guid.Empty;

        Transform pose = inverses.TryGetValue(objectId, out Transform inverse) ? inverse : Transform.Identity;
        Guid copyId = doc.Objects.Transform(objectId, pose, deleteOriginal: false);
        if (copyId == Guid.Empty || layerIndex < 0)
            return copyId;

        RhinoObject? copy = doc.Objects.FindId(copyId);
        if (copy != null)
        {
            ObjectAttributes attributes = copy.Attributes.Duplicate();
            attributes.LayerIndex = layerIndex;
            doc.Objects.ModifyAttributes(copy, attributes, quiet: true);
        }

        return copyId;
    }

    private static void CopyLayerLook(RhinoDoc doc, Layer source, int targetIndex)
    {
        Layer target = doc.Layers[targetIndex];
        target.Color = source.Color;
        target.PlotColor = source.PlotColor;
        target.PlotWeight = source.PlotWeight;
        target.LinetypeIndex = source.LinetypeIndex;
        doc.Layers.Modify(target, targetIndex, quiet: true);
    }
}
