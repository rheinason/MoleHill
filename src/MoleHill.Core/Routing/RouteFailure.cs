namespace MoleHill.Core.Routing;

/// <summary>Why no route came back. A host turns these into its own wording and units.</summary>
public enum RouteFailure
{
    None,

    /// <summary>A grade limit, weight or width that cannot be searched with.</summary>
    InvalidSettings,

    /// <summary>Fewer than two points.</summary>
    TooFewPoints,

    /// <summary><see cref="RouteSuggestionResult.FailedPoint"/> is not over the terrain.</summary>
    PointOffTerrain,

    /// <summary>No path over the terrain joins the two ends of <see cref="RouteSuggestionResult.FailedLeg"/>.</summary>
    NotConnected,

    /// <summary>
    /// The best route found for <see cref="RouteSuggestionResult.FailedLeg"/> is too short for its height
    /// difference: <see cref="RouteSuggestionResult.RequiredGrade"/> exceeds the limit.
    /// </summary>
    ExceedsMaxGrade,
}
