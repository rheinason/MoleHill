using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

// JSON polymorphism is registry-driven (Services/TerrainJsonTypeResolver reads AnalysisTypeRegistry),
// not [JsonDerivedType] — registering an AnalysisTypeDescriptor is enough. Discriminators are unchanged.
public abstract class AnalysisDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = "Analysis";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 2;

    /// <summary>When true (the default) annotation size comes from the terrain's Rhino dimension style
    /// rather than this definition's stored absolute height, so drawing standards live in Rhino's
    /// Annotation Styles editor. Documents saved before schema 27 are migrated to false so their existing
    /// explicit heights are preserved exactly.</summary>
    public bool FollowsAnnotationStyle { get; set; } = true;

    public string PalettePreset { get; set; } = MoleHill.Rhino.Services.SlopePreviewPaletteCatalog.DefaultKey;

    public double RangeLow { get; set; }

    public double RangeHigh { get; set; }

    /// <summary>Whether the preview interpolates the palette or classifies values into bands.</summary>
    public AnalysisColorMapper.Mode ColorMode { get; set; } = AnalysisColorMapper.Mode.Gradient;

    /// <summary>Width of a stepped color band in the analysis unit. Zero selects an automatic step.</summary>
    public double ColorInterval { get; set; }

    /// <summary>When true, the display range is derived from the latest analysis result.</summary>
    public bool AutoColorRange { get; set; } = true;

    public virtual IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield break;
    }
}
