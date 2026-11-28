using MoleHill.Shared;

namespace MoleHill.Rhino.Model;

/// <summary>
/// Elevation labels at points, along curves or on a grid. Replaced the separate Spot Heights (Points)
/// and Spot Heights (Curve) cards in schema 34.
/// </summary>
public sealed class SpotHeightsAnnotationDefinition : SpotLabelAnnotationDefinition
{
    public SpotHeightsAnnotationDefinition()
    {
        Label = "Spot Heights";
        ValueFormat = "F2";
    }

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        if (string.IsNullOrWhiteSpace(ValueFormat))
            ValueFormat = "F2";
    }
}
