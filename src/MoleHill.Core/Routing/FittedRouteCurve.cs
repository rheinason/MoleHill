namespace MoleHill.Core.Routing;

/// <summary>
/// A route as a designer would draw it: a few cubic pieces with only as many control points as the
/// route needs to stay within tolerance of the line it was fitted to, joined end to end. Pieces meet
/// at waypoints and at corners left sharp (a cubic cannot turn on the spot), and are smooth everywhere
/// else. See <see cref="RouteCurveFitter"/>.
/// </summary>
public sealed class FittedRouteCurve
{
    internal FittedRouteCurve(
        IReadOnlyList<RouteSplineCurve> pieces,
        double[] pointsXyz,
        double maxPlanDeviation,
        double maxVerticalDeviation,
        double maxGrade,
        double? smallestRadius,
        RouteComplexity complexity)
    {
        Pieces = pieces;
        PointsXyz = pointsXyz;
        MaxPlanDeviation = maxPlanDeviation;
        MaxVerticalDeviation = maxVerticalDeviation;
        MaxGrade = maxGrade;
        SmallestRadius = smallestRadius;
        Complexity = complexity;
    }

    /// <summary>The cubic pieces, start to end; each starts where the previous one ends.</summary>
    public IReadOnlyList<RouteSplineCurve> Pieces { get; }

    /// <summary>Points sampled along the pieces, for previewing and measuring.</summary>
    public double[] PointsXyz { get; }

    /// <summary>Control points of the joined curve: a joint is one point shared by the pieces either side.</summary>
    public int ControlPointCount => Pieces.Count == 0 ? 0 : Pieces.Sum(piece => piece.ControlPointCount) - (Pieces.Count - 1);

    /// <summary>The farthest the curve strays in plan from the line it was fitted to.</summary>
    public double MaxPlanDeviation { get; }

    /// <summary>The farthest the curve strays in elevation from the line it was fitted to.</summary>
    public double MaxVerticalDeviation { get; }

    /// <summary>The steepest grade along the curve, measured finely.</summary>
    public double MaxGrade { get; }

    /// <summary>The tightest plan radius along the curve's smooth stretches; null when it never bends.</summary>
    public double? SmallestRadius { get; }

    public RouteComplexity Complexity { get; }
}
