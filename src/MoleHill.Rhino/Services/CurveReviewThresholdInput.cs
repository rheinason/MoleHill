// Reads and writes mhInspectCurve rule thresholds as field text, the grade limit in the user's slope unit.
using System.Globalization;
using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The text a rule's threshold field shows and the value typed text commits. Kept out of the form so the
/// two rules the inspector once got wrong are testable without a window:
///
/// <para>The maximum grade is a slope, so it displays and parses through <see cref="SlopeInput"/> in the
/// user's slope unit (stored as a percent, as the rule evaluator compares it). The vertical grade change
/// stays in percentage points: it is an algebraic difference of two grades, not a slope a unit can
/// re-express — in degrees it would read as an angle it is not.</para>
///
/// <para>Text that does not parse — including an empty field — is rejected rather than read as zero.
/// A rule already has "no limit": its mode set to Off.</para>
/// </summary>
internal static class CurveReviewThresholdInput
{
    public static string Format(CurveReviewRuleKind kind, double threshold, SlopeAnalyzer.SlopeUnit slopeUnit) =>
        kind == CurveReviewRuleKind.MaximumGrade
            ? SlopeInput.FormatValue(threshold / 100.0, slopeUnit)
            : threshold.ToString("F2", CultureInfo.CurrentCulture);

    public static bool TryParse(
        CurveReviewRuleKind kind,
        string? text,
        SlopeAnalyzer.SlopeUnit slopeUnit,
        out double threshold)
    {
        threshold = 0.0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (kind == CurveReviewRuleKind.MaximumGrade)
        {
            // A grade limit is a magnitude — the evaluator compares the absolute grade against it.
            if (!SlopeInput.TryParse(text, slopeUnit, out double ratio))
                return false;
            threshold = Math.Abs(ratio) * 100.0;
            return double.IsFinite(threshold);
        }

        string trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out threshold) &&
            !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold))
            return false;
        return double.IsFinite(threshold);
    }
}
