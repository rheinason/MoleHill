namespace MoleHill.Rhino.Model;

/// <summary>
/// Grades the terrain away from a drawn line. The curve's own Z is the finished elevation — nothing
/// is inferred from the terrain — and the batters run out to daylight on both sides.
/// </summary>
/// <remarks>
/// There is no per-side "off" switch, because there is no such thing as an ungraded side: a line at an
/// authored elevation is a discontinuity, so if one side got no batter its faces would run from the
/// authored Z straight to whatever existing vertices were nearest. The controls are therefore the
/// slopes each side leaves at. Where the terrain already meets the line, the batter builder measures
/// no difference and emits nothing, which is what "this side needs no grading" looks like.
/// </remarks>
public sealed class GradeLineModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Lines { get; set; } = new();

    /// <summary>Main (fill) batter slope in degrees, used where terrain sits below the line.</summary>
    [UnitFree("A slope angle; only the reach scales.")]
    public double SlopeAngle { get; set; } = 33.0;

    /// <summary>Cut-side batter slope override in degrees. 0 = inherit <see cref="SlopeAngle"/>.</summary>
    [UnitFree("A slope angle; only the reach scales.")]
    public double CutSlopeAngle { get; set; }

    /// <summary>Opt-in switch for an asymmetric section. Off keeps both sides on the shared pair.</summary>
    public bool UseAsymmetricSides { get; set; }

    /// <summary>Left-side cut override in degrees. 0 = inherit the shared pair. Left is the side the
    /// curve's plan normal points to, following the curve's own direction.</summary>
    [UnitFree("A slope angle; only the reach scales.")]
    public double LeftCutSlopeAngle { get; set; }

    [UnitFree("A slope angle; only the reach scales.")]
    public double LeftFillSlopeAngle { get; set; }

    [UnitFree("A slope angle; only the reach scales.")]
    public double RightCutSlopeAngle { get; set; }

    [UnitFree("A slope angle; only the reach scales.")]
    public double RightFillSlopeAngle { get; set; }

    [ModelLength]
    public double MaxDistance { get; set; }

    /// <summary>
    /// Grade through earlier breaklines instead of stopping at them. Off, every breakline and graded edge
    /// upstream is a hard line this modifier may not cross. On, it regrades across them, and the parts of
    /// them it regraded are dropped, so later stages do not pull the old ground back.
    /// </summary>
    public bool GradeThroughBreaklines { get; set; }

    public GradeLineModifierDefinition()
    {
        Label = "Grade Line";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Lines;
    }
}
