namespace MoleHill.Core.Routing;

/// <summary>
/// The cost per unit plan length of travelling at a given ground grade — the one function the grid
/// search and the plan straightening both price by, so straightening can never trade a route for one
/// the search would have rejected.
/// </summary>
internal readonly struct RouteCost
{
    private readonly double _maxGrade;
    private readonly double _distance;
    private readonly double _steepness;
    private readonly double _earthworks;

    public RouteCost(RouteRequest request)
    {
        _maxGrade = request.MaxGrade;
        _distance = request.DistanceWeight;
        _steepness = request.SteepnessWeight;
        _earthworks = request.EarthworksWeight;
    }

    /// <summary>The smallest cost per unit length any move can have — what keeps the A* heuristic admissible.</summary>
    public double DistanceWeight => _distance;

    /// <summary>
    /// <c>wDistance + wSteepness * min(r, 1)^2 + wEarthworks * E(r)</c>, with r the share of the limit
    /// the ground grade uses and E(r) = 2x + 4x^2 for the excess x = r - 1 (0 within the limit).
    /// </summary>
    public double Factor(double grade)
    {
        double ratio = grade / _maxGrade;
        double used = Math.Min(ratio, 1.0);
        double factor = _distance + (_steepness * used * used);
        if (ratio > 1.0)
        {
            double excess = ratio - 1.0;
            factor += _earthworks * ((2.0 * excess) + (4.0 * excess * excess));
        }

        return factor;
    }
}
