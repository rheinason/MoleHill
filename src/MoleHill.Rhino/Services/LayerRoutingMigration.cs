using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Carries pre-schema-30 output layers into the document's own layer template.
///
/// Before schema 30, each terrain named its own terrain / auxiliary / annotation layers and several
/// cards carried an output layer of their own. Routing now lives in the template, so a document that
/// customised any of those would otherwise have its geometry move the first time it was rebuilt.
///
/// Rather than retarget anything, a customised document gets a template of its own, binding exactly
/// the roles it had changed and leaving the rest to inherit. Geometry keeps landing where it landed,
/// and what used to be scattered across a panel section and several cards is now visible in one
/// place. A document that only ever used the defaults — the overwhelming majority — needs no
/// template and gets none.
/// </summary>
internal static class LayerRoutingMigration
{
    public sealed record Result(
        LayerTemplateDefinition? Template,
        IReadOnlyList<string> Conflicts)
    {
        public static readonly Result Nothing = new(null, Array.Empty<string>());

        public bool HasTemplate => Template != null;
    }

    /// <summary>
    /// Builds the template a document needs to keep rendering as it did, or
    /// <see cref="Result.Nothing"/> when it only used the defaults.
    /// </summary>
    /// <param name="documentName">Names the synthesized template, so the user can tell where it came
    /// from when they meet it in the template editor.</param>
    public static Result Plan(IReadOnlyList<TerrainDefinition> terrains, string documentName)
    {
        var bindings = new Dictionary<LayerRole, string>();
        var conflicts = new List<string>();
        string? annotationStyle = null;

        foreach (TerrainDefinition terrain in terrains)
        {
            Bind(bindings, conflicts, LayerRole.Terrain, terrain.LegacyTerrainLayerPath, terrain.Name);
            Bind(bindings, conflicts, LayerRole.Auxiliary, terrain.LegacyAuxiliaryLayerPath, terrain.Name);
            Bind(bindings, conflicts, LayerRole.Annotation, terrain.LegacyAnnotationLayerPath, terrain.Name);

            annotationStyle ??= NullIfBlank(terrain.LegacyAnnotationStyleName);

            foreach (ModifierDefinition modifier in terrain.Modifiers)
            {
                // A wall card's old default was the shared auxiliary layer, not today's Walls sublayer,
                // so an untouched wall carries that value and must not read as a customisation.
                if (modifier is RetainingWallModifierDefinition wall)
                {
                    Bind(bindings, conflicts, LayerRole.Walls, wall.LegacyOutputLayerPath, wall.Label,
                        legacyDefaultPath: TerrainDefinition.DefaultAuxiliaryLayerPath);
                }
            }

            foreach (ITerrainContentItem item in LegacyRoutedContent(terrain))
            {
                LayerRole? role = RoleFor(item);
                if (role.HasValue)
                    Bind(bindings, conflicts, role.Value, LegacyLayerOf(item), item.Label);
            }
        }

        if (bindings.Count == 0 && annotationStyle == null)
            return Result.Nothing;

        var entries = bindings
            .Select(pair => new LayerTemplateEntry
            {
                Path = pair.Value,
                Roles = { LayerRoleRegistry.For(pair.Key).Id }
            })
            .ToList();

        if (annotationStyle != null)
        {
            LayerTemplateEntry annotation = entries.FirstOrDefault(entry =>
                entry.Roles.Contains(LayerRoleRegistry.For(LayerRole.Annotation).Id))
                ?? Add(entries, LayerRole.Annotation, LayerRoleRegistry.DefaultPath(LayerRole.Annotation));

            annotation.AnnotationStyleName = annotationStyle;
        }

        return new Result(
            new LayerTemplateDefinition
            {
                Version = 1,
                Name = $"{documentName} (migrated)",
                Entries = entries
            },
            conflicts);
    }

