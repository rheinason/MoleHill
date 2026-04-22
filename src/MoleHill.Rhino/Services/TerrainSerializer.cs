using System.Text.Json;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal static class TerrainSerializer
{
    private const int DocumentSchemaVersion = 19;

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
            terrain.Objects ??= new List<TerrainObjectDefinition>();
            terrain.Zones ??= new List<CollageZoneDefinition>();
            terrain.Analyses ??= new List<AnalysisDefinition>();
            terrain.OutputObjectIds ??= new List<Guid>();
            terrain.ZoneObjectIds ??= new List<Guid>();
            terrain.AuxiliaryObjectIds ??= new List<Guid>();
            terrain.MarkerObjectIds ??= new List<Guid>();
            terrain.BakedObjectIds ??= new List<Guid>();
            terrain.BakedObjectIds.RemoveAll(id => id == Guid.Empty);
            terrain.LastAnalysisResults ??= new List<TerrainAnalysisSummary>();
            NormalizeObjects(terrain);
            PromoteLegacyTolerance(terrain);
            PromoteDisplaySettings(terrain);
            MigrateZones(terrain);
            MigrateAnalyses(terrain);
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

    private static void NormalizeObjects(TerrainDefinition terrain)
    {
        terrain.Objects ??= new List<TerrainObjectDefinition>();
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

    private static void PromoteDisplaySettings(TerrainDefinition terrain)
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
        terrain.SlopePalettePreset = SlopePreviewPaletteCatalog.Resolve(terrain.SlopePalettePreset).Key;
        terrain.SlopeColorLowPercent = Math.Max(0.0, terrain.SlopeColorLowPercent);
        terrain.SlopeColorHighPercent = Math.Max(0.0, terrain.SlopeColorHighPercent);
    }

    private static void MigrateAnalyses(TerrainDefinition terrain)
    {
        if (terrain.Analyses.Count > 0)
        {
            foreach (var analysis in terrain.Analyses)
            {
                analysis.PalettePreset = SlopePreviewPaletteCatalog.Resolve(analysis.PalettePreset).Key;
                switch (analysis)
                {
                    case ContourAnalysisDefinition contour:
                        contour.Interval = Math.Max(contour.Interval, 0.01);
                        break;
                    case ReferenceComparisonAnalysisDefinition comparison:
                        comparison.Reference ??= new SourceReferenceSet();
                        comparison.Boundary ??= new SourceReferenceSet();
                        break;
                    case CurveSlopeLabelAnalysisDefinition curveSlope:
                        NormalizeBlockAttributeAnalysis(curveSlope);
                        curveSlope.Interval = Math.Max(curveSlope.Interval, 0.01);
                        if (string.IsNullOrWhiteSpace(curveSlope.ValueFormat))
                            curveSlope.ValueFormat = "F1";
                        break;
                    case CurveElevationLabelAnalysisDefinition curveElevation:
                        NormalizeBlockAttributeAnalysis(curveElevation);
                        curveElevation.Interval = Math.Max(curveElevation.Interval, 0.01);
                        if (string.IsNullOrWhiteSpace(curveElevation.ValueFormat))
                            curveElevation.ValueFormat = "F2";
                        break;
                    case PointSlopeLabelAnalysisDefinition pointSlope:
                        NormalizeBlockAttributeAnalysis(pointSlope);
                        if (string.IsNullOrWhiteSpace(pointSlope.ValueFormat))
                            pointSlope.ValueFormat = "F1";
                        break;
                    case ProjectedElevationLabelAnalysisDefinition projectedElevation:
                        NormalizeBlockAttributeAnalysis(projectedElevation);
                        if (string.IsNullOrWhiteSpace(projectedElevation.ValueFormat))
                            projectedElevation.ValueFormat = "F2";
                        break;
                }
            }

            EnsureEarthworkAnalysis(terrain);
            PromoteLegacyEarthworkSources(terrain);
            PromoteLegacyAnalysisResults(terrain);
            return;
        }

        if (!terrain.ShowSlopePreview)
            EnsureEarthworkAnalysis(terrain);
        else
            terrain.Analyses.Add(new SlopeAnalysisDefinition
            {
                IsEnabled = true,
                PalettePreset = SlopePreviewPaletteCatalog.Resolve(terrain.SlopePalettePreset).Key,
                RangeLow = terrain.SlopeColorLowPercent,
                RangeHigh = terrain.SlopeColorHighPercent
            });

        terrain.ShowSlopePreview = false;
        PromoteLegacyEarthworkSources(terrain);
        EnsureEarthworkAnalysis(terrain);
        PromoteLegacyAnalysisResults(terrain);
    }

    private static void NormalizeBlockAttributeAnalysis(BlockAttributeAnalysisDefinition analysis)
    {
        analysis.Sources ??= new SourceReferenceSet();
        analysis.BlockScale = Math.Max(0.01, analysis.BlockScale);
        analysis.AttributePrefix ??= string.Empty;
        analysis.AttributeSuffix ??= string.Empty;
        analysis.ValueFormat ??= string.Empty;
    }

    private static void EnsureEarthworkAnalysis(TerrainDefinition terrain)
    {
        if (terrain.Analyses.OfType<EarthworkAnalysisDefinition>().Any())
            return;

        bool hasLegacyReferences = terrain.LegacyEarthworkReference?.HasReferences == true ||
                                   terrain.LegacyEarthworkBoundary?.HasReferences == true;
        if (!hasLegacyReferences &&
            terrain.LegacyLastAnalysis == null &&
            terrain.LastAnalysisResults.Count == 0)
            return;

        var analysis = new EarthworkAnalysisDefinition
        {
            IsEnabled = false
        };
        ApplyLegacyEarthworkSources(terrain, analysis);
        terrain.Analyses.Add(analysis);
    }

    private static void PromoteLegacyEarthworkSources(TerrainDefinition terrain)
    {
        foreach (var analysis in terrain.Analyses.OfType<ReferenceComparisonAnalysisDefinition>())
        {
            if (analysis.Reference.HasReferences || analysis.Boundary.HasReferences)
                continue;

            ApplyLegacyEarthworkSources(terrain, analysis);
        }

        terrain.LegacyEarthworkReference = null;
        terrain.LegacyEarthworkBoundary = null;
    }

    private static void ApplyLegacyEarthworkSources(TerrainDefinition terrain, ReferenceComparisonAnalysisDefinition analysis)
    {
        if (terrain.LegacyEarthworkReference?.HasReferences == true)
            analysis.Reference = CloneSourceSet(terrain.LegacyEarthworkReference);

        if (terrain.LegacyEarthworkBoundary?.HasReferences == true)
            analysis.Boundary = CloneSourceSet(terrain.LegacyEarthworkBoundary);
    }

    private static void PromoteLegacyAnalysisResults(TerrainDefinition terrain)
    {
        if (terrain.LastAnalysisResults.Count == 0 && terrain.LegacyLastAnalysis != null)
        {
            foreach (var analysis in terrain.Analyses)
            {
                var clone = TerrainRuntimeCacheCloner.CloneAnalysis(terrain.LegacyLastAnalysis);
                if (clone == null)
                    continue;

                clone.AnalysisId = analysis.Id;
                terrain.LastAnalysisResults.Add(clone);
            }
        }

        terrain.LegacyLastAnalysis = null;
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
