namespace MoleHill.Rhino.Model;

/// <summary>
/// Checks the built surface against accessibility gradient rules, against a standard the project sets.
/// </summary>
/// <remarks>
/// <para>Not a slope analysis with a threshold. Slope measures each face's steepest fall; accessibility
/// rules are measured with a level over a length, and (for routes) split by the direction of travel. See
/// <c>docs/backlog.md</c> → B13 and <c>GradientComplianceAnalyzer</c>.</para>
///
/// <para>Two kinds of input. <see cref="LevelAreas"/> (landings, turning spaces, plazas) have no direction
/// of travel and one limit in every direction. <see cref="Routes"/> have a direction, so their slope is
/// split into running slope (walk or ramp) and cross slope. Where both cover the same ground the level
/// area wins, because it is the stricter and more specific claim.</para>
/// </remarks>
public sealed class GradientComplianceAnalysisDefinition : AnalysisDefinition
{
    /// <summary>The standard checked against, copied in full. See <see cref="GradientRuleSet"/>.</summary>
    public GradientRuleSet Rules { get; set; } = GradientRulePresets.Default.Create();

    /// <summary>Closed curves bounding the areas that must be level. Nested curves make holes.</summary>
    public SourceReferenceSet LevelAreas { get; set; } = new();

    /// <summary>Curves along the centre of accessible routes. The drawing direction does not matter.</summary>
    public SourceReferenceSet Routes { get; set; } = new();

    /// <summary>Width of the corridor checked either side of each route, in model units.</summary>
    public double RouteWidth { get; set; } = 1.5;

    /// <summary>
    /// The length the gradient is averaged over, in model units: the level a surveyor would lay across
    /// the surface. Zero measures each triangle alone, which on survey-derived ground fails landings a
    /// level would pass. Not part of the standard, because no standard states one, so it is not in
    /// <see cref="Rules"/>. The default is a wheelchair's turning diameter.
    /// </summary>
    public double MeasurementLength { get; set; } = 1.5;

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return LevelAreas;
        yield return Routes;
    }

    public GradientComplianceAnalysisDefinition()
    {
        Label = "Gradient Compliance";
    }
}
