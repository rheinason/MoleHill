namespace MoleHill.Rhino.Model;

/// <summary>
/// Grade callout between the two endpoints of each source line: both ends are projected onto the
/// terrain, the chord grade is computed, and a connector line + downhill arrowhead + a "1:n (x%)"
/// text label are emitted at the midpoint. Inherits the block-attribute fields for reuse, but emits
/// plain geometry/text rather than block instances, so block name/scale are unused.
/// </summary>
public sealed class GradeBetweenPointsAnnotationDefinition : BlockAttributeAnnotationDefinition
{
    /// <summary>Text height of the callout label and the size basis for the downhill arrow.</summary>
    public double TextHeight { get; set; } = 1.0;

    public GradeBetweenPointsAnnotationDefinition()
    {
        Label = "Grade Callout";
        ValueFormat = "F1";
    }
}
