using MoleHill.Shared;

namespace MoleHill.Rhino.Model;

public sealed class ProjectedElevationLabelAnnotationDefinition : BlockAttributeAnnotationDefinition
{
    public ProjectedElevationLabelAnnotationDefinition()
    {
        Label = "Spot Heights (Points)";
        ValueFormat = "F2";
    }

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        if (string.IsNullOrWhiteSpace(ValueFormat))
            ValueFormat = "F2";
    }
}
