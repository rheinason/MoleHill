namespace MoleHill.Core.Routing;

/// <summary>
/// A relaxed route as it stands: its line with a grade-limited profile, the figures it is compared by, the
/// stretches it does not fit, and (when asked for) the editable curve.
/// </summary>
public sealed class RouteRelaxEvaluation
{
    internal RouteRelaxEvaluation(
        double[] pointsXyz,
        double[] groundZ,
        RouteMetrics metrics,
        double cost,
        double sketchCost,
        IReadOnlyList<RouteRelaxIssue> issues,
        FittedRouteCurve? curve,
        RouteMetrics? curveMetrics,
        double requiredGrade)
    {
        PointsXyz = pointsXyz;
        GroundZ = groundZ;
        Metrics = metrics;
        Cost = cost;
        SketchCost = sketchCost;
        Issues = issues;
        Curve = curve;
        CurveMetrics = curveMetrics;
        RequiredGrade = requiredGrade;
    }

    /// <summary>The route every half cell with its profile: on the ground where it can be, graded where it must.</summary>
    public double[] PointsXyz { get; }

    /// <summary>The ground under each point of <see cref="PointsXyz"/>.</summary>
    public double[] GroundZ { get; }

    public RouteMetrics Metrics { get; }

    /// <summary>The line priced by the route search's own cost function (<see cref="RouteAlternative.SearchCost"/>).</summary>
    public double Cost { get; }

    /// <summary>The sketch as drawn, priced the same way: what <see cref="Improvement"/> compares against.</summary>
    public double SketchCost { get; }

    /// <summary>How much cheaper than the sketch the route is, as a share: 0.4 is 40% cheaper.</summary>
    public double Improvement => SketchCost > 0.0 ? 1.0 - (Cost / SketchCost) : 0.0;

    public IReadOnlyList<RouteRelaxIssue> Issues { get; }

    /// <summary>The editable curve, when the evaluation was asked to fit one.</summary>
    public FittedRouteCurve? Curve { get; }

    /// <summary><see cref="Metrics"/> measured on <see cref="Curve"/>.</summary>
    public RouteMetrics? CurveMetrics { get; }

    /// <summary>The grade the route's ends need over its length: over the limit, the route is too short to climb between them.</summary>
    public double RequiredGrade { get; }
}
