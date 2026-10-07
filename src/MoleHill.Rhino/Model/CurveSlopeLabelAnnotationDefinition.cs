using MoleHill.Core.Analysis;
using MoleHill.Shared;

namespace MoleHill.Rhino.Model;

public sealed class CurveSlopeLabelAnnotationDefinition : BlockAttributeAnnotationDefinition
{
    [ModelLength]
    public double Interval { get; set; } = 10.0;

    public SlopeAnalyzer.SlopeUnit Unit { get; set; } = SlopeAnalyzer.SlopeUnit.Percent;

    public bool FlipDirection { get; set; }

    public CurveSlopeLabelAnnotationDefinition()
    {
        Label = "Spot Slope (Curve)";
        ValueFormat = "F1";
    }

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        Interval = Interval > 0.0
            ? Interval
            : unitContext.FromMeters(10.0);
        if (string.IsNullOrWhiteSpace(ValueFormat))
            ValueFormat = "F1";
    }
}
