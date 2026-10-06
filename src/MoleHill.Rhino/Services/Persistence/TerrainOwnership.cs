using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Which source layers belong to a terrain, and how a duplicate's sources are repointed at its copies.
/// A terrain owns the input layers that sit under its own layer root and are not an output
/// destination; everything outside the root (a survey import, a layer the user drew elsewhere) is
/// shared by reference and stays where it is. Pure, so it is testable without a document.
/// </summary>
internal static class TerrainOwnership
{
    /// <summary>
    /// True for a layer the terrain owns: under its root, and not a layer the role table sends
    /// generated output to (copying output would just bring stale geometry along).
    /// </summary>
    public static bool IsOwnedInputLayer(string? layerPath, string terrainName, LayerRoleTable table) =>
        TerrainLayerNaming.IsUnderRoot(layerPath, terrainName) && table.FindByLayerPath(layerPath) == null;

    /// <summary>
    /// Repoints every source set of <paramref name="terrain"/>: owned layers move from
    /// <paramref name="oldTerrainName"/>'s root to the terrain's own, and each object id in
    /// <paramref name="objectMap"/> becomes its copy. Anything else is left as it is.
    /// </summary>
    public static void Remap(
        TerrainDefinition terrain,
        string oldTerrainName,
        LayerRoleTable oldTable,
        IReadOnlyDictionary<Guid, Guid> objectMap)
    {
        foreach (SourceReferenceSet set in terrain.EnumerateSourceSets())
        {
            if (set.LayerPaths.Count > 0)
            {
                set.ReplaceLayers(set.LayerPaths.Select(path =>
                    IsOwnedInputLayer(path, oldTerrainName, oldTable)
                        ? TerrainLayerNaming.Rebase(path, oldTerrainName, terrain.Name) ?? path
                        : path));
            }

            if (set.ObjectIds.Count > 0 && objectMap.Count > 0)
                set.ReplaceObjects(set.ObjectIds.Select(id => objectMap.TryGetValue(id, out Guid copy) ? copy : id));
        }
    }
}
