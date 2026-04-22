using System.Text.Json.Serialization;

namespace MoleHill.Rhino.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(EarthworkAnalysisDefinition), "earthwork")]
[JsonDerivedType(typeof(SlopeAnalysisDefinition), "slope")]
[JsonDerivedType(typeof(ElevationAnalysisDefinition), "elevation")]
[JsonDerivedType(typeof(CutFillAnalysisDefinition), "cut-fill")]
[JsonDerivedType(typeof(ContourAnalysisDefinition), "contour")]
[JsonDerivedType(typeof(CurveElevationLabelAnalysisDefinition), "curve-elevation-label")]
[JsonDerivedType(typeof(CurveSlopeLabelAnalysisDefinition), "curve-slope-label")]
[JsonDerivedType(typeof(ProjectedElevationLabelAnalysisDefinition), "projected-elevation-label")]
[JsonDerivedType(typeof(PointSlopeLabelAnalysisDefinition), "point-slope-label")]
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
