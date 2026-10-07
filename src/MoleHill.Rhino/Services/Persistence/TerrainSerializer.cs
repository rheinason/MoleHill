using System.Text.Json;
using System.Text.Json.Nodes;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.Services;

internal static class TerrainSerializer
{
    private const int DocumentSchemaVersion = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new TerrainJsonTypeResolver()
    };

    /// <summary>
    /// Shared options carrying the registry-driven modifier polymorphism resolver. All whole-terrain
    /// (de)serialization (save/load here, plus the in-memory clone paths) must use these so modifier
    /// `$type` discriminators round-trip — the modifier base no longer declares `[JsonDerivedType]`.
    /// </summary>
    internal static JsonSerializerOptions SharedOptions => JsonOptions;

    public static string Serialize(IReadOnlyList<TerrainDefinition> terrains)
    {
        var envelope = new TerrainDocumentEnvelope
        {
            SchemaVersion = DocumentSchemaVersion,
            Terrains = terrains.ToList()
        };

        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static List<TerrainDefinition> Deserialize(string? json, UnitSystem unitSystem = UnitSystem.Meters)
    {
        ModelUnitContext unitContext = ModelUnitContext.FromUnitSystem(unitSystem);
        if (!unitContext.IsSupported)
            unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
        return Deserialize(json, unitContext);
    }

    public static List<TerrainDefinition> Deserialize(string? json, ModelUnitContext unitContext)
    {
        if (!unitContext.IsSupported)
            throw new InvalidOperationException(ModelUnitGuard.RequiredMessage);

        if (string.IsNullOrWhiteSpace(json))
            return new List<TerrainDefinition>();

        var envelope = JsonSerializer.Deserialize<TerrainDocumentEnvelope>(TerrainSchemaMigrations.ApplyJsonMigrations(json), JsonOptions);
        if (envelope == null)
            return new List<TerrainDefinition>();

        GuardSupportedSchema(envelope);

        if (envelope.Terrains == null)
            return new List<TerrainDefinition>();

        foreach (var terrain in envelope.Terrains)
        {
            int sourceSchemaVersion = terrain.SchemaVersion;
            if (terrain.SchemaVersion <= 0)
                sourceSchemaVersion = 0;

            // Load-time normalization first (not version-gated), so every migration step can rely on
            // non-null collections; then the versioned steps in order; then the legacy promotions.
            NormalizeTerrain(terrain);
            TerrainSchemaMigrations.Apply(terrain, sourceSchemaVersion);

            TerrainSchemaMigrations.ApplyLegacyPromotions(terrain, sourceSchemaVersion, unitContext);
            NormalizeDisplaySettings(terrain);
            TerrainSchemaMigrations.MigrateZones(terrain);
            NormalizeAnalyses(terrain);
            TerrainSchemaMigrations.PromoteLegacyAnalyses(terrain);
            NormalizeAnnotations(terrain, unitContext);
            TerrainSchemaMigrations.DropLegacyBoundaries(terrain);
            terrain.EnsureBaseModifier();
            terrain.SchemaVersion = TerrainDefinition.CurrentSchemaVersion;
        }


        return envelope.Terrains;
    }

    /// <summary>
    /// The newest document schema this build can read. A document stamped higher than this was written
    /// by a newer plugin: it may carry properties on types this build already knows, which
    /// <c>System.Text.Json</c> silently drops. Loading it would therefore round-trip a lossy copy and
    /// re-stamp it with this build's version, so it is refused here — before any legacy rewriting or
    /// normalization runs — and the caller's load-failure path preserves the original JSON untouched.
    /// A missing or zero version is a legacy document, not a future one, and is accepted.
    /// </summary>
    internal static int SupportedSchemaVersion => DocumentSchemaVersion;

    private static void GuardSupportedSchema(TerrainDocumentEnvelope envelope)
    {
        if (envelope.SchemaVersion > DocumentSchemaVersion)
            throw new NotSupportedException(FutureSchemaMessage(envelope.SchemaVersion));

        if (envelope.Terrains == null)
            return;

        foreach (TerrainDefinition terrain in envelope.Terrains)
        {
            if (terrain != null && terrain.SchemaVersion > TerrainDefinition.CurrentSchemaVersion)
                throw new NotSupportedException(FutureSchemaMessage(terrain.SchemaVersion));
        }
    }

    private static string FutureSchemaMessage(int documentVersion) =>
        $"this document's terrain data uses schema version {documentVersion}, but this build of MoleHill " +
        $"reads up to version {DocumentSchemaVersion}. Update MoleHill to open it; the stored data is " +
        "left untouched in the meantime.";

    private sealed class TerrainDocumentEnvelope
    {
        public int SchemaVersion { get; set; }

        public List<TerrainDefinition> Terrains { get; set; } = new();
    }

    private static void NormalizeObjects(TerrainDefinition terrain)
    {
        foreach (var obj in terrain.Objects)
        {
            obj.Sources ??= new SourceReferenceSet();
            obj.RandomRotationMinDegrees = Math.Clamp(obj.RandomRotationMinDegrees, 0.0, 360.0);
            obj.RandomRotationMaxDegrees = Math.Clamp(obj.RandomRotationMaxDegrees, 0.0, 360.0);
            if (obj.RandomRotationMaxDegrees < obj.RandomRotationMinDegrees)
                obj.RandomRotationMaxDegrees = obj.RandomRotationMinDegrees;
            obj.RandomScaleMin = Math.Max(0.01, obj.RandomScaleMin);
            obj.RandomScaleMax = Math.Max(0.01, obj.RandomScaleMax);
            if (obj.RandomScaleMax < obj.RandomScaleMin)
                obj.RandomScaleMax = obj.RandomScaleMin;
            obj.PlacementStates ??= new List<TerrainObjectPlacementState>();
            obj.PlacementStates.RemoveAll(state => state.ObjectId == Guid.Empty);
            foreach (var state in obj.PlacementStates)
            {
                state.LastAppliedTransform ??= new[]
                {
                    1.0, 0.0, 0.0, 0.0,
                    0.0, 1.0, 0.0, 0.0,
                    0.0, 0.0, 1.0, 0.0,
                    0.0, 0.0, 0.0, 1.0
                };

                if (state.LastAppliedTransform.Length != 16)
                {
                    state.LastAppliedTransform =
                    [
                        1.0, 0.0, 0.0, 0.0,
                        0.0, 1.0, 0.0, 0.0,
                        0.0, 0.0, 1.0, 0.0,
                        0.0, 0.0, 0.0, 1.0
                    ];
                }
            }
        }
    }

    private static void NormalizeDisplaySettings(TerrainDefinition terrain)
    {
        terrain.OutputTransparencyPercent = Math.Clamp(terrain.OutputTransparencyPercent, 0, 100);
        if (terrain.TerrainColorArgb == 0)
        {
            var defaultColor = System.Drawing.Color.FromArgb(TerrainDefinition.DefaultTerrainColorArgb);
            int alpha = (int)Math.Round(255.0 * (1.0 - (terrain.OutputTransparencyPercent / 100.0)));
            terrain.TerrainColorArgb = System.Drawing.Color.FromArgb(
                Math.Clamp(alpha, 0, 255),
                defaultColor.R,
                defaultColor.G,
                defaultColor.B).ToArgb();
        }
        terrain.SlopePalettePreset = ColorRampPresets.Resolve(terrain.SlopePalettePreset).Key;
        terrain.SlopeColorLowPercent = Math.Max(0.0, terrain.SlopeColorLowPercent);
        terrain.SlopeColorHighPercent = Math.Max(0.0, terrain.SlopeColorHighPercent);
        // Documents written before the preview weight existed deserialize it as 0, which would draw
        // nothing; clamp to a usable band around the 1.0 default.
        terrain.PreviewLineWeight = terrain.PreviewLineWeight > 0.0
            ? Math.Clamp(terrain.PreviewLineWeight, 0.25, 6.0)
            : 1.0;
    }

    /// <summary>
    /// Makes a deserialized ramp override usable or drops it. A malformed stop list must never leave an
    /// analysis unable to draw, and a list too short to be a ramp is indistinguishable from "no override" —
    /// so it becomes exactly that, and the preset takes over.
    /// </summary>
    private static void NormalizePaletteStops(AnalysisDefinition analysis)
    {
        analysis.PaletteStops ??= new List<AnalysisColorStopState>();
        var stops = analysis.PaletteStops;
        if (stops.Count == 0)
            return;

        stops.RemoveAll(stop => stop == null || !double.IsFinite(stop.Position));
        if (stops.Count < ColorRamp.MinimumStops)
        {
            stops.Clear();
            return;
        }

        foreach (var stop in stops)
        {
            stop.Position = Math.Clamp(stop.Position, 0.0, 1.0);
            // Alpha is not part of a ramp — the analysis mesh gets its transparency from the terrain — and
            // a stop deserialized with alpha 0 would otherwise round-trip into an invisible colour.
            stop.ColorArgb = unchecked((int)0xFF000000) | (stop.ColorArgb & 0x00FFFFFF);
        }

        stops.Sort((a, b) => a.Position.CompareTo(b.Position));
        if (stops.Count > ColorRamp.MaximumStops)
            stops.RemoveRange(ColorRamp.MaximumStops, stops.Count - ColorRamp.MaximumStops);
    }

    private static void NormalizeTerrain(TerrainDefinition terrain)
    {
        terrain.Modifiers ??= new List<ModifierDefinition>();
        terrain.Markers ??= new List<MarkerDefinition>();
        terrain.Objects ??= new List<TerrainObjectDefinition>();
        terrain.Zones ??= new List<CollageZoneDefinition>();
        terrain.Analyses ??= new List<AnalysisDefinition>();
        terrain.Annotations ??= new List<AnnotationDefinition>();
        terrain.OutputObjectIds ??= new List<Guid>();
        terrain.ZoneObjectIds ??= new List<Guid>();
        terrain.AuxiliaryObjectIds ??= new List<Guid>();
        terrain.MarkerObjectIds ??= new List<Guid>();
        terrain.BakedObjectIds ??= new List<Guid>();
        terrain.BakedObjectIds.RemoveAll(id => id == Guid.Empty);
        terrain.LastAnalysisResults ??= new List<TerrainAnalysisSummary>();
        foreach (ModifierDefinition modifier in terrain.Modifiers)
            modifier.NormalizeAfterLoad();
        NormalizeObjects(terrain);
        TerrainSchemaMigrations.ConsolidateBoundaryRoles(terrain);
    }

    private static void NormalizeAnalyses(TerrainDefinition terrain)
    {
        foreach (var analysis in terrain.Analyses)
        {
            analysis.PalettePreset = ColorRampPresets.Resolve(analysis.PalettePreset).Key;
            NormalizePaletteStops(analysis);
            analysis.ColorInterval = Math.Max(0.0, analysis.ColorInterval);
            if (!Enum.IsDefined(analysis.ColorMode))
                analysis.ColorMode = MoleHill.Core.Analysis.AnalysisColorMapper.Mode.Gradient;
            analysis.NormalizeAfterLoad();
        }
    }

    private static void NormalizeAnnotations(TerrainDefinition terrain, ModelUnitContext unitContext)
    {
        foreach (var annotation in terrain.Annotations)
            annotation.NormalizeAfterLoad(unitContext, terrain.TerrainId);
    }
}