    /// <summary>
    /// Clears the legacy values once they have been carried over, so the fields stop being a second
    /// answer to a question the template now owns.
    /// </summary>
    public static void ClearLegacyValues(IReadOnlyList<TerrainDefinition> terrains)
    {
        foreach (TerrainDefinition terrain in terrains)
        {
            terrain.LegacyTerrainLayerPath = null;
            terrain.LegacyAuxiliaryLayerPath = null;
            terrain.LegacyAnnotationLayerPath = null;
            terrain.LegacyAnnotationStyleName = null;

            foreach (var wall in terrain.Modifiers.OfType<RetainingWallModifierDefinition>())
                wall.LegacyOutputLayerPath = null;

            foreach (ITerrainContentItem item in LegacyRoutedContent(terrain))
                ClearLegacyLayer(item);
        }
    }

    private static LayerTemplateEntry Add(List<LayerTemplateEntry> entries, LayerRole role, string path)
    {
        var entry = new LayerTemplateEntry { Path = path, Roles = { LayerRoleRegistry.For(role).Id } };
        entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// First customised value wins. A binding is per role and the old data was per card, so a
    /// document with two contour analyses pointed at different layers cannot keep both — the loser
    /// is named so the user can see which one moved rather than discovering it in the drawing.
    /// </summary>
    private static void Bind(
        Dictionary<LayerRole, string> bindings,
        List<string> conflicts,
        LayerRole role,
        string? layerPath,
        string owner,
        string? legacyDefaultPath = null)
    {
        string? path = NullIfBlank(layerPath);
        if (path == null
            || string.Equals(path, LayerRoleRegistry.DefaultPath(role), StringComparison.OrdinalIgnoreCase)
            || (legacyDefaultPath != null && string.Equals(path, legacyDefaultPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (bindings.TryGetValue(role, out string? existing))
        {
            if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(
                    $"'{owner}' used {path} for {LayerRoleRegistry.For(role).DisplayName}; "
                        + $"the document now routes it to {existing}.");
            }

            return;
        }

        bindings[role] = path;
    }

    /// <summary>
    /// Content that could carry a pre-schema-30 output layer. Both families can: the split into two
    /// collections happened later (schema 31) than the routing move, so a document being migrated here
    /// has its annotations already separated but still holding legacy paths.
    /// </summary>
    private static IEnumerable<ITerrainContentItem> LegacyRoutedContent(TerrainDefinition terrain) =>
        terrain.Analyses.Cast<ITerrainContentItem>().Concat(terrain.Annotations);

    private static LayerRole? RoleFor(ITerrainContentItem analysis) => analysis switch
    {
        // Major and minor inherit from the contour layer, so one binding carries both.
        ContourAnnotationDefinition => LayerRole.Contours,
        WaterflowAnalysisDefinition => LayerRole.Waterflow,
        TerrainSectionAnnotationDefinitionBase => LayerRole.Sections,
        BlockAttributeAnnotationDefinition => LayerRole.Markers,
        _ => null
    };

    private static string? LegacyLayerOf(ITerrainContentItem analysis) => analysis switch
    {
        ContourAnnotationDefinition contour => contour.LegacyOutputLayerPath,
        WaterflowAnalysisDefinition waterflow => waterflow.LegacyOutputLayerPath,
        TerrainSectionAnnotationDefinitionBase section => section.LegacyOutputLayerPath,
        BlockAttributeAnnotationDefinition block => block.LegacyOutputLayerPath,
        _ => null
    };

    private static void ClearLegacyLayer(ITerrainContentItem analysis)
    {
        switch (analysis)
        {
            case ContourAnnotationDefinition contour:
                contour.LegacyOutputLayerPath = null;
                break;
            case WaterflowAnalysisDefinition waterflow:
                waterflow.LegacyOutputLayerPath = null;
                break;
            case TerrainSectionAnnotationDefinitionBase section:
                section.LegacyOutputLayerPath = null;
                break;
            case BlockAttributeAnnotationDefinition block:
                block.LegacyOutputLayerPath = null;
                break;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
