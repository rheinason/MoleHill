using MoleHill.Core.Routing;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// mhSuggestRoute's command-line options, offered identically while points are picked and while the
/// alternatives are reviewed, so a setting can be changed at either stage and the routes searched again:
/// <c>MaxGrade Units Distance Steepness Earthworks CorridorWidth CellSize Alternatives</c>.
/// </summary>
internal sealed class RouteOptionSet
{
    private OptionDouble _distance = new(1.0);
    private OptionDouble _steepness = new(1.0);
    private OptionDouble _earthworks = new(1.0);
    private OptionDouble _corridorWidth = new(1.0);
    private OptionDouble _cellSize = new(0.0);
    private OptionInteger _alternatives = new(RouteSuggester.MaxAlternatives);

    public RouteOptionSet(RouteSuggestionSettings settings, SlopeCommandOption maxGrade)
    {
        Settings = settings;
        MaxGrade = maxGrade;
    }

    public RouteSuggestionSettings Settings { get; }

    public SlopeCommandOption MaxGrade { get; }

    /// <summary>Getters are rebuilt every pass, so the options are re-created from the settings each time.</summary>
    public void AddTo(GetBaseClass get)
    {
        MaxGrade.AddTo(get);
        _distance = new OptionDouble(Settings.DistanceWeight, 0.0, RouteSuggestionSettings.MaxWeight);
        _steepness = new OptionDouble(Settings.SteepnessWeight, 0.0, RouteSuggestionSettings.MaxWeight);
        _earthworks = new OptionDouble(Settings.EarthworksWeight, 0.0, RouteSuggestionSettings.MaxWeight);
        _corridorWidth = new OptionDouble(Settings.CorridorWidth, 1e-9, 1e12);
        _cellSize = new OptionDouble(Math.Max(0.0, Settings.CellSize), 0.0, 1e12);
        _alternatives = new OptionInteger(Settings.AlternativeCount, 1, RouteSuggester.MaxAlternatives);
        get.AddOptionDouble("Distance", ref _distance, "Weight on route length");
        get.AddOptionDouble("Steepness", ref _steepness, "Weight on grade, up to MaxGrade");
        get.AddOptionDouble("Earthworks", ref _earthworks, "Weight on ground the route cannot follow");
        get.AddOptionDouble("CorridorWidth", ref _corridorWidth);
        get.AddOptionDouble("CellSize", ref _cellSize, "Ground sample spacing; 0 is automatic");
        get.AddOptionInteger("Alternatives", ref _alternatives);
    }

    /// <summary>Reads the options back. True when anything the search depends on changed.</summary>
    public bool Commit(GetBaseClass get, GetResult result)
    {
        double gradeBefore = MaxGrade.Ratio;
        MaxGrade.Commit(get, result);

        var before = (Settings.DistanceWeight, Settings.SteepnessWeight, Settings.EarthworksWeight,
            Settings.CorridorWidth, Settings.CellSize, Settings.AlternativeCount);
        Settings.DistanceWeight = _distance.CurrentValue;
        Settings.SteepnessWeight = _steepness.CurrentValue;
        Settings.EarthworksWeight = _earthworks.CurrentValue;
        Settings.CorridorWidth = _corridorWidth.CurrentValue;
        Settings.CellSize = _cellSize.CurrentValue;
        Settings.AlternativeCount = _alternatives.CurrentValue;
        var after = (Settings.DistanceWeight, Settings.SteepnessWeight, Settings.EarthworksWeight,
            Settings.CorridorWidth, Settings.CellSize, Settings.AlternativeCount);

        return before != after || gradeBefore != MaxGrade.Ratio;
    }
}
