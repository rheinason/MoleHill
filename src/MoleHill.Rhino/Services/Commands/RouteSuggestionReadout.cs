using System.Globalization;
using MoleHill.Core.Analysis;
using MoleHill.Core.Routing;
using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The words mhSuggestRoute prints and stores: the per-route readout, the curve's name and user text,
/// and the failure messages. Slopes go through <see cref="SlopeInput"/> in the user's unit and lengths
/// carry the model unit, so a route reads the same at the command line, in Properties and on the panel.
/// </summary>
internal static class RouteSuggestionReadout
{
    public const string UserTextPrefix = "MoleHill.Route.";

    public static string Slope(double ratio, SlopeAnalyzer.SlopeUnit unit)
    {
        string value = SlopeInput.FormatValueForReport(ratio, unit, 1);
        return unit == SlopeAnalyzer.SlopeUnit.Ratio ? value : value + SlopeInput.Suffix(unit);
    }

    /// <summary>
    /// One command-line line per route: "Route 1: 523.40 m, max grade 4.9%, mean 2.1%, cut 12.0 m³,
    /// fill 9.5 m³, min radius 10.00 m" — and, when corners could not take the requested radius, how
    /// many are tighter and why.
    /// </summary>
    public static string Describe(
        int rank,
        RouteMetrics metrics,
        RouteFilletSummary fillet,
        double requestedRadius,
        ModelUnitContext units,
        SlopeAnalyzer.SlopeUnit unit) =>
        $"Route {rank}: {units.FormatLength(metrics.PlanLength)}, " +
        $"max grade {Slope(metrics.MaxGrade, unit)}, mean {Slope(metrics.MeanGrade, unit)}, " +
        $"cut {units.FormatVolume(metrics.CutVolume, "F1")}, fill {units.FormatVolume(metrics.FillVolume, "F1")}" +
        DescribeRadius(fillet, requestedRadius, units);

    /// <summary>The radius part of the readout; empty when corners were not rounded or the route has none.</summary>
    public static string DescribeRadius(RouteFilletSummary fillet, double requestedRadius, ModelUnitContext units)
    {
        if (!(requestedRadius > 0.0) || fillet.SmallestRadius is not double smallest)
            return string.Empty;

        string text = $", min radius {units.FormatLength(smallest)}";
        if (fillet.ReducedCorners <= 0)
            return text;

        var reasons = new List<(int Count, string Why)>
        {
            (fillet.TerrainLimitedCorners, "where a wider curve costs more than allowed or leaves the terrain"),
            (fillet.GradeLimitedCorners, "kept tighter because a wider curve would break the grade"),
            (fillet.ReversalCorners, "turning back on themselves"),
        };
        reasons.RemoveAll(reason => reason.Count <= 0);
        string corners = fillet.ReducedCorners == 1 ? "1 corner" : $"{fillet.ReducedCorners} corners";
        string under = $"{corners} under {units.FormatLength(requestedRadius)}";
        return text + (reasons.Count == 1
            ? $" ({under} {reasons[0].Why})"
            : $" ({under}: {string.Join(", ", reasons.Select(reason => $"{reason.Count} {reason.Why}"))})");
    }

    /// <summary>The curve's object name — short enough for the Properties panel, enough to tell the routes apart.</summary>
    public static string ObjectName(int rank, RouteMetrics metrics, ModelUnitContext units, SlopeAnalyzer.SlopeUnit unit) =>
        $"Suggested route {rank} ({units.FormatLength(metrics.PlanLength, "F0")}, max {Slope(metrics.MaxGrade, unit)})";

    /// <summary>Key/value pairs stored on the curve, for inspection after it has been edited away from them.</summary>
    public static IReadOnlyList<(string Key, string Value)> UserText(
        int rank,
        RouteMetrics metrics,
        double gradeLimit,
        double corridorWidth,
        RouteFilletSummary fillet,
        double requestedRadius,
        ModelUnitContext units,
        SlopeAnalyzer.SlopeUnit unit)
    {
        var text = new List<(string Key, string Value)>
        {
        (UserTextPrefix + "Rank", rank.ToString(CultureInfo.InvariantCulture)),
        (UserTextPrefix + "Length", units.FormatLength(metrics.PlanLength)),
        (UserTextPrefix + "MaxGrade", Slope(metrics.MaxGrade, unit)),
        (UserTextPrefix + "MeanGrade", Slope(metrics.MeanGrade, unit)),
        (UserTextPrefix + "Cut", units.FormatVolume(metrics.CutVolume, "F1")),
        (UserTextPrefix + "Fill", units.FormatVolume(metrics.FillVolume, "F1")),
        (UserTextPrefix + "GradeLimit", Slope(gradeLimit, unit)),
        (UserTextPrefix + "CorridorWidth", units.FormatLength(corridorWidth)),
        };

        if (requestedRadius > 0.0)
        {
            text.Add((UserTextPrefix + "RadiusLimit", units.FormatLength(requestedRadius)));
            if (fillet.SmallestRadius is double smallest)
                text.Add((UserTextPrefix + "MinRadius", units.FormatLength(smallest)));
            text.Add((UserTextPrefix + "TightCorners", fillet.ReducedCorners.ToString(CultureInfo.InvariantCulture)));
        }

        return text;
    }

    /// <summary>What went wrong and what to change, in the user's units.</summary>
    public static string DescribeFailure(RouteSuggestionResult result, double gradeLimit, SlopeAnalyzer.SlopeUnit unit) =>
        result.Failure switch
        {
            RouteFailure.TooFewPoints => "Pick a start point and an end point.",
            RouteFailure.PointOffTerrain => $"{PointName(result.FailedPoint)} is not over the terrain. Pick it on the terrain.",
            RouteFailure.NotConnected =>
                $"No route over the terrain joins {LegName(result.FailedLeg)}: a hole or the terrain border separates them.",
            RouteFailure.ExceedsMaxGrade =>
                $"No route within {Slope(gradeLimit, unit)} found for {LegName(result.FailedLeg)}: the best route needs " +
                $"{Slope(result.RequiredGrade ?? 0.0, unit)}. Raise MaxGrade, raise Earthworks so the route can wind " +
                "up the slope, or add a waypoint that lengthens it.",
            RouteFailure.InvalidSettings => "The route options cannot be searched with. Check MaxGrade, the weights and CorridorWidth.",
            _ => "No route was found.",
        };

    private static string PointName(int index) => index switch
    {
        0 => "The start point",
        _ => $"Point {index + 1}",
    };

    private static string LegName(int leg) => leg switch
    {
        < 0 => "the picked points",
        0 => "the start and the next point",
        _ => $"point {leg + 1} and point {leg + 2}",
    };
}
