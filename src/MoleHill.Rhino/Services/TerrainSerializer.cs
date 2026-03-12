using System.Text.Json;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal static class TerrainSerializer
{
    private const int DocumentSchemaVersion = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(IReadOnlyList<TerrainDefinition> terrains)
    {
        var envelope = new TerrainDocumentEnvelope
        {
            SchemaVersion = DocumentSchemaVersion,
            Terrains = terrains.ToList()
        };

        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static List<TerrainDefinition> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<TerrainDefinition>();

        var envelope = JsonSerializer.Deserialize<TerrainDocumentEnvelope>(json, JsonOptions);
        if (envelope?.Terrains == null)
            return new List<TerrainDefinition>();

        foreach (var terrain in envelope.Terrains)
        {
            if (terrain.SchemaVersion <= 0)
                terrain.SchemaVersion = TerrainDefinition.CurrentSchemaVersion;

            terrain.Modifiers ??= new List<ModifierDefinition>();
            terrain.Markers ??= new List<MarkerDefinition>();
            terrain.Zones ??= new List<CollageZoneDefinition>();
            terrain.OutputObjectIds ??= new List<Guid>();
            terrain.ZoneObjectIds ??= new List<Guid>();
            terrain.AuxiliaryObjectIds ??= new List<Guid>();
            terrain.MarkerObjectIds ??= new List<Guid>();
            PromoteLegacyTolerance(terrain);
            PromoteDisplaySettings(terrain);
            MigrateZones(terrain);
            terrain.EnsureBaseModifier();
            terrain.SchemaVersion = TerrainDefinition.CurrentSchemaVersion;
        }

        return envelope.Terrains;
    }

    private sealed class TerrainDocumentEnvelope
    {
        public int SchemaVersion { get; set; }

        public List<TerrainDefinition> Terrains { get; set; } = new();
    }

    private static void MigrateZones(TerrainDefinition terrain)
    {
        bool migrated = false;

        foreach (var collage in terrain.Modifiers.OfType<MeshCollageModifierDefinition>().ToList())
        {
            foreach (var zone in collage.Zones)
            {
                terrain.Zones.Add(CloneZone(zone));
            }

            terrain.Modifiers.Remove(collage);
            migrated = true;
        }

        foreach (var meshAreas in terrain.Modifiers.OfType<MeshAreasModifierDefinition>().ToList())
        {
            if (meshAreas.Boundaries.HasReferences)
            {
                terrain.Zones.Add(new CollageZoneDefinition
                {
                    Name = NextImportedZoneName(terrain.Zones),
                    Boundaries = CloneSourceSet(meshAreas.Boundaries),
                    ColorArgb = unchecked((int)0xFFB0B0B0)
                });
            }

            terrain.Modifiers.Remove(meshAreas);
            migrated = true;
        }

        if (migrated)
            terrain.SchemaVersion = TerrainDefinition.CurrentSchemaVersion;
    }

    private static void PromoteLegacyTolerance(TerrainDefinition terrain)
    {
        if (terrain.GlobalTolerance > 0)
            return;

        var triangulate = terrain.Modifiers.OfType<TriangulateModifierDefinition>().FirstOrDefault();
        if (triangulate == null || triangulate.Tolerance <= 0)
            return;

        terrain.GlobalTolerance = triangulate.Tolerance;
        triangulate.Tolerance = 0;
    }

    private static void PromoteDisplaySettings(TerrainDefinition terrain)
    {
        terrain.OutputTransparencyPercent = Math.Clamp(terrain.OutputTransparencyPercent, 0, 100);
    }

    private static CollageZoneDefinition CloneZone(CollageZoneDefinition zone)
    {
        return new CollageZoneDefinition
        {
            ZoneId = zone.ZoneId == Guid.Empty ? Guid.NewGuid() : zone.ZoneId,
            IsEnabled = zone.IsEnabled,
            Name = zone.Name,
            Boundaries = CloneSourceSet(zone.Boundaries),
            ColorArgb = zone.ColorArgb,
            LayerName = zone.LayerName,
            MaterialName = zone.MaterialName,
            UseInputElevationForPriority = zone.UseInputElevationForPriority,
            SplitToSeparateMesh = zone.SplitToSeparateMesh
        };
    }

    private static SourceReferenceSet CloneSourceSet(SourceReferenceSet sourceSet)
    {
        return new SourceReferenceSet
        {
            ObjectIds = sourceSet.ObjectIds.ToList(),
            LayerPaths = sourceSet.LayerPaths.ToList()
        };
    }

    private static string NextImportedZoneName(IEnumerable<CollageZoneDefinition> zones)
    {
        int index = 1;
        var names = zones.Select(zone => zone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains($"Imported Zone {index}"))
            index++;

        return $"Imported Zone {index}";
    }
}
