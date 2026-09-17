// Resolves scripted Grasshopper terrain references without silently choosing a duplicate name.
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal static class TerrainReferenceResolver
{
    public static TerrainDefinition? Resolve(
        IReadOnlyList<TerrainDefinition> terrains,
        string reference,
        out string? errorMessage)
    {
        errorMessage = null;
        if (Guid.TryParse(reference, out Guid terrainId))
            return terrains.FirstOrDefault(terrain => terrain.TerrainId == terrainId);

        TerrainDefinition[] matches = terrains.Where(terrain =>
            string.Equals(terrain.Name, reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
        {
            string candidates = string.Join(", ", matches.Select(terrain =>
                $"{terrain.Name} ({terrain.TerrainId:D})"));
            errorMessage = $"Terrain name '{reference}' is ambiguous. Use a terrain GUID: {candidates}.";
            return null;
        }

        return matches.FirstOrDefault();
    }
}
