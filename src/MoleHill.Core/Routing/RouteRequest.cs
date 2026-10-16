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

    /// <summary>How many distinct routes to return at most (1 to <see cref="RouteSuggester.MaxAlternatives"/>).</summary>
    public int AlternativeCount { get; init; } = 3;

    /// <summary>Vertical tolerance for thinning the fitted profile, in model units. 0 picks one from the cell size.</summary>
    public double ProfileTolerance { get; init; }
}
