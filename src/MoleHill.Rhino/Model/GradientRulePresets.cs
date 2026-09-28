namespace MoleHill.Rhino.Model;

/// <summary>
/// Built-in accessibility standards a gradient compliance card can start from.
/// </summary>
/// <remarks>
/// <para><b>Every number here is checked against the published text, and cites the clause.</b> A wrong
/// built-in compliance preset is worse than none: it looks authoritative and it is the one value nobody
/// re-checks. A standard whose text was not available to check does not ship as a preset, however well
/// known its figures are. Users on those standards start from "Custom" or from the nearest preset and
/// edit it, which the card marks as modified.</para>
///
/// <para>Checked 2026-09-27 against: the 2010 ADA Standards for Accessible Design, as published by the
/// US Access Board (access-board.gov/ada); and Approved Document M Volume 2, 2015 edition incorporating
/// the 2024 amendments (gov.uk). DIN 18040 and AS 1428.1 are paywalled and are not yet here.</para>
///
/// <para>Presets are copied into the definition when chosen, so editing a value here changes new cards
/// only. That is intended: see <see cref="GradientRuleSet"/>.</para>
/// </remarks>
public static class GradientRulePresets
{
    public const string CustomKey = "custom";

    public sealed record Preset(string Key, string Label, string Source, Func<GradientRuleSet> Create);

    public static IReadOnlyList<Preset> All { get; } = new[]
    {
        new Preset(
            "ada-2010",
            "ADA 2010 (US)",
            "2010 ADA Standards: §304.2 turning space and §405.7.1 ramp landings, slopes not steeper " +
            "than 1:48; §403.3 walking surfaces running slope 1:20 and cross slope 1:48; §405.2 ramp " +
            "running slope 1:12; §405.6 ramp run rise 760 mm; §405.7.3 landings 1525 mm long.",
            () => new GradientRuleSet
            {
                PresetKey = "ada-2010",
                LevelAreaMode = GradientRuleMode.Warn,
                LevelAreaMaxSlopeDegrees = RatioToDegrees(1.0, 48.0),
                RouteMode = GradientRuleMode.Warn,
                WalkMaxSlopeDegrees = RatioToDegrees(1.0, 20.0),
                RampMaxSlopeDegrees = RatioToDegrees(1.0, 12.0),
                CrossMaxSlopeDegrees = RatioToDegrees(1.0, 48.0),
                LandingMinLength = 1.525,
                RampMaxRise = 0.76,
            }),
        new Preset(
            "adm-vol2-2015",
            "Approved Doc M Vol 2 (England)",
            "Approved Document M Vol 2 (2015, 2024 amendments): §1.26(k) landings level, max 1:60 along " +
            "their length and 1:40 cross-fall; \"level\" is max 1:60 in the direction of travel. A level " +
            "area has no direction of travel, so it is checked at the stricter 1:60 in every direction. " +
            "§1.13(c) approaches less steep than 1:20 with cross-fall no steeper than 1:40 and a landing " +
            "every 500 mm of rise; §1.26(c) flights no longer than 10 m or rising more than 500 mm; Table 1 " +
            "goings 10 m at 1:20, 5 m at 1:15, 2 m at 1:12, interpolated between; §1.26(i) intermediate " +
            "landings 1.5 m long.",
            () => new GradientRuleSet
            {
                PresetKey = "adm-vol2-2015",
                LevelAreaMode = GradientRuleMode.Warn,
                LevelAreaMaxSlopeDegrees = RatioToDegrees(1.0, 60.0),
                RouteMode = GradientRuleMode.Warn,
                WalkMaxSlopeDegrees = RatioToDegrees(1.0, 20.0),
                RampMaxSlopeDegrees = RatioToDegrees(1.0, 12.0),
                CrossMaxSlopeDegrees = RatioToDegrees(1.0, 40.0),
                LandingMinLength = 1.5,
                WalkMaxRise = 0.5,
                RampMaxRise = 0.5,
                RampGoingLimits = new List<GradientGoingLimit>
                {
                    new() { SlopeDegrees = RatioToDegrees(1.0, 20.0), MaxGoing = 10.0 },
                    new() { SlopeDegrees = RatioToDegrees(1.0, 15.0), MaxGoing = 5.0 },
                    new() { SlopeDegrees = RatioToDegrees(1.0, 12.0), MaxGoing = 2.0 },
                },
                InterpolateGoing = true,
            }),
    };

