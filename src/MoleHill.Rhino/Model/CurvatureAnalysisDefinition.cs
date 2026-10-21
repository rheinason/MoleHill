using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

/// <summary>
/// Curvature and landform: where the ground is convex, planar or concave, measured at a stated landform
/// size.
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
    /// <remarks>Zero means Auto: the size is resolved at build and preview time from the incoming mesh's
    /// plan extent (<c>LandformSize.Resolve</c>), so a 200 m site reads at about 20 m and a small garden at
    /// about 1 m. A positive value is used as typed. Always read the resolved value, never this raw one.
    /// Zero is unit-free under <c>TerrainUnitScaler</c>. A stored explicit value is kept as it is.</remarks>
    [ModelLength]
    public double FeatureSize { get; set; }

    /// <summary>
    /// How far, in model units, the ground may depart from a plane across one landform size and still count
    /// as planar: the sagitta <c>|mean curvature| * L^2 / 8</c> with <c>L</c> = <see cref="FeatureSize"/>. A
    /// height rather than a curvature so it reads on the card and follows the landform size by itself.
    /// The default is 50 mm: a curvature radius of 1 km at the 20 m Auto size on a 200 m site, and a survey-scale height that does not depend on the site.
    /// </summary>
    /// <remarks>Replaced the unreleased <c>PlanarRadius</c> (a radius of curvature). A stored
    /// <c>PlanarRadius</c> is ignored on load, which falls back to this default.</remarks>
    [ModelLength]
    public double FlatnessTolerance { get; set; } = 0.05;

    public CurvatureAnalysisDefinition()
    {
        Label = "Curvature";

        // Diverging, neutral at zero: concave cool, convex warm, the same reading as the landform colours.
        PalettePreset = "cool-warm";
        AutoColorRange = true;
        RangeLow = 0.0;
        RangeHigh = 0.0;
    }

    // Convex is positive, so the low end of the symmetric range is the concave one.
    public override (string Low, string High) GetRangeLabels() => ("Concave", "Convex");

    public override void NormalizeAfterLoad()
    {
        if (!double.IsFinite(FeatureSize) || FeatureSize < 0.0)
            FeatureSize = 0.0;
        if (!double.IsFinite(FlatnessTolerance) || FlatnessTolerance < 0.0)
            FlatnessTolerance = 0.0;
    }
}
