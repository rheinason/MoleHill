namespace MoleHill.Rhino.Model;

/// <summary>
/// Checks the built surface against accessibility gradient rules, against a standard the project sets.
/// </summary>
/// <remarks>
/// <para>Not a slope analysis with a threshold. Slope measures each face's steepest fall; accessibility
/// rules are measured with a level over a length, and (for routes) split by the direction of travel. See
/// <c>docs/backlog.md</c> → B13 and <c>GradientComplianceAnalyzer</c>.</para>
///
/// <para>Stage one checks <see cref="LevelAreas"/> only: landings, turning spaces and plazas, which have
/// no direction of travel and one limit in every direction. Routes, with running and cross slope, come
/// next.</para>
/// </remarks>
public sealed class GradientComplianceAnalysisDefinition : AnalysisDefinition
{
    /// <summary>The standard checked against, copied in full. See <see cref="GradientRuleSet"/>.</summary>
    public GradientRuleSet Rules { get; set; } = GradientRulePresets.Default.Create();

    /// <summary>Closed curves bounding the areas that must be level. Nested curves make holes.</summary>
    public SourceReferenceSet LevelAreas { get; set; } = new();

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
    }

    public GradientComplianceAnalysisDefinition()
    {
        Label = "Gradient Compliance";
    }
}
