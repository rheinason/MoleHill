namespace MoleHill.Rhino.Model;

/// <summary>
/// How one gradient compliance rule is applied: not checked, reported, or flagged as a failure.
/// These are the three states <c>mhInspectCurve</c> gives its rules, so a rule reads alike wherever it
/// appears.
/// </summary>
public enum GradientRuleMode
{
    /// <summary>Not checked. The rule's inputs are ignored and nothing is coloured for it.</summary>
    Off = 0,

    /// <summary>Checked and shown, but a breach is information rather than an error.</summary>
    Report = 1,

    /// <summary>Checked, and a breach is flagged as a failure.</summary>
    Warn = 2,
}
