namespace MoleHill.Core.Routing;

/// <summary>
/// How a route's corners were rounded. <see cref="SmallestRadius"/> is null when no corner was rounded
/// (no minimum radius, or a route without corners); <see cref="ReducedCorners"/> counts every corner
/// that came out under the requested radius, and the rest say why.
/// </summary>
/// <param name="SmallestRadius">The tightest corner on the route; 0 when a corner was left sharp.</param>
/// <param name="ReducedCorners">Corners under the requested radius, for any reason.</param>
/// <param name="ReversalCorners">Of those, corners that turn back on themselves and stay sharp.</param>
/// <param name="GradeLimitedCorners">
/// Of those, corners left sharp or rounded tighter because a wider curve broke the grade limit (an arc
/// is shorter than its tangents, so the leg had no longer the length its height difference needs).
/// </param>
public readonly record struct RouteFilletSummary(
    double? SmallestRadius,
    int ReducedCorners,
    int ReversalCorners = 0,
    int GradeLimitedCorners = 0)
{
    public static RouteFilletSummary None => new(null, 0);

    /// <summary>
    /// Corners under the radius because their segments are too short for it, and lengthening them —
    /// widening the curve across the neighbouring corners — would cost more than the route allows there
    /// or leave the terrain.
    /// </summary>
    public int TerrainLimitedCorners => ReducedCorners - ReversalCorners - GradeLimitedCorners;

    internal RouteFilletSummary Merge(RouteFilletSummary other) => new(
        SmallestRadius is double a && other.SmallestRadius is double b ? Math.Min(a, b) : SmallestRadius ?? other.SmallestRadius,
        ReducedCorners + other.ReducedCorners,
        ReversalCorners + other.ReversalCorners,
        GradeLimitedCorners + other.GradeLimitedCorners);
}
