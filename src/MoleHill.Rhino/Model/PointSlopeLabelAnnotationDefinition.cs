using MoleHill.Core.Analysis;
using MoleHill.Shared;

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

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        if (string.IsNullOrWhiteSpace(ValueFormat))
            ValueFormat = "F1";
    }
}