    /// <summary>The standard a new card starts from when the document has no other to follow.</summary>
    public static Preset Default => All[0];

    public static Preset? Find(string? key) =>
        key == null ? null : All.FirstOrDefault(preset => string.Equals(preset.Key, key, StringComparison.Ordinal));

    /// <summary>A fresh rule set with no preset behind it, starting from the default's values.</summary>
    public static GradientRuleSet CreateCustom()
    {
        GradientRuleSet rules = Default.Create();
        rules.PresetKey = null;
        rules.IsModified = false;
        return rules;
    }

    /// <summary>
    /// The rules a new card on <paramref name="terrain"/> should start with: a copy of the standard already
    /// in use on that terrain, else anywhere in the document, else null (use the default). The country is
    /// set once per project, not once per card.
    /// </summary>
    public static GradientRuleSet? FindDocumentStandard(TerrainDefinition terrain, IEnumerable<TerrainDefinition> documentTerrains)
    {
        GradientComplianceAnalysisDefinition? existing =
            terrain.Analyses.OfType<GradientComplianceAnalysisDefinition>().FirstOrDefault() ??
            documentTerrains.SelectMany(other => other.Analyses)
                .OfType<GradientComplianceAnalysisDefinition>()
                .FirstOrDefault();
        return existing?.Rules.Clone();
    }

    /// <summary>
    /// Recomputes <see cref="GradientRuleSet.IsModified"/> after an edit, against the preset the rules
    /// came from. Compared rather than latched, so editing a value back to the preset's clears the flag.
    /// </summary>
    public static void RefreshModified(GradientRuleSet rules)
    {
        Preset? preset = Find(rules.PresetKey);
        rules.IsModified = preset != null && !HaveSameLimits(rules, CreateScaled(preset, rules.ModelUnitsPerMeter));
    }

    /// <summary>A preset's rules in a document whose model units are <paramref name="modelUnitsPerMeter"/> per metre.</summary>
    public static GradientRuleSet CreateScaled(Preset preset, double modelUnitsPerMeter)
    {
        GradientRuleSet rules = preset.Create();
        rules.ScaleLengths(modelUnitsPerMeter);
        return rules;
    }

    private static bool HaveSameLimits(GradientRuleSet a, GradientRuleSet b) =>
        a.LevelAreaMode == b.LevelAreaMode &&
        Math.Abs(a.LevelAreaMaxSlopeDegrees - b.LevelAreaMaxSlopeDegrees) <= 1e-9 &&
        a.RouteMode == b.RouteMode &&
        Math.Abs(a.WalkMaxSlopeDegrees - b.WalkMaxSlopeDegrees) <= 1e-9 &&
        Math.Abs(a.RampMaxSlopeDegrees - b.RampMaxSlopeDegrees) <= 1e-9 &&
        Math.Abs(a.CrossMaxSlopeDegrees - b.CrossMaxSlopeDegrees) <= 1e-9 &&
        SameLength(a.LandingMinLength, b.LandingMinLength) &&
        SameLength(a.WalkMaxRise, b.WalkMaxRise) &&
        SameLength(a.RampMaxRise, b.RampMaxRise) &&
        a.InterpolateGoing == b.InterpolateGoing &&
        a.RampGoingLimits.Count == b.RampGoingLimits.Count &&
        a.RampGoingLimits.Zip(b.RampGoingLimits).All(pair =>
            Math.Abs(pair.First.SlopeDegrees - pair.Second.SlopeDegrees) <= 1e-9 &&
            SameLength(pair.First.MaxGoing, pair.Second.MaxGoing));

    private static bool SameLength(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));

    private static double RatioToDegrees(double rise, double run) => Math.Atan2(rise, run) * 180.0 / Math.PI;
}
