namespace MoleHill.Core.Routing;

/// <summary>
/// How a route's corners were rounded. <see cref="SmallestRadius"/> is null when no corner was rounded
/// (no minimum radius, or a route without corners); <see cref="ReducedCorners"/> counts corners whose
/// segments were too short for the requested radius and took the largest that fits.
/// </summary>
public readonly record struct RouteFilletSummary(double? SmallestRadius, int ReducedCorners)
{
    public static RouteFilletSummary None => new(null, 0);

    internal RouteFilletSummary Merge(RouteFilletSummary other) => new(
        SmallestRadius is double a && other.SmallestRadius is double b ? Math.Min(a, b) : SmallestRadius ?? other.SmallestRadius,
        ReducedCorners + other.ReducedCorners);
}
