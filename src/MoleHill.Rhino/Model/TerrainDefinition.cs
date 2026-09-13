using System.Text.Json.Serialization;
using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;

namespace MoleHill.Rhino.Model;

public sealed class TerrainDefinition
{
    public const int CurrentSchemaVersion = 32;
    public const int DefaultTerrainColorArgb = unchecked((int)0xFFC7D2C2);
    public const string DefaultTerrainLayerPath = "MoleHill::Terrain";
    public const string DefaultAuxiliaryLayerPath = "MoleHill::Auxiliary";
    public const string DefaultAnnotationLayerPath = "MoleHill::Annotation";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid TerrainId { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Terrain";

    public bool LiveUpdateEnabled { get; set; } = true;

    public bool IsVisible { get; set; } = true;

    public bool IsLocked { get; set; } = false;

    public bool ProtectOutput { get; set; } = false;

    public int OutputTransparencyPercent { get; set; } = 0;

    public int TerrainColorArgb { get; set; } = DefaultTerrainColorArgb;

    public bool ShowTerrainMesh { get; set; } = true;

    public bool ShowZoneMeshes { get; set; } = true;

    public bool ShowAnalysisOutputs { get; set; } = true;

    public bool ShowMeshWires { get; set; } = false;

    public bool ShowSlowBuildWarning { get; set; } = true;

    public bool ShowSlopePreview { get; set; }

    /// <summary>
    /// Multiplier on the viewport thickness of every line this terrain previews — contours, waterflow,
    /// sections, annotation, markers. 1.0 is the thickness the line's role resolves to; raise it to
    /// read a busy plan on a dense screen.
    ///
    /// Purely a display preference: it never reaches baked geometry, whose weight belongs to the
    /// layer. It is also the one place preview and bake deliberately differ, now that everything else
    /// resolves through one appearance record — which is why the panel labels it as on-screen only.
    /// </summary>
    public double PreviewLineWeight { get; set; } = 1.0;

    /// <summary>
    /// Pre-schema-30 output layers, kept only so <c>LayerRoutingMigration</c> can carry a customised
    /// path into the document's own layer template, and so a document saved by this build does not
    /// silently lose one. Nothing reads them at build time — routing comes from the template.
    /// </summary>
    [JsonPropertyName("terrainLayerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTerrainLayerPath { get; set; }

    [JsonPropertyName("auxiliaryLayerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAuxiliaryLayerPath { get; set; }

    [JsonPropertyName("annotationLayerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAnnotationLayerPath { get; set; }

    /// <summary>
    /// Pre-schema-30 annotation style, kept only so <c>LayerRoutingMigration</c> can carry a chosen
    /// style into the document's own layer template, where it now lives per role — so section labels
    /// can differ from contour labels, which one style per terrain could not express. It never had a
    /// panel row of its own.
    /// </summary>
    [JsonPropertyName("annotationStyleName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAnnotationStyleName { get; set; }

    /// <summary>
    /// Layer template this terrain routes and styles its output through. Blank uses the document's
    /// active template, which is what almost every terrain wants.
    ///
    /// It exists so two terrains in one document can be drawn on separate layers — an existing
    /// surface against a proposed one — which is what the per-terrain output layer paths used to
    /// provide. Routing still lives entirely in templates: this chooses between them, it does not
    /// override any individual layer.
    /// </summary>
    public string? LayerTemplateName { get; set; }

    public string SlopePalettePreset { get; set; } = ColorRampPresets.DefaultKey;

    public double SlopeColorLowPercent { get; set; }

    public double SlopeColorHighPercent { get; set; }

    public double GlobalTolerance { get; set; }

    [JsonPropertyName("earthworkReference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReferenceSet? LegacyEarthworkReference { get; set; }

    [JsonPropertyName("earthworkBoundary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReferenceSet? LegacyEarthworkBoundary { get; set; }

    public List<ModifierDefinition> Modifiers { get; set; } = new();

    public List<MarkerDefinition> Markers { get; set; } = new();

    public List<TerrainObjectDefinition> Objects { get; set; } = new();

    public List<CollageZoneDefinition> Zones { get; set; } = new();

    public List<AnalysisDefinition> Analyses { get; set; } = new();

    /// <summary>
    /// Content that describes the terrain rather than evaluating it. Separate from <see cref="Analyses"/>
    /// since schema 31; documents saved before that carried both in one list and are split on load.
    ///
    /// There is deliberately no terrain-level visibility flag for annotations. An annotation <em>is</em>
    /// the drawing, so it is always drawn; the per-card <c>IsEnabled</c> checkbox is the only control.
    /// <see cref="ShowAnalysisOutputs"/> has no say here — that gate is what hid every label and section
    /// when someone turned off slope colours.
    /// </summary>
    public List<AnnotationDefinition> Annotations { get; set; } = new();

    public List<Guid> OutputObjectIds { get; set; } = new();

    public List<Guid> ZoneObjectIds { get; set; } = new();

    public List<Guid> AuxiliaryObjectIds { get; set; } = new();

    public List<Guid> MarkerObjectIds { get; set; } = new();

    public bool ReplacePreviouslyBaked { get; set; } = true;

    public List<Guid> BakedObjectIds { get; set; } = new();

    public string? LastBuildMessage { get; set; }

    [JsonIgnore]
    public List<GradingDiagnostic> LastStructuredDiagnostics { get; set; } = new();

    public DateTimeOffset? LastBuildUtc { get; set; }

    [JsonPropertyName("lastAnalysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerrainAnalysisSummary? LegacyLastAnalysis { get; set; }

    public List<TerrainAnalysisSummary> LastAnalysisResults { get; set; } = new();

    public IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        foreach (var modifier in Modifiers)
        {
            foreach (var sourceSet in modifier.EnumerateSourceSets())
                yield return sourceSet;
        }

        foreach (var marker in Markers)
        {
            foreach (var sourceSet in marker.EnumerateSourceSets())
                yield return sourceSet;
        }

        foreach (var obj in Objects)
        {
            foreach (var sourceSet in obj.EnumerateSourceSets())
                yield return sourceSet;
        }

        foreach (var zone in Zones)
        {
            yield return zone.Boundaries;
        }

        foreach (var analysis in Analyses)
        {
            foreach (var sourceSet in analysis.EnumerateSourceSets())
                yield return sourceSet;
        }

        foreach (var annotation in Annotations)
        {
            foreach (var sourceSet in annotation.EnumerateSourceSets())
                yield return sourceSet;
        }
    }

    public void EnsureBaseModifier()
    {
        TriangulateModifierDefinition? baseModifier = null;
        for (int index = 0; index < Modifiers.Count; index++)
        {
            if (Modifiers[index] is not TriangulateModifierDefinition triangulate)
                continue;

            if (baseModifier == null)
            {
                baseModifier = triangulate;
                continue;
            }

            Modifiers[index] = AddGeometryModifierDefinition.FromTriangulate(triangulate);
        }

        if (baseModifier == null)
        {
            baseModifier = new TriangulateModifierDefinition();
            Modifiers.Insert(0, baseModifier);
        }
        else
        {
            int baseIndex = Modifiers.IndexOf(baseModifier);
            if (baseIndex > 0)
            {
                Modifiers.RemoveAt(baseIndex);
                Modifiers.Insert(0, baseModifier);
            }
        }

    }
}
