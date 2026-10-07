using System.Text.Json;
using System.Text.Json.Nodes;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

/// <summary>
/// One historical schema step that rewrites a deserialized terrain. It runs only when the document's
/// source schema version is below <see cref="ToVersion"/>.
/// </summary>
internal sealed record TerrainSchemaMigration(int ToVersion, string Description, Action<TerrainDefinition> Apply);

/// <summary>
/// A schema step that has to rewrite the raw JSON of one terrain before the envelope is bound, because
/// the old shape no longer deserializes into the typed model. <see cref="Apply"/> returns whether it
/// changed anything.
/// </summary>
internal sealed record TerrainJsonMigration(int ToVersion, string Description, Func<JsonObject, bool> Apply);

/// <summary>
/// The ordered schema migrations <see cref="TerrainSerializer"/> runs on load, one step per historical
/// version, and the legacy promotions that are not tied to a version. Load-time normalization that is
/// not about an old document shape (clamps, null-coalescing) lives on the definitions
/// (<c>NormalizeAfterLoad</c>) and in the serializer, not here.
/// </summary>
internal static class TerrainSchemaMigrations
{
    /// <summary>Raw-JSON steps, in version order. Run before the envelope is bound.</summary>
    internal static readonly IReadOnlyList<TerrainJsonMigration> JsonSteps = new[]
    {
        new TerrainJsonMigration(
            31,
            "Annotations moved out of the shared \"analyses\" array into their own family",
            SplitLegacyAnnotations)
    };

    /// <summary>Typed steps, in version order. Run once after deserialization and normalization.</summary>
    internal static readonly IReadOnlyList<TerrainSchemaMigration> Steps = new[]
    {
        // Schema 22: the Remesh modifier became a pure isotropic remesh with a single Edge Length target.
        // Old documents that drove refinement via MaxArea get the equivalent edge length (equilateral
        // triangle of that area — the same mapping the old local-refine mode used); the legacy value is
        // then zeroed so it can't be re-migrated or re-serialized as meaningful.
        // Schema 24: the "rebuild" mode re-exposes Min Angle / Max Area quality knobs. MaxArea is already
        // zeroed for pre-22 documents above; the same <22 gate also zeroes the legacy global MinAngle so a
        // stray old value can't quietly refine a terrain the first time its mode is switched to rebuild.
        new TerrainSchemaMigration(
            22,
            "Remesh: MaxArea becomes an equivalent Edge Length; legacy MaxArea and MinAngle are zeroed",
            terrain =>
            {
                foreach (var remesh in terrain.Modifiers.OfType<RemeshModifierDefinition>())
                {
                    if (remesh.MaxArea > 0 && remesh.EdgeLength <= 0)
                        remesh.EdgeLength = Math.Sqrt(remesh.MaxArea * 4.0 / Math.Sqrt(3.0));
                    remesh.MaxArea = 0;
                    remesh.MinAngle = 0;
                }
            }),

        // Schema 23: the Remesh modifier gained a Mode choice (isotropic/rebuild/local). Mode's property
        // initializer already resolves missing JSON to "isotropic" for any older document, so this is only a
        // defensive normalize for hand-edited documents with an explicit null.
        new TerrainSchemaMigration(
            23,
            "Remesh: a blank Mode becomes isotropic",
            terrain =>
            {
                foreach (var remesh in terrain.Modifiers.OfType<RemeshModifierDefinition>())
                {
                    if (string.IsNullOrWhiteSpace(remesh.Mode))
                        remesh.Mode = "isotropic";
                }
            }),

        new TerrainSchemaMigration(
            25,
            "Analyses: an explicit colour range turns Auto Range off",
            terrain =>
            {
                foreach (var analysis in terrain.Analyses)
                {
                    // Pre-v25 analyses had no auto-range flag. Preserve their explicit ranges.
                    if (analysis.RangeLow != 0.0 || analysis.RangeHigh != 0.0)
                        analysis.AutoColorRange = false;
                }
            }),

        new TerrainSchemaMigration(
            27,
            "Annotations and marker symbols keep their stored absolute sizes and a single contour layer",
            terrain =>
            {
                // Pre-v27 marker symbols used BlockScale as an absolute scale. Keep those documents unchanged.
                foreach (MarkerDefinition marker in terrain.Markers)
                    marker.FollowsAnnotationStyle = false;

                foreach (var annotation in terrain.Annotations)
                {
                    // Pre-v27 annotation sized itself from stored absolute heights. Keep those documents
                    // looking identical; only new annotations follow the terrain's dimension style.
                    annotation.FollowsAnnotationStyle = false;

                    // Pre-v27 contours were emitted on one flat layer; re-routing them would move
                    // geometry out from under existing layer settings.
                    if (annotation is ContourAnnotationDefinition contour)
                        contour.SeparateMajorMinorLayers = false;
                }
            }),

        new TerrainSchemaMigration(
            29,
            "Grade Path: assigned width edges imply Use Variable Width",
            terrain =>
            {
                foreach (var gradePath in terrain.Modifiers.OfType<GradePathModifierDefinition>())
                {
                    // Pre-v29 documents had no UseVariableWidth toggle and expressed "variable width" purely
                    // by having width edges assigned; keep those paths variable instead of flattening them.
                    // Only pre-v29, so a v29 user who switches the toggle off keeps their edges parked.
                    if (gradePath.WidthEdges.ObjectIds.Count > 0 || gradePath.WidthEdges.LayerPaths.Count > 0)
                        gradePath.UseVariableWidth = true;
                }
            }),

        new TerrainSchemaMigration(
            32,
            "Triangulate: the base card's legacy combined boundary becomes its Outer boundary",
            terrain =>
            {
                // Only the base Triangulate card's legacy boundary was a terrain crop, so only it becomes Outer. It
                // stays on the same card, so a disabled card keeps it without trimming (a disabled card owns no
                // boundaries). A legacy Add Geometry boundary did the opposite — it was combined with the existing
                // mesh boundary to extend the patch — and has no equivalent role; turning it into an Outer trim
                // would crop the whole terrain to the patch, so it is dropped. Duplicate legacy Triangulate cards
                // become Add Geometry cards on load, so theirs is dropped for the same reason.
                TriangulateModifierDefinition? primary =
                    terrain.Modifiers.OfType<TriangulateModifierDefinition>().FirstOrDefault();
                if (primary?.LegacyBoundary?.HasReferences == true)
                    MergeSourceSet(primary.OuterBoundaries, primary.LegacyBoundary);
            })
    };

