namespace MoleHill.Rhino.Model;

/// <summary>
/// The signed delta between this terrain and a reference, as colour on the mesh and — optionally — as
/// drawn lines.
///
/// The colour map answers "where did I move ground, and how much"; the drawn outputs answer the same
/// question on paper. Both read the one delta field, which is why they live on one analysis: the
/// Earthworks analysis beside this one owns the *volumes*, and nothing should compute the delta twice.
/// </summary>
[ModelLengthMembers("RangeLow", "RangeHigh", "ColorInterval")]
public sealed class CutFillAnalysisDefinition : ReferenceComparisonAnalysisDefinition
{
    /// <summary>Draw contours of the delta itself — "cut deeper than 1 m" as a line, not a colour.</summary>
    public bool ShowDeltaContours { get; set; }

    /// <summary>
    /// Spacing of the delta contours, in model length. Levels step outwards from zero in both directions,
    /// so an interval of 0.5 draws at ±0.5, ±1.0 and so on; the zero level itself belongs to
    /// <see cref="ShowBalanceLine"/>.
    /// </summary>
    [ModelLength]
    public double DeltaContourInterval { get; set; } = 0.5;

    /// <summary>
    /// Draw the balance line — where the delta crosses zero, so cut meets fill. It is a level of the same
    /// field as the contours, but it means something different from every other level (it is the decision,
    /// not a depth), so it draws on its own layer and is switched on its own.
    /// </summary>
    public bool ShowBalanceLine { get; set; }

    /// <summary>Explicit colour for the delta contours. Null takes the colour from the role's layer.</summary>
    public int? DeltaContourColorArgb { get; set; }

    /// <summary>Explicit colour for the balance line. Null takes the colour from the role's layer.</summary>
    public int? BalanceLineColorArgb { get; set; }

    /// <summary>Whether either drawn output is switched on, and so whether the delta field is needed.</summary>
    public bool DrawsDeltaOutput => ShowDeltaContours || ShowBalanceLine;

    public CutFillAnalysisDefinition()
    {
        Label = "Cut / Fill";
        PalettePreset = "cool-warm";
        RangeLow = -1.0;
        RangeHigh = 1.0;
    }
}
