namespace MoleHill.Core.Routing;

/// <summary>What <see cref="RouteSuggester"/> is asked to connect, and how it weighs the ways of doing it.</summary>
/// <remarks>
/// Lengths are in model units, like the height grid. The three weights are relative: doubling all of
/// them changes nothing. Distance prices every unit of plan length, Steepness prices grade up to the
/// limit (quadratically, so a route spends its grade where it must), and Earthworks prices ground the
/// route cannot follow without cut or fill — see <see cref="RouteSuggester"/> for the exact terms.
/// </remarks>
public sealed class RouteRequest
{
    /// <summary>Plan points in order: the start, any waypoints, the end. At least two.</summary>
    public required IReadOnlyList<(double X, double Y)> Points { get; init; }

    /// <summary>The longitudinal grade the returned profiles never exceed, as rise over run.</summary>
    public required double MaxGrade { get; init; }

    public double DistanceWeight { get; init; } = 1.0;

    public double SteepnessWeight { get; init; } = 1.0;

    public double EarthworksWeight { get; init; } = 1.0;

    /// <summary>
    /// Width the cut/fill estimate is taken over (a flat prism, no side slopes), and the band within
    /// which two routes count as the same corridor.
    /// </summary>
    public required double CorridorWidth { get; init; }

    /// <summary>
    /// Smallest plan radius the route's corners are rounded to, in model units; 0 keeps the corners
    /// sharp (a polyline). Waypoints stay exact, so a turn at a waypoint is not rounded.
    /// </summary>
    public double MinimumRadius { get; init; }

    /// <summary>How many distinct routes to return at most (1 to <see cref="RouteSuggester.MaxAlternatives"/>).</summary>
    public int AlternativeCount { get; init; } = 3;

    /// <summary>Vertical tolerance for thinning the fitted profile, in model units. 0 picks one from the cell size.</summary>
    public double ProfileTolerance { get; init; }

    /// <summary>
    /// How far, in plan, the editable curve (<see cref="RouteAlternative.Curve"/>) may stray from the exact
    /// route so that it needs fewer control points; model units. 0 picks one from the corridor width. Widened
    /// by <see cref="Simplicity"/> like the profile tolerance.
    /// </summary>
    public double CurveTolerance { get; init; }

    /// <summary>The plan tolerance the curve is fitted to: <see cref="CurveTolerance"/>, or 5% of the corridor width.</summary>
    internal double EffectiveCurveTolerance => (CurveTolerance > 0.0 ? CurveTolerance : 0.05 * CorridorWidth) * SimplicityScale;

    /// <summary>
    /// How much plainer a route may be at the price of earthwork; 0 (the default) is the route that follows
    /// the ground as closely as the other weights allow. At s, the turning radius the search prices turns
    /// at, the radius bends are rounded towards (never under <see cref="MinimumRadius"/>) and the profile's
    /// thinning tolerance are all (1 + s) times their base, so a route takes fewer, broader bends and
    /// longer grades. See <see cref="RouteComplexity"/> for how plainness is scored.
    /// </summary>
    public double Simplicity { get; init; }

    /// <summary>The factor <see cref="Simplicity"/> scales radii and tolerances by.</summary>
    internal double SimplicityScale => 1.0 + Math.Max(0.0, Simplicity);

    /// <summary>
    /// The radius a turn is priced at and a bend counts 2 at in <see cref="RouteComplexity"/>: the minimum
    /// radius, or the corridor width when none is asked for (a road that wide cannot turn on the spot).
    /// </summary>
    internal double ReferenceRadius => MinimumRadius > 0.0 ? MinimumRadius : CorridorWidth;
}