    /// <summary>Runs every raw-JSON step the document's terrains are still below.</summary>
    internal static string ApplyJsonMigrations(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json; // Let the real deserialize report it.
        }

        if (root is not JsonObject envelope || envelope["terrains"] is not JsonArray terrains)
            return json;

        bool changed = false;
        foreach (JsonNode? terrainNode in terrains)
        {
            if (terrainNode is not JsonObject terrain)
                continue;

            int version = terrain["schemaVersion"]?.GetValue<int>() ?? 0;
            foreach (TerrainJsonMigration step in JsonSteps)
            {
                if (version < step.ToVersion && step.Apply(terrain))
                    changed = true;
            }
        }

        return changed ? root.ToJsonString() : json;
    }

    /// <summary>Runs, in version order, every step the document is still below.</summary>
    internal static void Apply(TerrainDefinition terrain, int sourceSchemaVersion)
    {
        foreach (TerrainSchemaMigration step in Steps)
        {
            if (sourceSchemaVersion < step.ToVersion)
                step.Apply(terrain);
        }
    }

    private static bool SplitLegacyAnnotations(JsonObject terrain)
    {
        // Before schema 31 both families shared the "analyses" array. Annotation discriminators are no
        // longer registered under AnalysisDefinition, so those entries have to be moved across before the
        // envelope is bound or binding fails outright. Done on the node tree rather than with a
        // deserialize-time shim so the typed model never has to know the two were once one list.
        if (terrain["analyses"] is not JsonArray analyses)
            return false;

        var keptAnalyses = new JsonArray();
        var annotations = new JsonArray();
        foreach (JsonNode? entry in analyses)
        {
            // Re-parse rather than re-parent: a JsonNode cannot belong to two arrays, and
            // DeepClone is not available on this target framework.
            JsonNode? copy = entry is null ? null : JsonNode.Parse(entry.ToJsonString());
            string? kind = entry?["$type"]?.GetValue<string>();
            if (kind != null && AnnotationTypeRegistry.ForKind(kind) != null)
                annotations.Add(copy);
            else
                keptAnalyses.Add(copy);
        }

        if (annotations.Count == 0)
            return false;

        terrain["analyses"] = keptAnalyses;
        terrain["annotations"] = annotations;
        return true;
    }

    /// <summary>
    /// Boundary roles are terrain-wide. Guarantees a base Triangulate card and consolidates the role
    /// references of duplicate legacy Triangulate cards onto it before <c>EnsureBaseModifier</c> turns
    /// those duplicates into Add Geometry cards. Not version-gated.
    /// </summary>
    internal static void ConsolidateBoundaryRoles(TerrainDefinition terrain)
    {
        List<TriangulateModifierDefinition> triangulates = terrain.Modifiers
            .OfType<TriangulateModifierDefinition>()
            .ToList();
        TriangulateModifierDefinition primary;
        if (triangulates.Count == 0)
        {
            primary = new TriangulateModifierDefinition();
            terrain.Modifiers.Insert(0, primary);
        }
        else
        {
            primary = triangulates[0];
        }

        foreach (TriangulateModifierDefinition duplicate in triangulates.Skip(1))
        {
            MergeSourceSet(primary.OuterBoundaries, duplicate.OuterBoundaries);
            MergeSourceSet(primary.HideBoundaries, duplicate.HideBoundaries);
            MergeSourceSet(primary.ShowBoundaries, duplicate.ShowBoundaries);
            MergeSourceSet(primary.DataClipBoundaries, duplicate.DataClipBoundaries);
        }
    }

    /// <summary>
    /// Legacy promotions that are not tied to a schema version: each is idempotent and only does work when
    /// the old shape is present. Runs after the versioned steps.
    /// </summary>
    internal static void ApplyLegacyPromotions(
        TerrainDefinition terrain,
        int sourceSchemaVersion,
        ModelUnitContext unitContext)
    {
        PromoteLegacyTolerance(terrain);
        PromoteLegacyDetailSize(terrain, sourceSchemaVersion, unitContext);
    }

    /// <summary>
    /// Legacy promotions for the zone family (collage and mesh-areas modifiers become zones). Not
    /// version-gated.
    /// </summary>
    internal static void MigrateZones(TerrainDefinition terrain)
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
    /// Legacy promotions for the analysis family: the terrain-level slope preview and earthwork sources
    /// and the single last-analysis result move onto analysis cards. Not version-gated. Runs after the
    /// analyses have been normalized.
    /// </summary>
    internal static void PromoteLegacyAnalyses(TerrainDefinition terrain)
    {
        if (terrain.Analyses.Count > 0)
        {
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

    /// <summary>Drops the pre-v32 combined boundary once the versioned step has had its chance to read it.</summary>
    internal static void DropLegacyBoundaries(TerrainDefinition terrain)
    {
        foreach (GeometryInputModifierDefinition input in terrain.Modifiers.OfType<GeometryInputModifierDefinition>())
            input.LegacyBoundary = null;
    }

    private static void MergeSourceSet(SourceReferenceSet target, SourceReferenceSet source)
    {
        target.AddObjects(source.ObjectIds);
        target.AddLayers(source.LayerPaths);
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
            IEnumerable<ITerrainContentItem> content = terrain.Analyses
                .Cast<ITerrainContentItem>()
                .Concat(terrain.Annotations);
            foreach (ITerrainContentItem item in content)
            {
                var clone = TerrainRuntimeCacheCloner.CloneAnalysis(terrain.LegacyLastAnalysis);
                if (clone == null)
                    continue;

                clone.AnalysisId = item.Id;
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
