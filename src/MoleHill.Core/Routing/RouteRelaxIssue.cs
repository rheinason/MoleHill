namespace MoleHill.Core.Routing;

/// <summary>What kind of stretch a relaxed route does not fit.</summary>
public enum RouteRelaxIssueKind
{
    /// <summary>The ground climbs faster than the limit for longer than a cut or fill can absorb: it needs more route.</summary>
    Climb,

    /// <summary>A bump the route cuts through deeper than the reporting depth.</summary>
    Cut,

    /// <summary>A dip the route fills deeper than the reporting depth.</summary>
    Fill,

    /// <summary>The whole route is too short for the height between its ends at the grade limit.</summary>
    TooShort,

    /// <summary>The route crosses itself in plan, at the issue's point. A road cannot cross itself at grade.</summary>
    Crossing,
}

/// <summary>
/// A stretch of a relaxed route that does not fit the ground within the grade limit, located so a host can
/// mark it and word it: between two plan stations, deepest at a point, and for a climb, how much longer
/// the route would have to be there.
/// </summary>
public readonly record struct RouteRelaxIssue(
    RouteRelaxIssueKind Kind,
    double StartStation,
    double EndStation,
    double X,
    double Y,
    double Depth,
    double GroundGrade,
    double ExtraLength);
