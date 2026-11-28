using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Moves a terrain's definition - its cards and settings - between documents as a file (design review
/// Steps 77, 78, 80). Only what another document can resolve travels: assigned <b>layer paths</b>. Picked
/// object ids, geometry, build results and links to other terrains mean nothing elsewhere, so they are left
/// out on export and cleared again on import (a hand-edited file is not trusted to have done it).
/// Lengths are converted from the file's units to the document's.
/// </summary>
internal static class TerrainTransfer
{
    public const string FileExtension = ".mhterrain";

    private const string FormatKey = "molehillTerrainDefinition";
    private const int FormatVersion = 1;

    public static string Export(TerrainDefinition terrain, UnitSystem units)
    {
        TerrainDefinition copy = Clone(terrain);
        StripDocumentState(copy);

        var file = new JsonObject
        {
            [FormatKey] = FormatVersion,
            ["unitSystem"] = units.ToString(),
            ["document"] = JsonNode.Parse(TerrainSerializer.Serialize(new[] { copy })),
        };
        return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Reads a file into a new terrain for <paramref name="documentUnits"/>: fresh ids, document state
    /// stripped, lengths converted. <paramref name="layerExists"/> sorts the assigned layer paths into those
    /// the document has and those that need reassigning. Throws <see cref="InvalidDataException"/> for a file
    /// that is not a terrain definition.
    /// </summary>
    public static TerrainImportPreview Read(string json, ModelUnitContext documentUnits, Func<string, bool> layerExists)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("This file is not a MoleHill terrain definition.", exception);
        }

        if (root is not JsonObject file || file[FormatKey] is null || file["document"] is not JsonNode document)
            throw new InvalidDataException("This file is not a MoleHill terrain definition.");

        string? unitName = file["unitSystem"] is JsonValue unitValue && unitValue.TryGetValue(out string? name) ? name : null;
        if (!Enum.TryParse(unitName, out UnitSystem sourceUnits))
            sourceUnits = UnitSystem.Meters;
        ModelUnitContext sourceContext = ModelUnitContext.FromUnitSystem(sourceUnits);
        if (!sourceContext.IsSupported)
            throw new InvalidDataException($"The file's units ({sourceUnits}) are not supported.");

        TerrainDefinition terrain;
        try
        {
            terrain = TerrainSerializer.Deserialize(document.ToJsonString(), sourceContext).FirstOrDefault()
                ?? throw new InvalidDataException("The file holds no terrain.");
        }
        catch (NotSupportedException exception)
        {
            // GuardSupportedSchema: written by a newer MoleHill.
            throw new InvalidDataException("This terrain definition was written by a newer version of MoleHill. Update MoleHill to import it.", exception);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("This terrain definition file is damaged and cannot be read.", exception);
        }

        int clearedLinks = ClearTerrainLinks(terrain, _ => true);
        StripDocumentState(terrain);
        RenewIdentity(terrain);

        double scale = documentUnits.FromMeters(sourceContext.ToMeters(1.0));
        if (Math.Abs(scale - 1.0) > 1e-12)
            TerrainUnitScaler.Scale(new[] { terrain }, scale);

