using MoleHill.Shared;

namespace MoleHill.Rhino.Model;

public sealed class LongitudinalSectionAnnotationDefinition : TerrainSectionAnnotationDefinitionBase
{
    /// <summary>
    /// Distance between elevation samples along the curve. Zero is Auto: the terrain's Detail Size, so the
    /// profile is as fine as the terrain it reads (design review Step 33). A saved card keeps its interval.
    /// </summary>
    [ModelLength]
    public double SampleInterval { get; set; }

    public bool ShowElevationGrid { get; set; } = true;

    [ModelLength]
    public double ElevationGridInterval { get; set; }

    public bool ShowStationLabels { get; set; } = true;

    [ModelLength]
    public double StationLabelInterval { get; set; }

    public bool ShowBaseline { get; set; } = true;

    public LongitudinalSectionAnnotationDefinition()
    {
        Label = "Section Along Curve";
    }

    public override void NormalizeAfterLoad(ModelUnitContext unitContext, Guid ownerTerrainId)
    {
        base.NormalizeAfterLoad(unitContext, ownerTerrainId);
        SampleInterval = Math.Max(0.0, SampleInterval);
    }
}
