using System.Text.Json;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.Services;

internal static class TerrainSerializer
{
    private const int DocumentSchemaVersion = 29;

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

        var envelope = JsonSerializer.Deserialize<TerrainDocumentEnvelope>(json, JsonOptions);
        if (envelope?.Terrains == null)
            return new List<TerrainDefinition>();

        foreach (var terrain in envelope.Terrains)
        {
            int sourceSchemaVersion = terrain.SchemaVersion;
            if (terrain.SchemaVersion <= 0)
                sourceSchemaVersion = 0;

            terrain.Modifiers ??= new List<ModifierDefinition>();
            terrain.Markers ??= new List<MarkerDefinition>();

            // Pre-v27 marker symbols used BlockScale as an absolute scale. Keep those documents unchanged.
            if (sourceSchemaVersion < 27)
            {
                foreach (MarkerDefinition marker in terrain.Markers)
                    marker.FollowsAnnotationStyle = false;
            }
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
            foreach (var input in terrain.Modifiers.OfType<GeometryInputModifierDefinition>())
                input.TinMesh ??= new SourceReferenceSet();
            foreach (var gradePath in terrain.Modifiers.OfType<GradePathModifierDefinition>())
            {
                gradePath.WidthEdges ??= new SourceReferenceSet();
                // Pre-v29 documents had no UseVariableWidth toggle and expressed "variable width" purely
                // by having width edges assigned; keep those paths variable instead of flattening them.
                // Only pre-v29, so a v29 user who switches the toggle off keeps their edges parked.
                if (sourceSchemaVersion < 29 &&
                    (gradePath.WidthEdges.ObjectIds.Count > 0 || gradePath.WidthEdges.LayerPaths.Count > 0))
                {
                    gradePath.UseVariableWidth = true;
                }
            }
            NormalizeSculptModifiers(terrain);
            NormalizeObjects(terrain);
            PromoteLegacyTolerance(terrain);
            PromoteLegacyDetailSize(terrain, sourceSchemaVersion, unitContext);
            PromoteDisplaySettings(terrain);
            MigrateZones(terrain);
            MigrateAnalyses(terrain, sourceSchemaVersion, unitContext);
            MigrateRemeshModifiers(terrain, sourceSchemaVersion);
            terrain.EnsureBaseModifier();
            terrain.SchemaVersion = TerrainDefinition.CurrentSchemaVersion;
        }

