namespace MoleHill.Core.Routing;

/// <summary>
/// One suggested route: a simplified 3D polyline whose profile obeys the requested grade limit, and the
/// figures a designer compares alternatives by.
/// </summary>
public sealed class RouteAlternative
{
    internal RouteAlternative(double[] pointsXyz, RouteMetrics metrics, double searchCost)
    {
        PointsXyz = pointsXyz;
        Metrics = metrics;
        SearchCost = searchCost;
    }

    /// <summary>Flat XYZ vertices, start to end, passing through every waypoint.</summary>
    public double[] PointsXyz { get; }

    public int PointCount => PointsXyz.Length / 3;

    public RouteMetrics Metrics { get; }

    /// <summary>
    /// The drawn polyline priced by the search's own cost function, without the diversity penalty —
    /// what the alternatives are ranked by. Comparable only within one result.
    /// </summary>
    public double SearchCost { get; }
}
