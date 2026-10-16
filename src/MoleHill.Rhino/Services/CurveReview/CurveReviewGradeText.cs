// Writes an inspected curve's grades in the user's slope unit.
using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The one way mhInspectCurve writes a grade: signed, in the user's slope unit (<see cref="SlopeUnitPreference"/>).
/// The analysis measures grades in percent, and the summary, the labels on the curve and the hover readout
/// used to print them as percent whatever the preference said — while every command that produced the curve
/// (mhSuggestRoute's readout, mhSlopeCurve) and Inspect's own rule rows used the preference. With degrees
/// chosen, a route built to 10° read "10.0°" in its readout and 17.6% in Inspect (2026-10-16).
/// </summary>
internal static class CurveReviewGradeText
{
    /// <summary>A grade given in percent, written signed in the user's unit: "+5.00%", "-2.86°", "+1:20.0".</summary>
    public static string Signed(double percent, int decimals = 2) => Signed(percent, SlopeUnitPreference.Current, decimals);

    public static string Signed(double percent, SlopeAnalyzer.SlopeUnit unit, int decimals = 2)
    {
        if (!double.IsFinite(percent))
            return "n/a";
        double ratio = Math.Abs(percent) / 100.0;
        string sign = percent > 0.0 ? "+" : percent < 0.0 ? "-" : string.Empty;
        string value = SlopeInput.FormatValueForReport(ratio, unit, unit == SlopeAnalyzer.SlopeUnit.Ratio ? 1 : decimals);
        return sign + (unit == SlopeAnalyzer.SlopeUnit.Ratio ? value : value + SlopeInput.Suffix(unit));
    }
}
