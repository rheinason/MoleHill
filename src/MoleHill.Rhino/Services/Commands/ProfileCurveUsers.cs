// Which terrain cards read a curve, so mhEditProfile can say what its edit will rebuild and pick the right ground.
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

/// <summary>A card that takes the edited curve as input: the terrain, the card's label, and the modifier when it is one.</summary>
internal readonly record struct ProfileCurveUser(TerrainDefinition Terrain, string CardLabel, ModifierDefinition? Modifier);

/// <summary>
/// Finds the cards whose sources name a curve, by object or by its layer, the way the build resolves them.
/// mhEditProfile says so before the edit (applying it rebuilds them, as any edit of the curve does) and takes
/// its ground from the mesh the first such modifier received: the ground under a Grade Path is the terrain
/// before the road, not the finished terrain, which already carries the road.
/// </summary>
internal static class ProfileCurveUsers
{
    public static List<ProfileCurveUser> Find(IEnumerable<TerrainDefinition> terrains, Guid curveId, string? layerPath)
    {
        var users = new List<ProfileCurveUser>();
        foreach (TerrainDefinition terrain in terrains)
        {
            foreach (ModifierDefinition modifier in terrain.Modifiers)
            {
                if (modifier.IsEnabled && Reads(modifier.EnumerateSourceSets(), curveId, layerPath))
                    users.Add(new ProfileCurveUser(terrain, modifier.Label, modifier));
            }

            foreach (AnalysisDefinition analysis in terrain.Analyses)
            {
                if (Reads(analysis.EnumerateSourceSets(), curveId, layerPath))
                    users.Add(new ProfileCurveUser(terrain, analysis.Label, null));
            }

            foreach (AnnotationDefinition annotation in terrain.Annotations)
            {
                if (Reads(annotation.EnumerateSourceSets(), curveId, layerPath))
                    users.Add(new ProfileCurveUser(terrain, annotation.Label, null));
            }
        }

        return users;
    }

    /// <summary>One line naming the users, or null when nothing reads the curve.</summary>
    public static string? Describe(IReadOnlyList<ProfileCurveUser> users)
    {
        if (users.Count == 0)
            return null;
        IEnumerable<string> names = users.Select(user => $"'{user.CardLabel}' on terrain '{user.Terrain.Name}'");
        return $"This curve is input to {string.Join(", ", names)}; applying the profile updates {(users.Count == 1 ? "it" : "them")}.";
    }

    private static bool Reads(IEnumerable<SourceReferenceSet> sets, Guid curveId, string? layerPath)
    {
        foreach (SourceReferenceSet set in sets)
        {
            if (set.ObjectIds.Contains(curveId))
                return true;
            if (layerPath != null && set.LayerPaths.Any(path => string.Equals(path, layerPath, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }
}
