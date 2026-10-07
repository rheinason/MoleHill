namespace MoleHill.Rhino.Model;

public sealed class GradePathModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Paths { get; set; } = new();

    /// <summary>Opt-in switch for variable width. When false the modifier is a plain constant-width
    /// corridor and <see cref="WidthEdges"/> / <see cref="MaxEdgeDistance"/> are ignored entirely.</summary>
    public bool UseVariableWidth { get; set; }

    /// <summary>Optional plan-only curves that control either side of nearby centerlines. Their Z
    /// coordinates are ignored; centerline elevations author the finished path. Only read when
    /// <see cref="UseVariableWidth"/> is true.</summary>
    public SourceReferenceSet WidthEdges { get; set; } = new();

    public double Width { get; set; } = 2.0;

    /// <summary>Main (fill) batter slope in degrees, used where terrain sits below the road. The cut
    /// slope inherits this value unless <see cref="CutSlopeAngle"/> overrides it.</summary>
    public double SlopeAngle { get; set; } = 33.0;

    /// <summary>Cut-side batter slope override in degrees (terrain above the road). 0 = inherit <see cref="SlopeAngle"/>.</summary>
    public double CutSlopeAngle { get; set; }

    public double MaxDistance { get; set; }

    /// <summary>
    /// Grade through earlier breaklines instead of stopping at them. Off, every breakline and graded edge
    /// upstream is a hard line this modifier may not cross. On, it regrades across them, and the parts of
    /// them it regraded are dropped, so later stages do not pull the old ground back.
    /// </summary>
    public bool GradeThroughBreaklines { get; set; }

    /// <summary>Maximum plan distance used to match width edges. Zero uses four times Width.</summary>
    public double MaxEdgeDistance { get; set; }

    public GradePathModifierDefinition()
    {
        Label = "Grade Path";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Paths;
        // Always enumerated, even with UseVariableWidth off, so stale-object cleanup and layer
        // rename tracking keep the parked references honest. The build stage does the gating.
        yield return WidthEdges;
    }

    public override void NormalizeAfterLoad()
    {
        base.NormalizeAfterLoad();
        WidthEdges ??= new SourceReferenceSet();
    }
}
