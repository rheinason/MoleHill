using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Tidies a deleted terrain's layer tree. Output layers empty out when the terrain's generated
/// objects are deleted; this removes the layers that are left with nothing on them, deepest first.
/// A layer that still holds geometry (the terrain's survey inputs, anything the user drew there, or a
/// baked copy) keeps its ancestors, so the tree only ever shrinks down to what the user still has.
/// </summary>
internal static class TerrainLayerCleanup
{
    /// <summary>
    /// Removes the empty layers under the terrain's own root and returns how many went. Does nothing
    /// for a template whose layers are not per terrain: a shared root belongs to every terrain that
    /// uses it.
    /// </summary>
    public static int RemoveEmptyLayers(RhinoDoc doc, string terrainName, LayerRoleTable table)
    {
        if (!TerrainLayerNaming.IsUnderRoot(table.Path(LayerRole.Terrain), terrainName))
            return 0;

        var layers = doc.Layers
            .Where(layer => !layer.IsDeleted && TerrainLayerNaming.IsUnderRoot(layer.FullPath, terrainName))
            .OrderByDescending(layer => layer.FullPath.Length)
            .ToList();

        int removed = 0;
        foreach (Layer layer in layers)
        {
            if (layer.Index == doc.Layers.CurrentLayerIndex)
                continue;

            RhinoObject[]? objects = doc.Objects.FindByLayer(layer);
            if (objects != null && objects.Length > 0)
                continue;

            Layer[]? children = layer.GetChildren();
            if (children != null && children.Any(child => !child.IsDeleted))
                continue;

            if (doc.Layers.Delete(layer.Index, quiet: true))
                removed++;
        }

        return removed;
    }
}
