using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

/// <summary>
/// Per-face aspect: which way the ground faces, coloured through a cyclic wheel.
///
/// The range is not stored as a user choice even though the base class carries one — aspect is always the
/// full compass, pinned by <see cref="RangeShape.Cyclic"/> — so <see cref="AnalysisDefinition.RangeLow"/>
/// and <see cref="AnalysisDefinition.RangeHigh"/> are set to a full turn here and the ramp card hides the
/// bounds rather than offering fields that cannot change anything.
/// </summary>
public sealed class AspectAnalysisDefinition : AnalysisDefinition
{
    /// <summary>
    /// Ground flatter than this has no aspect and is drawn neutral instead of being given an arbitrary
    /// bearing. Stored in degrees like every other slope in the model, and shown and typed in the user's
    /// own slope unit. Small by default: a survey-derived terrain is never exactly level, so a threshold of
    /// zero would colour numerical noise as though it were a hillside.
    /// </summary>
    [UnitFree("An angle in degrees; angles do not change with model units.")]
    public double FlatSlopeThresholdDegrees { get; set; } = 1.0;

    public AspectAnalysisDefinition()
    {
        Label = "Aspect";

        // The wheel closes on itself, so the seam at north is invisible.
        PalettePreset = "aspect-wheel";

        // Constant, not Gradient: each wheel stop's colour holds to the next, which turns the eight-stop
        // wheel into eight crisp sectors. A continuous wheel is a click away in the ramp card, but sectors
        // are what an aspect map is read for.
        ColorMode = AnalysisColorMapper.Mode.Constant;
        ColorInterval = 45.0;

        AutoColorRange = false;
        RangeLow = 0.0;
        RangeHigh = AnalysisRange.FullTurnDegrees;
    }
}
