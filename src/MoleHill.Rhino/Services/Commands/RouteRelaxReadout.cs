using System.Globalization;
using MoleHill.Core.Analysis;
using MoleHill.Core.Routing;
using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The words mhRelaxRoute shows: the score against the sketch, the figures, and each stretch the route does
/// not fit, short for the viewport and in full for the panel. Slopes in the user's unit, lengths with the
/// model unit, as <see cref="RouteSuggestionReadout"/> does for suggested routes.
/// </summary>
internal static class RouteRelaxReadout
{
    /// <summary>"76% better than your sketch", or "as your sketch" until it improves.</summary>
    public static string Score(double improvement) =>
        improvement > 0.005
            ? string.Format(CultureInfo.InvariantCulture, "{0:0}% better than your sketch", improvement * 100.0)
            : improvement < -0.005
                ? string.Format(CultureInfo.InvariantCulture, "{0:0}% worse than your sketch", -improvement * 100.0)
                : "as your sketch";

    /// <summary>The figures line: length, grades, cut and fill.</summary>
    public static string Figures(RouteMetrics metrics, ModelUnitContext units, SlopeAnalyzer.SlopeUnit unit) =>
        $"{units.FormatLength(metrics.PlanLength, "F0")}, max {RouteSuggestionReadout.Slope(metrics.MaxGrade, unit)}, " +
        $"cut {units.FormatVolume(metrics.CutVolume, "F0")}, fill {units.FormatVolume(metrics.FillVolume, "F0")}";

    /// <summary>The curve line: control points, tightest bend and complexity of what Keep will add.</summary>
    public static string Curve(FittedRouteCurve curve, ModelUnitContext units) =>
        $"{curve.ControlPointCount} control points" +
        (curve.SmallestRadius is double tightest ? $", tightest bend {units.FormatLength(tightest, "F1")}" : string.Empty) +
        $", complexity {curve.Complexity.Total.ToString("F1", CultureInfo.InvariantCulture)}";

    /// <summary>The short label drawn on the viewport at the stretch's deepest point.</summary>
    public static string IssueTag(RouteRelaxIssue issue, ModelUnitContext units) => issue.Kind switch
    {
        RouteRelaxIssueKind.Climb => $"Climb: +{units.FormatLength(issue.ExtraLength, "F0")} route",
        RouteRelaxIssueKind.TooShort => $"Too short: +{units.FormatLength(issue.ExtraLength, "F0")}",
        RouteRelaxIssueKind.Cut => $"Cut {units.FormatLength(issue.Depth, "F1")}",
        RouteRelaxIssueKind.Crossing => "Crosses itself",
        _ => $"Fill {units.FormatLength(issue.Depth, "F1")}",
    };

    /// <summary>The full sentence the panel lists, with what to do about it.</summary>
    public static string IssueSentence(RouteRelaxIssue issue, double maxGrade, ModelUnitContext units, SlopeAnalyzer.SlopeUnit unit)
    {
        string where = $"{units.FormatLength(issue.StartStation, "F0")}–{units.FormatLength(issue.EndStation, "F0")}";
        return issue.Kind switch
        {
            RouteRelaxIssueKind.Climb =>
                $"{where}: the ground climbs at {RouteSuggestionReadout.Slope(issue.GroundGrade, unit)}. At " +
                $"{RouteSuggestionReadout.Slope(maxGrade, unit)} it needs about {units.FormatLength(issue.ExtraLength, "F0")} more route. " +
                "Help me can find switchbacks.",
            RouteRelaxIssueKind.TooShort =>
                $"The route is too short for the height between its ends: it needs {RouteSuggestionReadout.Slope(issue.GroundGrade, unit)}, " +
                $"or about {units.FormatLength(issue.ExtraLength, "F0")} more route. Help me can find switchbacks.",
            RouteRelaxIssueKind.Cut =>
                $"{where}: cuts up to {units.FormatLength(issue.Depth, "F1")} into the ground.",
            RouteRelaxIssueKind.Crossing =>
                $"At {units.FormatLength(issue.StartStation, "F0")} the route crosses itself. Drag it apart, or use Back to sketch to start again.",
            _ => $"{where}: fills up to {units.FormatLength(issue.Depth, "F1")} over the ground.",
        };
    }
}
