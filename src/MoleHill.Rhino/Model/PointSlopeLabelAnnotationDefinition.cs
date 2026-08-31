using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

public sealed class PointSlopeLabelAnnotationDefinition : BlockAttributeAnnotationDefinition
{
    public SlopeAnalyzer.SlopeUnit Unit { get; set; } = SlopeAnalyzer.SlopeUnit.Percent;

    public bool FlipDirection { get; set; }

    public PointSlopeLabelAnnotationDefinition()
    {
        Label = "Spot Slope (Points)";
        ValueFormat = "F1";
    }
}
