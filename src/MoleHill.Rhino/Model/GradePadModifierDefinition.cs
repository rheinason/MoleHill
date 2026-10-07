namespace MoleHill.Rhino.Model;

public sealed class GradePadModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Boundaries { get; set; } = new();

    public SourceReferenceSet LockCurves { get; set; } = new();

    /// <summary>Main (fill) batter slope in degrees, used where terrain sits below the pad. The cut
    /// slope inherits this value unless <see cref="CutSlopeAngle"/> overrides it.</summary>
    [UnitFree("A slope angle; only the reach scales.")]
    public double SlopeAngle { get; set; } = 33.0;

    /// <summary>Cut-side batter slope override in degrees (terrain above the pad). 0 = inherit <see cref="SlopeAngle"/>.</summary>
    [UnitFree("A slope angle; only the reach scales.")]
    public double CutSlopeAngle { get; set; }

    [ModelLength]
    public double MaxDistance { get; set; }

    /// <summary>
    /// Grade through earlier breaklines instead of stopping at them. Off, every breakline and graded edge
    /// upstream is a hard line this modifier may not cross. On, it regrades across them, and the parts of
    /// them it regraded are dropped, so later stages do not pull the old ground back.
    /// </summary>
    public bool GradeThroughBreaklines { get; set; }

    public GradePadModifierDefinition()
    {
        Label = "Grade Pad";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Boundaries;
    }
}
