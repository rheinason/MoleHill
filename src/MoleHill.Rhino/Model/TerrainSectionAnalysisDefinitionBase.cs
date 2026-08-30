using System.Text.Json.Serialization;
namespace MoleHill.Rhino.Model;

public abstract class TerrainSectionAnalysisDefinitionBase : AnalysisDefinition
{
    public const int DefaultCutColorArgb = unchecked((int)0xFFEB462D);

    public const int DefaultFillColorArgb = unchecked((int)0xFF4C849E);

    public SourceReferenceSet Sources { get; set; } = new();

    public List<Guid> ComparisonTerrainIds { get; set; } = new();

    /// <summary>
    /// Another MoleHill terrain to treat as existing ground. Optional: <see cref="CutFillReference"/> can
    /// supply the reference instead, and usually does — requiring a whole second terrain meant cut/fill
    /// shading was unreachable for anyone modelling one surface against a surveyed mesh.
    /// </summary>
    public Guid? CutFillReferenceTerrainId { get; set; }

    /// <summary>
    /// Rhino meshes, surfaces or extrusions to treat as existing ground, sliced along the same cut line as
    /// the terrain. Takes precedence over <see cref="CutFillReferenceTerrainId"/> when both are set.
    /// </summary>
    public SourceReferenceSet CutFillReference { get; set; } = new();

    public bool ShowCutFillRegions { get; set; } = true;

    // Cut and fill regions are emitted as hatches whose appearance is layer-driven (see
    // SectionOutputLayers and the office layer template), so these three no longer affect output. They are
    // kept so existing documents round-trip unchanged, and so the layer template can seed its cut/fill
    // layers from the same two colours; their panel rows were removed because editing them did nothing.
    public int CutColorArgb { get; set; } = DefaultCutColorArgb;

    public int FillColorArgb { get; set; } = DefaultFillColorArgb;

    public int CutFillOpacityPercent { get; set; } = 40;

    /// <summary>Hatch pattern for cut regions. Emitted as a real Rhino hatch so the fill prints; the
    /// pattern itself is edited in Rhino's hatch pattern table.</summary>
    public string? CutHatchPatternName { get; set; }

    /// <summary>Hatch pattern for fill regions.</summary>
    public string? FillHatchPatternName { get; set; }

    /// <summary>Pattern scale for the generated hatch. 0 (the default) derives a scale from the annotation
    /// text height, so the fill reads as a texture at whatever scale the drawing is set up for. A pattern's
    /// native spacing is arbitrary — Rhino's Hatch1 is 0.125 model units — so a fixed scale of 1 prints as
    /// solid black on a real section. Set a positive value to override.</summary>
    public double HatchScale { get; set; }

    /// <summary>Pattern rotation, in degrees, passed to the generated hatch.</summary>
    public double HatchRotationDegrees { get; set; }

    /// <summary>
    /// Pre-schema-30 output layer, kept only so <c>MigrateLayerRouting</c> can carry a customised
    /// path into the document's own layer template. Routing comes from the card's role now.
    /// </summary>
    [JsonPropertyName("outputLayerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOutputLayerPath { get; set; }

    public int? ColorArgb { get; set; }

    public double InsertionOriginX { get; set; }

    public double InsertionOriginY { get; set; }

    public double InsertionOriginZ { get; set; }

    public double InsertionXAxisX { get; set; } = 1.0;

    public double InsertionXAxisY { get; set; }

    public double InsertionXAxisZ { get; set; }

    public double InsertionYAxisX { get; set; }

    public double InsertionYAxisY { get; set; } = 1.0;

    public double InsertionYAxisZ { get; set; }

    public bool HasInsertionPlane { get; set; }

    public double TextHeight { get; set; } = 1.0;

    /// <summary>
    /// Vertical scale relative to horizontal. 1 draws the section true to shape; higher values stretch
    /// elevations so gentle ground is readable — 5x or 10x is ordinary for a landform section, where the
    /// interesting relief is a metre or two across a hundred.
    ///
    /// Lives on the base because every section type needs it. It was previously declared separately on
    /// two of the three, and the plain Section Cut simply passed 1.0, so the one section people reach for
    /// first was the one that could not be exaggerated.
    /// </summary>
    public double VerticalExaggeration { get; set; } = 1.0;

    /// <summary>True when cut/fill shading has something to compare against.</summary>
    public bool HasCutFillReference =>
        CutFillReferenceTerrainId.HasValue || CutFillReference.HasReferences;

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
        yield return CutFillReference;
    }
}
