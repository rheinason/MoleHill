using MoleHill.Core.Routing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The route commands' command-line options, offered identically at every prompt so a setting can be changed
/// at any stage. mhSuggestRoute: <c>MaxGrade Units MinRadius Simplicity Distance Steepness Earthworks
/// CorridorWidth CellSize Alternatives Curve Layer</c>; mhRelaxRoute swaps Alternatives and Curve for
/// Fidelity (its curve is always the fitted one).
/// </summary>
internal sealed class RouteOptionSet
{
    private readonly RhinoDoc _doc;
    private OptionDouble _minimumRadius = new(0.0);
    private OptionDouble _simplicity = new(0.0);
    private OptionDouble _distance = new(1.0);
    private OptionDouble _steepness = new(1.0);
    private OptionDouble _earthworks = new(1.0);
    private OptionDouble _corridorWidth = new(1.0);
    private OptionDouble _cellSize = new(0.0);
    private OptionInteger _alternatives = new(RouteSuggester.MaxAlternatives);
    private OptionToggle _smoothCurve = new(true, "Exact", "Smooth");
    private OptionDouble _fidelity = new(RouteSuggestionSettings.DefaultFidelity);
    private readonly bool _relax;
    private int _layerOption = -1;
    private int _currentLayerOption = -1;

    public RouteOptionSet(RhinoDoc doc, RouteSuggestionSettings settings, SlopeCommandOption maxGrade, bool relax = false)
    {
        _doc = doc;
        _relax = relax;
        Settings = settings;
        MaxGrade = maxGrade;
        LayerId = RouteOutputLayer.Resolve(RouteOutputLayer.Remembered(doc.RuntimeSerialNumber), LayerExists, out bool fellBack);
        if (fellBack)
        {
            RouteOutputLayer.Remember(doc.RuntimeSerialNumber, Guid.Empty);
            RhinoApp.WriteLine("The layer chosen for suggested routes no longer exists; routes go on the current layer.");
        }
    }

    public RouteSuggestionSettings Settings { get; }

    public SlopeCommandOption MaxGrade { get; }

    /// <summary>The chosen output layer, or <see cref="Guid.Empty"/> for the current layer.</summary>
    public Guid LayerId { get; private set; }

    /// <summary>Getters are rebuilt every pass, so the options are re-created from the settings each time.</summary>
    public void AddTo(GetBaseClass get)
    {
        MaxGrade.AddTo(get);
        _minimumRadius = new OptionDouble(Math.Max(0.0, Settings.MinimumRadius), 0.0, 1e12);
        _simplicity = new OptionDouble(Math.Clamp(Settings.Simplicity, 0.0, RouteSuggestionSettings.MaxSimplicity), 0.0, RouteSuggestionSettings.MaxSimplicity);
        _distance = new OptionDouble(Settings.DistanceWeight, 0.0, RouteSuggestionSettings.MaxWeight);
        _steepness = new OptionDouble(Settings.SteepnessWeight, 0.0, RouteSuggestionSettings.MaxWeight);
        _earthworks = new OptionDouble(Settings.EarthworksWeight, 0.0, RouteSuggestionSettings.MaxWeight);
        _corridorWidth = new OptionDouble(Settings.CorridorWidth, 1e-9, 1e12);
        _cellSize = new OptionDouble(Math.Max(0.0, Settings.CellSize), 0.0, 1e12);
        _alternatives = new OptionInteger(Settings.AlternativeCount, 1, RouteSuggester.MaxAlternatives);
        get.AddOptionDouble("MinRadius", ref _minimumRadius, "Smallest plan radius at corners; 0 keeps sharp corners");
        get.AddOptionDouble("Simplicity", ref _simplicity, "Fewer, broader bends and longer grades for more earthwork; 0 follows the ground");
        get.AddOptionDouble("Distance", ref _distance, "Weight on route length");
        get.AddOptionDouble("Steepness", ref _steepness, "Weight on grade, up to MaxGrade");
        get.AddOptionDouble("Earthworks", ref _earthworks, "Weight on ground the route cannot follow");
        if (_relax)
        {
            _fidelity = new OptionDouble(Math.Clamp(Settings.Fidelity, 0.0, RouteSuggestionSettings.MaxFidelity), 0.0, RouteSuggestionSettings.MaxFidelity);
            get.AddOptionDouble("Fidelity", ref _fidelity, "How firmly the route keeps to the sketch; 0 lets it go");
        }

        get.AddOptionDouble("CorridorWidth", ref _corridorWidth);
        get.AddOptionDouble("CellSize", ref _cellSize, "Ground sample spacing; 0 is automatic");
        if (!_relax)
        {
            get.AddOptionInteger("Alternatives", ref _alternatives);
            _smoothCurve = new OptionToggle(Settings.SmoothCurve, "Exact", "Smooth");
            get.AddOptionToggle("Curve", ref _smoothCurve);
        }

        Layer? layer = LayerId == Guid.Empty ? null : _doc.Layers.FindId(LayerId);
        _layerOption = get.AddOption("Layer", RouteOutputLayer.OptionValue(layer?.Name));
        _currentLayerOption = layer != null ? get.AddOption("UseCurrentLayer") : -1;
    }

