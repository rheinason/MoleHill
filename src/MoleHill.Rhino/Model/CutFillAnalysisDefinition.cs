namespace MoleHill.Rhino.Model;

public sealed class CutFillAnalysisDefinition : AnalysisDefinition
{
    public CutFillAnalysisDefinition()
    {
        Label = "Cut / Fill";
        PalettePreset = "cool-warm";
        RangeLow = -1.0;
        RangeHigh = 1.0;
    }
}
