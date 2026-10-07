using MoleHill.Shared;

namespace MoleHill.Rhino.Model;

public sealed class CurveElevationLabelAnnotationDefinition : BlockAttributeAnnotationDefinition
{
    public double Interval { get; set; } = 10.0;

    public CurveElevationLabelAnnotationDefinition()
    {
        Label = "Spot Heights (Curve)";
        ValueFormat = "F2";
    }

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        Interval = Interval > 0.0
            ? Interval
            : unitContext.FromMeters(10.0);
        if (string.IsNullOrWhiteSpace(ValueFormat))
            ValueFormat = "F2";
    }
}
