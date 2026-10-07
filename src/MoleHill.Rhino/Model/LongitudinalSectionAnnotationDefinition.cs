using MoleHill.Shared;

namespace MoleHill.Rhino.Model;

public sealed class LongitudinalSectionAnnotationDefinition : TerrainSectionAnnotationDefinitionBase
{
    public double SampleInterval { get; set; } = 1.0;

    public bool ShowElevationGrid { get; set; } = true;

    public double ElevationGridInterval { get; set; }

    public bool ShowStationLabels { get; set; } = true;

    public double StationLabelInterval { get; set; }

    public bool ShowBaseline { get; set; } = true;

    public LongitudinalSectionAnnotationDefinition()
    {
        Label = "Section Along Curve";
    }

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        SampleInterval = SampleInterval > 0.0
            ? SampleInterval
            : unitContext.FromMeters(1.0);
    }
}
