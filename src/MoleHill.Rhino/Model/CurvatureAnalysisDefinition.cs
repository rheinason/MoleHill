using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

/// <summary>
/// Curvature and landform: where the ground is convex or concave, and which landform that makes (ridge,
/// valley, shoulder, toe, peak, hollow, saddle, planar), measured at a stated landform size.
/// </summary>
/// <remarks>
/// <para>One measurement, two colourings. <see cref="Display"/> picks between the landform classes (flat
/// colours, a swatch key) and a continuous curvature value through the ramp. Both read the same fitted
/// field (Core <c>CurvatureAnalyzer</c>), so switching between them, or between measures, recolours without
/// measuring again; neither is a build input.</para>
/// <para>Curvature is in 1/model-unit and convex positive, which makes the colour range an inverse length:
/// it shrinks by the factor a length grows when the document's units change, declared by
/// <see cref="InverseModelLengthMembersAttribute"/>.</para>
/// </remarks>
[InverseModelLengthMembers("RangeLow", "RangeHigh", "ColorInterval")]
public sealed class CurvatureAnalysisDefinition : AnalysisDefinition
{
    /// <summary>Landform classes, or the curvature value on the ramp. Presentation only.</summary>
    [NotBuildInput]
    public CurvatureDisplay Display { get; set; } = CurvatureDisplay.Landforms;

    /// <summary>Which curvature the ramp shows in <see cref="CurvatureDisplay.Curvature"/> mode. Presentation only.</summary>
    [NotBuildInput]
    public CurvatureMeasure Measure { get; set; } = CurvatureMeasure.Mean;

    /// <summary>
    /// The radius the surface is fitted over, in model units: landforms about this size and larger are
    /// what the map shows, and detail much smaller averages away. Storage name kept as the design calls it;
    /// the card labels it "Landform Size".
    /// </summary>
    [ModelLength]
    public double FeatureSize { get; set; } = 5.0;

    /// <summary>
    /// Ground bending more gently than a circle of this radius, in a direction, counts as straight in that
    /// direction. A radius rather than a curvature so it reads as a length on the card: "flatter than a
    /// 100 m radius is planar".
    /// </summary>
    [ModelLength]
    public double PlanarRadius { get; set; } = 100.0;

    public CurvatureAnalysisDefinition()
    {
        Label = "Curvature";

        // Diverging, neutral at zero: concave cool, convex warm, the same reading as the landform colours.
        PalettePreset = "cool-warm";
        AutoColorRange = true;
        RangeLow = 0.0;
        RangeHigh = 0.0;
    }

    public override void NormalizeAfterLoad()
    {
        if (!double.IsFinite(FeatureSize) || FeatureSize < 0.0)
            FeatureSize = 0.0;
        if (!double.IsFinite(PlanarRadius) || PlanarRadius < 0.0)
            PlanarRadius = 0.0;
    }
}
