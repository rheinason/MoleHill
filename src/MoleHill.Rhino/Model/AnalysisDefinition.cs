namespace MoleHill.Rhino.Model;

// JSON polymorphism is registry-driven (Services/TerrainJsonTypeResolver reads AnalysisTypeRegistry),
// not [JsonDerivedType] — registering an AnalysisTypeDescriptor is enough. Discriminators are unchanged.
public abstract class AnalysisDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = "Analysis";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 1;

    public string PalettePreset { get; set; } = MoleHill.Rhino.Services.SlopePreviewPaletteCatalog.DefaultKey;

    public double RangeLow { get; set; }

    public double RangeHigh { get; set; }

    public virtual IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield break;
    }
}
