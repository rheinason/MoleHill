using System.Text.Json.Serialization;

namespace MoleHill.Rhino.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SlopeAnalysisDefinition), "slope")]
[JsonDerivedType(typeof(ElevationAnalysisDefinition), "elevation")]
[JsonDerivedType(typeof(CutFillAnalysisDefinition), "cut-fill")]
public abstract class AnalysisDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = "Analysis";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 1;

    public string PalettePreset { get; set; } = MoleHill.Rhino.Services.SlopePreviewPaletteCatalog.DefaultKey;

    public double RangeLow { get; set; }

    public double RangeHigh { get; set; }
}