        var layers = SourceSets(terrain).SelectMany(set => set.LayerPaths).Distinct(StringComparer.Ordinal).ToList();
        return new TerrainImportPreview(
            terrain,
            layers.Where(layerExists).ToList(),
            layers.Where(path => !layerExists(path)).ToList(),
            clearedLinks,
            sourceUnits.ToString());
    }

    /// <summary>
    /// Gives a copied terrain new ids throughout - the terrain, every card and zone - keeping the scatter
    /// cards' zone filters pointing at the copied zones. Shared by Duplicate and Import.
    /// </summary>
    public static void RenewIdentity(TerrainDefinition terrain)
    {
        terrain.TerrainId = Guid.NewGuid();
        foreach (var modifier in terrain.Modifiers)
            modifier.Id = Guid.NewGuid();
        foreach (var marker in terrain.Markers)
            marker.Id = Guid.NewGuid();
        foreach (var terrainObject in terrain.Objects)
        {
            terrainObject.Id = Guid.NewGuid();
            terrainObject.PlacementStates.Clear();
        }

        var zoneIdMap = new Dictionary<Guid, Guid>();
        foreach (var zone in terrain.Zones)
        {
            Guid oldId = zone.ZoneId;
            zone.ZoneId = Guid.NewGuid();
            zoneIdMap[oldId] = zone.ZoneId;
        }

        foreach (var scatter in terrain.Objects.OfType<ScatterObjectDefinition>())
            scatter.ZoneIds = scatter.ZoneIds.Select(id => zoneIdMap.TryGetValue(id, out Guid mapped) ? mapped : id).ToList();
        foreach (var analysis in terrain.Analyses)
            analysis.Id = Guid.NewGuid();
        foreach (var annotation in terrain.Annotations)
            annotation.Id = Guid.NewGuid();
    }

    /// <summary>
    /// Clears every link from <paramref name="terrain"/> to another terrain that <paramref name="drop"/>
    /// selects (a compared terrain, a drawn profile, a Project To target). Returns how many were cleared.
    /// Shared by deleting a terrain (drop that one) and importing (drop all).
    /// </summary>
    public static int ClearTerrainLinks(TerrainDefinition terrain, Func<Guid, bool> drop)
    {
        int cleared = 0;
        foreach (TerrainSectionAnnotationDefinitionBase section in terrain.Annotations.OfType<TerrainSectionAnnotationDefinitionBase>())
        {
            cleared += section.ComparisonTerrainIds.RemoveAll(id => drop(id));
            foreach (Guid id in section.ProfileColorArgbs.Keys.Where(drop).ToList())
            {
                section.ProfileColorArgbs.Remove(id);
                cleared++;
            }
            if (section.CutFillReferenceTerrainId is { } reference && drop(reference))
            {
                section.CutFillReferenceTerrainId = null;
                cleared++;
            }
        }

        foreach (ReferenceComparisonAnalysisDefinition analysis in terrain.Analyses.OfType<ReferenceComparisonAnalysisDefinition>())
        {
            if (analysis.ReferenceTerrainId is { } reference && drop(reference))
            {
                analysis.ReferenceTerrainId = null;
                cleared++;
            }
        }

        foreach (ProjectToModifierDefinition modifier in terrain.Modifiers.OfType<ProjectToModifierDefinition>())
        {
            if (modifier.TargetTerrainId is { } target && drop(target))
            {
                modifier.TargetTerrainId = null;
                cleared++;
            }
        }

        return cleared;
    }

    /// <summary>
    /// Every <see cref="SourceReferenceSet"/> a terrain holds, wherever a card keeps one (including inside
    /// lists, such as a scatter card's block entries). Found by walking the model, so a new card's sources
    /// are covered without listing them here.
    /// </summary>
    public static IEnumerable<SourceReferenceSet> SourceSets(TerrainDefinition terrain)
    {
        var found = new List<SourceReferenceSet>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Visit(object? value)
        {
            if (value == null || value is string || !seen.Add(value))
                return;

            if (value is SourceReferenceSet set)
            {
                found.Add(set);
                return;
            }

            if (value is IEnumerable sequence)
            {
                foreach (object? item in sequence)
                    Visit(item);
                return;
            }

            Type type = value.GetType();
            if (type.Namespace != typeof(TerrainDefinition).Namespace || type.IsValueType)
                return;

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                    continue;
                Type propertyType = property.PropertyType;
                if (propertyType.IsPrimitive || propertyType.IsEnum || propertyType == typeof(string))
                    continue;
                Visit(property.GetValue(value));
            }
        }

        Visit(terrain.Modifiers);
        Visit(terrain.Markers);
        Visit(terrain.Objects);
        Visit(terrain.Zones);
        Visit(terrain.Analyses);
        Visit(terrain.Annotations);
        return found;
    }

    private static TerrainDefinition Clone(TerrainDefinition terrain) =>
        JsonSerializer.Deserialize<TerrainDefinition>(
            JsonSerializer.Serialize(terrain, TerrainSerializer.SharedOptions),
            TerrainSerializer.SharedOptions)!;

    /// <summary>Drops what only means something in the document it came from.</summary>
    private static void StripDocumentState(TerrainDefinition terrain)
    {
        foreach (SourceReferenceSet set in SourceSets(terrain))
            set.ReplaceObjects(Array.Empty<Guid>());

        ClearTerrainLinks(terrain, _ => true);
        terrain.OutputObjectIds.Clear();
        terrain.ZoneObjectIds.Clear();
        terrain.AuxiliaryObjectIds.Clear();
        terrain.MarkerObjectIds.Clear();
        terrain.BakedObjectIds.Clear();
        terrain.LastBuildMessage = null;
        terrain.LastStructuredDiagnostics.Clear();
        terrain.LastBuildUtc = null;
        terrain.LastAnalysisResults.Clear();
        foreach (var terrainObject in terrain.Objects)
        {
            terrainObject.PlacementStates.Clear();
            terrainObject.ReleasedObjectIds.Clear();
        }
    }
}
