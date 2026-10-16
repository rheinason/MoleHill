namespace MoleHill.Core.Routing;

/// <summary>
/// One suggested route: its exact geometry as straight and arc spans whose profile obeys the requested
/// grade limit, a polyline through it for measuring and previewing, and the figures a designer compares
/// alternatives by.
/// </summary>
public sealed class RouteAlternative
{
    internal RouteAlternative(
        IReadOnlyList<RouteSpan> spans,
        double[] pointsXyz,
        RouteMetrics metrics,
        RouteFilletSummary fillet,
        double searchCost,
        RouteComplexity complexity)
    {
        Spans = spans;
        PointsXyz = pointsXyz;
        Metrics = metrics;
        Fillet = fillet;
        SearchCost = searchCost;
        Complexity = complexity;
    }

    /// <summary>
    /// The route as built, start to end. All straights when no minimum radius was asked for — the
    /// route is then exactly the polyline <see cref="PointsXyz"/>.
    /// </summary>
    public IReadOnlyList<RouteSpan> Spans { get; }

    /// <summary>
    /// Flat XYZ points along the route, passing through every waypoint: the vertices of the straights,
    /// and every degree of each arc. The metrics are measured on these.
    /// </summary>
    public double[] PointsXyz { get; }

    public int PointCount => PointsXyz.Length / 3;

    public bool HasArcs => Spans.Any(span => span.IsArc);

    public RouteMetrics Metrics { get; }

    public RouteFilletSummary Fillet { get; }

    /// <summary>How much the route's curvature and grade change (<see cref="RouteComplexity"/>).</summary>
    public RouteComplexity Complexity { get; }

    /// <summary>
    /// The drawn polyline priced by the search's own cost function, without the diversity penalty —
    /// what the alternatives are ranked by. Comparable only within one result.
    /// </summary>
    public double SearchCost { get; }
}
