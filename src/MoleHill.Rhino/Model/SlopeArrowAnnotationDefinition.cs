using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

/// <summary>
/// Flow / slope arrows: a regular XY grid is sampled across the terrain, and each node gets an arrow
/// block oriented downhill (with the slope magnitude as its value). Optional source curves act as a
/// boundary clip; with no sources the whole terrain is covered.
/// </summary>
public sealed class SlopeArrowAnnotationDefinition : BlockAttributeAnnotationDefinition
{
    public double GridSpacing { get; set; } = 5.0;

    public SlopeAnalyzer.SlopeUnit Unit { get; set; } = SlopeAnalyzer.SlopeUnit.Percent;

    public bool FlipDirection { get; set; }

    public SlopeArrowAnnotationDefinition()
    {
        Label = "Flow Arrows";
        ValueFormat = "F1";
    }
}
