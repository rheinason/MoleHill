using MoleHill.Core.Analysis;
using MoleHill.Shared;

namespace MoleHill.Rhino.Model;

/// <summary>
/// Slope labels with a downhill arrow at points, along curves or on a grid. Replaced Spot Slope (Points),
/// Spot Slope (Curve) and Flow Arrows - the grid placement - in schema 34.
/// </summary>
public sealed class SpotSlopeAnnotationDefinition : SpotLabelAnnotationDefinition
{
    public SlopeAnalyzer.SlopeUnit Unit { get; set; } = SlopeAnalyzer.SlopeUnit.Percent;

    /// <summary>Turns the arrow to point uphill.</summary>
    public bool FlipDirection { get; set; }

    public SpotSlopeAnnotationDefinition()
    {
        Label = "Spot Slope";
        ValueFormat = "F1";
    }

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        if (string.IsNullOrWhiteSpace(ValueFormat))
            ValueFormat = "F1";
    }
}