    /// <summary>
    /// Reads the options back. True when anything the search depends on changed; a change of
    /// <see cref="RouteSuggestionSettings.SmoothCurve"/> only changes which curve is shown and kept.
    /// </summary>
    public bool Commit(GetBaseClass get, GetResult result)
    {
        if (result == GetResult.Option)
        {
            int index = get.OptionIndex();
            if (index == _layerOption)
            {
                ChooseLayer();
                return false;
            }

            if (index == _currentLayerOption)
            {
                SetLayer(Guid.Empty);
                return false;
            }
        }

        double gradeBefore = MaxGrade.Ratio;
        MaxGrade.Commit(get, result);

        var before = (Settings.DistanceWeight, Settings.SteepnessWeight, Settings.EarthworksWeight,
            Settings.CorridorWidth, Settings.CellSize, Settings.AlternativeCount, Settings.MinimumRadius, Settings.Simplicity, Settings.Fidelity);
        Settings.MinimumRadius = _minimumRadius.CurrentValue;
        Settings.Simplicity = _simplicity.CurrentValue;
        Settings.DistanceWeight = _distance.CurrentValue;
        Settings.SteepnessWeight = _steepness.CurrentValue;
        Settings.EarthworksWeight = _earthworks.CurrentValue;
        Settings.CorridorWidth = _corridorWidth.CurrentValue;
        Settings.CellSize = _cellSize.CurrentValue;
        if (_relax)
        {
            Settings.Fidelity = _fidelity.CurrentValue;
        }
        else
        {
            Settings.AlternativeCount = _alternatives.CurrentValue;
            Settings.SmoothCurve = _smoothCurve.CurrentValue;
        }

        var after = (Settings.DistanceWeight, Settings.SteepnessWeight, Settings.EarthworksWeight,
            Settings.CorridorWidth, Settings.CellSize, Settings.AlternativeCount, Settings.MinimumRadius, Settings.Simplicity, Settings.Fidelity);

        return before != after || gradeBefore != MaxGrade.Ratio;
    }

    /// <summary>The output layer's index right now: the chosen layer while it exists, else the current one.</summary>
    public int ResolveLayerIndex()
    {
        Guid id = RouteOutputLayer.Resolve(LayerId, LayerExists, out bool fellBack);
        if (fellBack)
        {
            SetLayer(Guid.Empty);
            RhinoApp.WriteLine("The layer chosen for suggested routes no longer exists; routes go on the current layer.");
        }

        return id == Guid.Empty ? _doc.Layers.CurrentLayerIndex : _doc.Layers.FindId(id).Index;
    }

    /// <summary>The output layer's full path, or "Current layer".</summary>
    public string LayerName =>
        LayerId != Guid.Empty && _doc.Layers.FindId(LayerId) is { IsDeleted: false } layer ? layer.FullPath : "Current layer";

    /// <summary>Opens Rhino's layer dialog and makes the picked layer the output layer.</summary>
    public void ChooseLayer()
    {
        int index = LayerId == Guid.Empty ? _doc.Layers.CurrentLayerIndex : (_doc.Layers.FindId(LayerId)?.Index ?? _doc.Layers.CurrentLayerIndex);
        bool setCurrent = false;
        if (!global::Rhino.UI.Dialogs.ShowSelectLayerDialog(ref index, "Layer for suggested routes", true, false, ref setCurrent))
            return;

        Layer? layer = index >= 0 && index < _doc.Layers.Count ? _doc.Layers[index] : null;
        if (layer == null || layer.IsDeleted)
            return;

        SetLayer(layer.Id);
        RhinoApp.WriteLine($"Suggested routes go on layer {layer.FullPath}.");
    }

    private void SetLayer(Guid id)
    {
        LayerId = id;
        RouteOutputLayer.Remember(_doc.RuntimeSerialNumber, id);
    }

    private bool LayerExists(Guid id) => _doc.Layers.FindId(id) is { IsDeleted: false };
}
