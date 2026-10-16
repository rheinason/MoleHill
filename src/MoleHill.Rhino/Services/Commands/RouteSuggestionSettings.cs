using MoleHill.Core.Routing;
using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The mhSuggestRoute options other than the grade limit (which is a <see cref="SlopeCommandOption"/>),
/// remembered between runs through <see cref="CommandOptionCache"/>. Lengths are held in model units and
/// cached in metres, so a value chosen in a metre model reads correctly when the next model is in
/// millimetres.
/// </summary>
internal sealed class RouteSuggestionSettings
{
    /// <summary>1:12.5. Steeper than an accessible ramp, gentle for a road; a starting point, not a rule.</summary>
    public const double DefaultMaxGrade = 0.08;

    public const double DefaultCorridorWidthMeters = 4.0;

    /// <summary>
    /// 10 m: comfortable on a footpath or a cycle path, and the tight end of what a slow access road
    /// takes. A road designer raises it; 0 keeps sharp corners.
    /// </summary>
    public const double DefaultMinimumRadiusMeters = 10.0;

    /// <summary>
    /// How far the route's profile may leave the grade-limited fit to the ground when it is thinned, in
    /// metres. It used to be 1% of the search cell — millimetres — so the profile kept a vertex at every
    /// ripple of the ground, half a metre apart. A tenth of a metre of extra cut or fill gives grade breaks
    /// a designer would draw, and the thinned profile still obeys the grade (its chords average the fit).
    /// </summary>
    public const double ProfileToleranceMeters = 0.1;

    public const double MaxWeight = 100.0;

    /// <summary>
    /// The default plainness (<see cref="RouteRequest.Simplicity"/>). At 0 a route follows every fold of
    /// the ground it can; on a 10% climb in the reporting file that gave 15–17 on the complexity score
    /// against 3–4 for a hand-drawn road. At 2 the alternatives span both: one still close to the ground,
    /// one as plain as the hand-drawn line, so the trade is on screen to choose from.
    /// </summary>
    public const double DefaultSimplicity = 2.0;

    public const double MaxSimplicity = 10.0;

    public const string MaxGradeCacheKey = "MoleHill.SuggestRoute.MaxGrade";
    private const string DistanceKey = "MoleHill.SuggestRoute.DistanceWeight";
    private const string SteepnessKey = "MoleHill.SuggestRoute.SteepnessWeight";
    private const string EarthworksKey = "MoleHill.SuggestRoute.EarthworksWeight";
    private const string CorridorKey = "MoleHill.SuggestRoute.CorridorWidth";
    private const string CellKey = "MoleHill.SuggestRoute.CellSize";
    private const string AlternativesKey = "MoleHill.SuggestRoute.Alternatives";
    private const string RadiusKey = "MoleHill.SuggestRoute.MinRadius";
    private const string SimplicityKey = "MoleHill.SuggestRoute.Simplicity";

    public double DistanceWeight { get; set; } = 1.0;

    public double SteepnessWeight { get; set; } = 1.0;

    public double EarthworksWeight { get; set; } = 1.0;

    /// <summary>Model units.</summary>
    public double CorridorWidth { get; set; }

    /// <summary>Model units; 0 lets the command choose from the size of the job.</summary>
    public double CellSize { get; set; }

    public int AlternativeCount { get; set; } = RouteSuggester.MaxAlternatives;

    /// <summary>Model units; 0 keeps the corners sharp.</summary>
    public double MinimumRadius { get; set; }

    /// <summary>How much plainer a route may be for more earthwork (<see cref="RouteRequest.Simplicity"/>).</summary>
    public double Simplicity { get; set; } = DefaultSimplicity;

    public bool IsAutomaticCellSize => !(CellSize > 0.0);

    public static RouteSuggestionSettings Load(ModelUnitContext units) => new()
    {
        DistanceWeight = CommandOptionCache.GetValue(DistanceKey, 1.0),
        SteepnessWeight = CommandOptionCache.GetValue(SteepnessKey, 1.0),
        EarthworksWeight = CommandOptionCache.GetValue(EarthworksKey, 1.0),
        CorridorWidth = CommandOptionCache.GetLength(CorridorKey, units, DefaultCorridorWidthMeters),
        CellSize = CommandOptionCache.GetLength(CellKey, units, 0.0),
        AlternativeCount = CommandOptionCache.GetValue(AlternativesKey, RouteSuggester.MaxAlternatives),
        MinimumRadius = CommandOptionCache.GetLength(RadiusKey, units, DefaultMinimumRadiusMeters),
        Simplicity = CommandOptionCache.GetValue(SimplicityKey, DefaultSimplicity),
    };

    public void Save(ModelUnitContext units)
    {
        CommandOptionCache.SetValue(DistanceKey, DistanceWeight);
        CommandOptionCache.SetValue(SteepnessKey, SteepnessWeight);
        CommandOptionCache.SetValue(EarthworksKey, EarthworksWeight);
        CommandOptionCache.SetLength(CorridorKey, units, CorridorWidth);
        CommandOptionCache.SetLength(CellKey, units, Math.Max(0.0, CellSize));
        CommandOptionCache.SetValue(AlternativesKey, AlternativeCount);
        CommandOptionCache.SetLength(RadiusKey, units, Math.Max(0.0, MinimumRadius));
        CommandOptionCache.SetValue(SimplicityKey, Math.Clamp(Simplicity, 0.0, MaxSimplicity));
    }

    /// <summary>Why these settings cannot be searched with, or null when they can.</summary>
    public string? Validate(double maxGrade)
    {
        if (!(maxGrade > 0.0))
            return "MaxGrade must be steeper than level: a route has to be allowed to climb.";
        if (DistanceWeight + SteepnessWeight + EarthworksWeight <= 0.0)
            return "At least one of Distance, Steepness and Earthworks must be weighted above zero.";
        if (!(CorridorWidth > 0.0))
            return "CorridorWidth must be greater than zero.";
        return null;
    }

    public RouteRequest ToRequest(IReadOnlyList<(double X, double Y)> points, double maxGrade, double tolerance) => new()
    {
        Points = points,
        MaxGrade = maxGrade,
        DistanceWeight = DistanceWeight,
        SteepnessWeight = SteepnessWeight,
        EarthworksWeight = EarthworksWeight,
        CorridorWidth = CorridorWidth,
        AlternativeCount = Math.Clamp(AlternativeCount, 1, RouteSuggester.MaxAlternatives),
        MinimumRadius = Math.Max(0.0, MinimumRadius),
        ProfileTolerance = tolerance,
        Simplicity = Math.Clamp(Simplicity, 0.0, MaxSimplicity),
    };

    /// <summary>
    /// The cell size to search at over a domain, and whether a requested size had to be coarsened to
    /// keep the grid within <see cref="RouteHeightGrid.MaximumNodeCount"/>.
    /// </summary>
    public (double CellSize, bool Coarsened) ResolveCellSize(double domainWidth, double domainHeight, double routeSpan)
    {
        if (IsAutomaticCellSize)
            return (RouteHeightGrid.AutomaticCellSize(domainWidth, domainHeight, routeSpan), false);

        double floor = RouteHeightGrid.MinimumCellSize(domainWidth, domainHeight);
        return CellSize < floor ? (floor, true) : (CellSize, false);
    }
}