        return envelope.Terrains;
    }

    private static void NormalizeSculptModifiers(TerrainDefinition terrain)
    {
        foreach (var sculpt in terrain.Modifiers.OfType<SculptModifierDefinition>())
        {
            sculpt.Constraints ??= new SourceReferenceSet();
            sculpt.Tiles ??= new List<SculptTile>();
            sculpt.ConstraintFeather = Math.Max(0.0, sculpt.ConstraintFeather);
        }
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

    /// <summary>
    /// Schema 22: the Remesh modifier became a pure isotropic remesh with a single Edge Length target.
    /// Old documents that drove refinement via MaxArea get the equivalent edge length (equilateral
    /// triangle of that area — the same mapping the old local-refine mode used); the legacy value is
    /// then zeroed so it can't be re-migrated or re-serialized as meaningful.
    /// Schema 23: the Remesh modifier gained a Mode choice (isotropic/rebuild/local). Mode's property
    /// initializer already resolves missing JSON to "isotropic" for any older document, so this is only a
    /// defensive normalize for hand-edited documents with an explicit null.
    /// Schema 24: the "rebuild" mode re-exposes Min Angle / Max Area quality knobs. MaxArea is already
    /// zeroed for pre-22 documents above; the same &lt;22 gate also zeroes the legacy global MinAngle so a
    /// stray old value can't quietly refine a terrain the first time its mode is switched to rebuild.
    /// </summary>
    private static void MigrateRemeshModifiers(TerrainDefinition terrain, int sourceSchemaVersion)
    {
        foreach (var remesh in terrain.Modifiers.OfType<RemeshModifierDefinition>())
        {
            if (sourceSchemaVersion < 22)
            {
                if (remesh.MaxArea > 0 && remesh.EdgeLength <= 0)
                    remesh.EdgeLength = Math.Sqrt(remesh.MaxArea * 4.0 / Math.Sqrt(3.0));
                remesh.MaxArea = 0;
                remesh.MinAngle = 0;
            }

            if (sourceSchemaVersion < 23 && string.IsNullOrWhiteSpace(remesh.Mode))
                remesh.Mode = "isotropic";
        }
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

    private static void PromoteLegacyDetailSize(
        TerrainDefinition terrain,
        int sourceSchemaVersion,
        ModelUnitContext unitContext)
    {
        if (sourceSchemaVersion >= TerrainDefinition.CurrentSchemaVersion)
            return;

        if (!TerrainTolerancePolicy.ShouldPromoteLegacyDetailSize(terrain.GlobalTolerance, unitContext))
            return;

        terrain.GlobalTolerance = TerrainTolerancePolicy.DefaultDetailSize(unitContext);
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

    private static void MigrateAnalyses(
        TerrainDefinition terrain,
        int sourceSchemaVersion,
        ModelUnitContext unitContext)
    {
        if (terrain.Analyses.Count > 0)
        {
            foreach (var analysis in terrain.Analyses)
            {
                analysis.PalettePreset = ColorRampPresets.Resolve(analysis.PalettePreset).Key;
                NormalizePaletteStops(analysis);
                analysis.ColorInterval = Math.Max(0.0, analysis.ColorInterval);
                if (!Enum.IsDefined(analysis.ColorMode))
                    analysis.ColorMode = MoleHill.Core.Analysis.AnalysisColorMapper.Mode.Gradient;
                // Pre-v25 analyses had no auto-range flag. Preserve their explicit ranges.
                if (sourceSchemaVersion < 25 && (analysis.RangeLow != 0.0 || analysis.RangeHigh != 0.0))
                    analysis.AutoColorRange = false;
                // Pre-v27 annotation sized itself from stored absolute heights. Keep those documents
                // looking identical; only new analyses follow the terrain's dimension style.
                if (sourceSchemaVersion < 27)
                    analysis.FollowsAnnotationStyle = false;
                switch (analysis)
                {
                    case ContourAnalysisDefinition contour:
                        contour.Interval = contour.Interval > 0.0 ? contour.Interval : unitContext.FromMeters(1.0);
                        contour.LabelEveryNth = Math.Max(1, contour.LabelEveryNth);
                        contour.LabelTextHeight = contour.LabelTextHeight > 0.0
                            ? contour.LabelTextHeight
                            : unitContext.FromMeters(1.0);
                        contour.LabelInterval = Math.Max(0.0, contour.LabelInterval);
                        contour.MajorEveryNth = Math.Max(1, contour.MajorEveryNth);
                        // Pre-v27 contours were emitted on one flat layer; re-routing them would move
                        // geometry out from under existing layer settings.
                        if (sourceSchemaVersion < 27)
                            contour.SeparateMajorMinorLayers = false;
                        if (string.IsNullOrWhiteSpace(contour.LabelFormat))
                            contour.LabelFormat = "F2";
                        break;
                    case ReferenceComparisonAnalysisDefinition comparison:
                        comparison.Reference ??= new SourceReferenceSet();
                        comparison.Boundary ??= new SourceReferenceSet();
                        break;
                    case CurveSlopeLabelAnalysisDefinition curveSlope:
                        NormalizeBlockAttributeAnalysis(curveSlope);
                        curveSlope.Interval = curveSlope.Interval > 0.0
                            ? curveSlope.Interval
                            : unitContext.FromMeters(10.0);
                        if (string.IsNullOrWhiteSpace(curveSlope.ValueFormat))
                            curveSlope.ValueFormat = "F1";
                        break;
                    case CurveElevationLabelAnalysisDefinition curveElevation:
                        NormalizeBlockAttributeAnalysis(curveElevation);
                        curveElevation.Interval = curveElevation.Interval > 0.0
                            ? curveElevation.Interval
                            : unitContext.FromMeters(10.0);
                        if (string.IsNullOrWhiteSpace(curveElevation.ValueFormat))
                            curveElevation.ValueFormat = "F2";
                        break;
                    case PointSlopeLabelAnalysisDefinition pointSlope:
                        NormalizeBlockAttributeAnalysis(pointSlope);
                        if (string.IsNullOrWhiteSpace(pointSlope.ValueFormat))
                            pointSlope.ValueFormat = "F1";
                        break;
                    case SlopeArrowAnalysisDefinition slopeArrows:
                        NormalizeBlockAttributeAnalysis(slopeArrows);
                        slopeArrows.GridSpacing = slopeArrows.GridSpacing > 0.0
                            ? slopeArrows.GridSpacing
                            : unitContext.FromMeters(5.0);
                        if (string.IsNullOrWhiteSpace(slopeArrows.ValueFormat))
                            slopeArrows.ValueFormat = "F1";
                        break;
                    case WaterflowAnalysisDefinition waterflow:
                        waterflow.Sources ??= new SourceReferenceSet();
                        waterflow.MaxLength = Math.Max(0.0, waterflow.MaxLength);
                        break;
                    case GradeBetweenPointsAnalysisDefinition gradeCallout:
                        NormalizeBlockAttributeAnalysis(gradeCallout);
                        gradeCallout.TextHeight = gradeCallout.TextHeight > 0.0
                            ? gradeCallout.TextHeight
                            : unitContext.FromMeters(1.0);
                        if (string.IsNullOrWhiteSpace(gradeCallout.ValueFormat))
                            gradeCallout.ValueFormat = "F1";
                        break;
                    case ProjectedElevationLabelAnalysisDefinition projectedElevation:
                        NormalizeBlockAttributeAnalysis(projectedElevation);
                        if (string.IsNullOrWhiteSpace(projectedElevation.ValueFormat))
                            projectedElevation.ValueFormat = "F2";
                        break;
                    case TerrainSectionAnalysisDefinitionBase section:
                        NormalizeTerrainSectionAnalysis(section, terrain.TerrainId, unitContext);
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
                PalettePreset = ColorRampPresets.Resolve(terrain.SlopePalettePreset).Key,
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

    private static void NormalizeTerrainSectionAnalysis(
        TerrainSectionAnalysisDefinitionBase analysis,
        Guid ownerTerrainId,
        ModelUnitContext unitContext)
    {
        analysis.ComparisonTerrainIds ??= new List<Guid>();
        analysis.ComparisonTerrainIds = analysis.ComparisonTerrainIds
            .Where(id => id != Guid.Empty && id != ownerTerrainId)
            .Distinct()
            .ToList();
        if (analysis.CutFillReferenceTerrainId == Guid.Empty ||
            (analysis.CutFillReferenceTerrainId.HasValue &&
             !analysis.ComparisonTerrainIds.Contains(analysis.CutFillReferenceTerrainId.Value)))
            analysis.CutFillReferenceTerrainId = null;
        analysis.CutFillOpacityPercent = Math.Clamp(analysis.CutFillOpacityPercent, 0, 100);
        if (analysis.CutColorArgb == 0)
            analysis.CutColorArgb = TerrainSectionAnalysisDefinitionBase.DefaultCutColorArgb;
        if (analysis.FillColorArgb == 0)
            analysis.FillColorArgb = TerrainSectionAnalysisDefinitionBase.DefaultFillColorArgb;

        analysis.Sources ??= new SourceReferenceSet();
        if (analysis.TextHeight <= 0.0)
            analysis.TextHeight = unitContext.FromMeters(1.0);
        // Every section type shares the exaggeration, so normalize it once, before the per-type cases —
        // not as a switch arm of its own, which would shadow them for any section that needed both.
        if (analysis.VerticalExaggeration <= 0.0)
            analysis.VerticalExaggeration = 1.0;

        switch (analysis)
        {
            case CrossSectionStationAnalysisDefinition crossSection:
                crossSection.StationInterval = crossSection.StationInterval > 0.0
                    ? crossSection.StationInterval
                    : unitContext.FromMeters(10.0);
                crossSection.CrossSectionWidth = crossSection.CrossSectionWidth > 0.0
                    ? crossSection.CrossSectionWidth
                    : unitContext.FromMeters(10.0);
                crossSection.GridColumns = Math.Max(crossSection.GridColumns, 1);
                break;
            case LongitudinalSectionAnalysisDefinition longitudinal:
                longitudinal.SampleInterval = longitudinal.SampleInterval > 0.0
                    ? longitudinal.SampleInterval
                    : unitContext.FromMeters(1.0);
                break;
        }
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
            UseColorOverride = zone.UseColorOverride,
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
