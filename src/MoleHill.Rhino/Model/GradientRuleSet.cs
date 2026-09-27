namespace MoleHill.Rhino.Model;

/// <summary>
/// The accessibility gradient limits one compliance analysis checks against: a standard, as data.
/// </summary>
/// <remarks>
/// <para>Stored in full on the definition, not by preset name. Which standard applies is a fact about the
/// project, so the next person to open the file must get the same verdict, and a plug-in update that
/// corrects a preset must not silently change the verdict on an existing project.
/// <see cref="PresetKey"/> and <see cref="IsModified"/> are provenance only: they say where these numbers
/// came from, and nothing reads a limit through them.</para>
///
/// <para>This deliberately differs from <c>mhInspectCurve</c>, whose thresholds are a per-user
/// preference. Those describe how one person likes to review a curve; these describe what the project is
/// required to meet.</para>
///
/// <para>Slopes are stored in degrees, like every slope in the model, and shown in the user's unit.</para>
/// </remarks>
public sealed class GradientRuleSet
{
    /// <summary>The preset these rules were copied from, or null for rules written from scratch.</summary>
    public string? PresetKey { get; set; }

    /// <summary>True once any limit has been edited away from the preset's value.</summary>
    public bool IsModified { get; set; }

    /// <summary>Whether level areas (landings, turning spaces) are checked, and how a breach reads.</summary>
    public GradientRuleMode LevelAreaMode { get; set; } = GradientRuleMode.Warn;

    /// <summary>The steepest a level area may be in any direction, in degrees.</summary>
    public double LevelAreaMaxSlopeDegrees { get; set; }

    public GradientRuleSet Clone() => (GradientRuleSet)MemberwiseClone();
}
