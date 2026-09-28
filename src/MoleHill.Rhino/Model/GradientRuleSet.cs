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

    /// <summary>Whether routes are checked for running and cross slope, and how a breach reads.</summary>
    public GradientRuleMode RouteMode { get; set; } = GradientRuleMode.Warn;

    /// <summary>The steepest running slope that is still a walk, in degrees. Steeper is a ramp.</summary>
    public double WalkMaxSlopeDegrees { get; set; }

    /// <summary>The steepest running slope allowed at all, as a ramp, in degrees.</summary>
    public double RampMaxSlopeDegrees { get; set; }

    /// <summary>The steepest a route may fall across its direction of travel, in degrees.</summary>
    public double CrossMaxSlopeDegrees { get; set; }

    /// <summary>
    /// The shortest level stretch of a route that counts as a landing, in model units. Landings are
    /// detected along routes, not drawn: see <c>RouteRunAnalyzer</c>. A shorter flat does not end a run.
    /// </summary>
    public double LandingMinLength { get; set; }

    /// <summary>Largest rise of a walk between landings, in model units. Zero means no limit.</summary>
    public double WalkMaxRise { get; set; }

    /// <summary>Largest rise of a ramp run between landings, in model units. Zero means no limit.</summary>
    public double RampMaxRise { get; set; }

    /// <summary>Longest ramp going per gradient. Empty means no going limit.</summary>
    public List<GradientGoingLimit> RampGoingLimits { get; set; } = new();

    /// <summary>Interpolate between going limits, as Approved Document M allows; otherwise the stricter applies.</summary>
    public bool InterpolateGoing { get; set; }

    /// <summary>
    /// Model units per metre that the lengths above are written in. Presets are defined in metres, so a
    /// preset chosen in a millimetre document must be scaled by this, and a comparison against the
    /// preset (for <see cref="IsModified"/>) must scale the same way. <c>TerrainUnitScaler</c> keeps it
    /// in step with the lengths when the document's units change.
    /// </summary>
    public double ModelUnitsPerMeter { get; set; } = 1.0;

    /// <summary>Multiplies every length by <paramref name="factor"/>, as a change of model units does.</summary>
    public void ScaleLengths(double factor)
    {
        LandingMinLength *= factor;
        WalkMaxRise *= factor;
        RampMaxRise *= factor;
        foreach (GradientGoingLimit limit in RampGoingLimits)
            limit.MaxGoing *= factor;
        ModelUnitsPerMeter *= factor;
    }

    public GradientRuleSet Clone()
    {
        var clone = (GradientRuleSet)MemberwiseClone();
        clone.RampGoingLimits = RampGoingLimits
            .Select(limit => new GradientGoingLimit { SlopeDegrees = limit.SlopeDegrees, MaxGoing = limit.MaxGoing })
            .ToList();
        return clone;
    }
}
