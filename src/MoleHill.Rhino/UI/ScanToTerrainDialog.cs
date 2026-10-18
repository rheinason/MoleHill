using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Core.Scan;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>One command-scoped floating settings panel. Pure work runs asynchronously; stale previews
/// cannot become accepted output. Viewport resources and callbacks belong to this dialog's lifetime.</summary>
internal sealed class ScanToTerrainDialog : Dialog<bool>
{
    private readonly RhinoDoc _doc;
    private readonly ModelUnitContext _units;
    private readonly Func<ScanToTerrainSettings, CancellationToken, Action<string>, Action<ScanTerrainPipeline.Result>, Task<ScanToTerrainCommandService.Prepared>> _compute;
    private readonly ScanToTerrainPreview _preview;
    private readonly UITimer _timer = new() { Interval = .5 };
    private readonly Button _keep = new() { Text = "Keep", Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel" };
    private readonly Button _refresh = new() { Text = "Recompute" };
    private readonly Label _status = new() { Text = "Preparing preview…", Wrap = WrapMode.Word, Width = 460 };
    private readonly Dictionary<string, CheckBox> _visibility = new();
    private readonly Dictionary<string, NumericStepper> _numbers = new();
    private CancellationTokenSource? _cancellation;
    private ScanTerrainPipeline.Result? _pipeline;
    private bool _closed, _running, _dirty;
    private int _generation;
    private string? _inputError;

    public ScanToTerrainSettings Settings { get; }
    public ScanToTerrainCommandService.Prepared? Prepared { get; private set; }

    public ScanToTerrainDialog(RhinoDoc doc, ModelUnitContext units, string sourceName, string summary,
        ScanToTerrainSettings settings,
        Func<ScanToTerrainSettings, CancellationToken, Action<string>, Action<ScanTerrainPipeline.Result>, Task<ScanToTerrainCommandService.Prepared>> compute)
    {
        _doc = doc; _units = units; Settings = settings; _compute = compute;
        _preview = new ScanToTerrainPreview(doc.RuntimeSerialNumber);
        Title = "Scan to Terrain — " + sourceName;
        Resizable = true; Padding = 12; Size = new Size(660, 790); MinimumSize = new Size(540, 520);
        this.UseRhinoStyle();
        var body = new DynamicLayout { Spacing = new Size(8, 7), Padding = 4 };
        body.AddRow(new Label { Text = summary, Wrap = WrapMode.Word, Width = 460 });
        body.AddRow(new Label { Text = "Adjust settings to update the preview. Keep writes a fresh layer tree; Cancel discards it.", Wrap = WrapMode.Word, Width = 460 });
        string length = units.Abbreviation;
        var ground = Section(body, "Ground");
        Number(ground, "Raster cell (0 = automatic) · " + length, nameof(settings.RasterCell), 0, 1e12);
        Number(ground, "Ground spacing · " + length, nameof(settings.GroundSpacing));
        Number(ground, "Largest object width · " + length, nameof(settings.LargestObject), units.FromMeters(2));
        Number(ground, "Initial height allowance · " + length, nameof(settings.InitialThreshold));
        Number(ground, "Maximum height allowance · " + length, nameof(settings.MaximumThreshold));
        Number(ground, "Object margin · " + length, nameof(settings.ObjectMargin), 0);
        var slope = new TextBox { Text = SlopeInput.FormatWithUnit(settings.GroundSlope, SlopeUnitPreference.Current), Width = 140,
            ToolTip = SlopeInput.AcceptedFormatsHelp };
        void CommitSlope()
        {
            _inputError = null;
            if (SlopeInput.TryParse(slope.Text, SlopeUnitPreference.Current, out double value) && double.IsFinite(value) && value >= 0)
                Settings.GroundSlope = value;
            else _inputError = "Enter a valid non-negative ground slope, such as 60% or 1:3.";
            Schedule();
        }
        slope.LostFocus += (_, _) => CommitSlope();
        slope.KeyDown += (_, e) => { if (e.Key == Keys.Enter) { CommitSlope(); e.Handled = true; } };
        slope.TextChanged += (_, _) => { _keep.Enabled = false; };
        ground.AddRow(new Label { Text = "Ground slope allowance" }, slope);
        Output(ground, "Check mesh", nameof(settings.AddGroundMesh), false);
        var roofs = Section(body, "Buildings"); Output(roofs, "Roof solids", nameof(settings.Buildings));
        Number(roofs, "Minimum footprint · " + length + "²", nameof(settings.BuildingArea), units.FromSquareMeters(.25));
        Number(roofs, "Minimum height · " + length, nameof(settings.BuildingHeight));
        Number(roofs, "Roof fitting tolerance · " + length, nameof(settings.RoofTolerance));
        var walls = Section(body, "Walls and terrace edges"); Output(walls, "Top and toe breaklines", nameof(settings.Walls));
        Number(walls, "Minimum height · " + length, nameof(settings.WallHeight));
        Number(walls, "Minimum length · " + length, nameof(settings.WallLength));
        var areas = Section(body, "Paved and soft ground"); Output(areas, "Area boundaries and colour split", nameof(settings.Surfaces));
        Number(areas, "Excess green threshold", nameof(settings.GreenThreshold), 0, 2);
        Number(areas, "Maximum paving saturation", nameof(settings.PavingSaturation), 0, 1);
        var roads = Section(body, "Kerbs and road edges"); Output(roads, "Measured kerbs or single breaklines", nameof(settings.RoadEdges));
        Number(roads, "Minimum kerb height · " + length, nameof(settings.KerbMin));
        Number(roads, "Maximum kerb height · " + length, nameof(settings.KerbMax));
        var contours = Section(body, "Contours and spot heights"); Output(contours, "Contours and spots", nameof(settings.Contours));
        Number(contours, "Vertical tolerance · " + length, nameof(settings.Tolerance));
        Number(contours, "Plan tolerance · " + length, nameof(settings.PlanTolerance));
        Number(contours, "Contour budget", nameof(settings.MaxContours), 1, 100000, 0);
        var lines = new DropDown { DataStore = TerrainToCurvesCommandService.LandformLabels, SelectedIndex = Math.Clamp(settings.RidgesValleys, 0, 2) };
        lines.SelectedIndexChanged += (_, _) => { Settings.RidgesValleys = lines.SelectedIndex; Schedule(); };
        contours.AddRow(new Label { Text = "Ridges and valleys" }, lines);
        var linePreview = new CheckBox { Text = "Preview ridges and valleys", Checked = true };
        _visibility[nameof(settings.RidgesValleys)] = linePreview;
        linePreview.CheckedChanged += (_, _) => UpdatePreview(); contours.AddRow(linePreview);
        body.AddRow(new Label { Text = "Tolerance describes the inferred ground, not surveyed ground beneath removed objects. Roof fallbacks remain marked for review.", Wrap = WrapMode.Word, Width = 460 });
        var shell = new DynamicLayout { Spacing = new Size(8, 8) };
        shell.Add(new Scrollable { Content = body, ExpandContentWidth = true }, yscale: true);
        shell.AddRow(_status);
        shell.AddRow(new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8,
            Items = { _refresh, new StackLayoutItem(null, true), _cancel, _keep } });
        Content = shell;
        DefaultButton = _keep; AbortButton = _cancel;
        _keep.Click += (_, _) => { if (Prepared != null && !_dirty && !_running && _inputError == null) Close(true); };
        _cancel.Click += (_, _) => Close(false);
        _refresh.Click += (_, _) => Schedule();
        _timer.Elapsed += OnTick;
        Shown += (_, _) => { _preview.Enabled = true; RhinoApp.EscapeKeyPressed += OnEscape; Schedule(); _timer.Start(); };
        Closed += (_, _) =>
        {
            _closed = true; _generation++; _timer.Stop(); _timer.Elapsed -= OnTick; _timer.Dispose();
            _cancellation?.Cancel(); RhinoApp.EscapeKeyPressed -= OnEscape;
            _preview.Dispose(); _doc.Views.Redraw();
        };
    }

    private DynamicLayout Section(DynamicLayout body, string title)
    {
        var layout = new DynamicLayout { Spacing = new Size(8, 5), Padding = 8 };
        body.AddRow(new GroupBox { Text = title, Content = layout }); return layout;
    }

    private void Number(DynamicLayout layout, string label, string propertyName, double? minimum = null, double maximum = 1e12, int decimals = 3)
    {
        var property = typeof(ScanToTerrainSettings).GetProperty(propertyName)!;
        double value = Convert.ToDouble(property.GetValue(Settings));
        double min = minimum ?? _doc.ModelAbsoluteTolerance;
        var number = new NumericStepper { DecimalPlaces = decimals, MinValue = min, MaxValue = maximum,
            Value = Math.Clamp(value, min, maximum), Width = 140,
            Increment = decimals == 0 ? 1 : propertyName is nameof(ScanToTerrainSettings.GreenThreshold) or nameof(ScanToTerrainSettings.PavingSaturation)
                ? .01 : propertyName == nameof(ScanToTerrainSettings.BuildingArea) ? _units.FromSquareMeters(1) : Math.Max(min, _units.FromMeters(.05)) };
        if (property.PropertyType == typeof(int)) property.SetValue(Settings, (int)number.Value);
        else property.SetValue(Settings, number.Value);
        number.ValueChanged += (_, _) =>
        {
            if (property.PropertyType == typeof(int)) property.SetValue(Settings, (int)number.Value);
            else property.SetValue(Settings, number.Value);
            Schedule();
        };
        _numbers[propertyName] = number;
        layout.AddRow(new Label { Text = label, VerticalAlignment = VerticalAlignment.Center }, number);
    }

    private void Output(DynamicLayout layout, string title, string propertyName, bool preview = true)
    {
        var property = typeof(ScanToTerrainSettings).GetProperty(propertyName)!;
        var enabled = new CheckBox { Text = title, Checked = (bool)property.GetValue(Settings)! };
        var visible = new CheckBox { Text = "Preview", Checked = preview };
        enabled.CheckedChanged += (_, _) => { property.SetValue(Settings, enabled.Checked == true); Schedule(); };
        visible.CheckedChanged += (_, _) => UpdatePreview();
        _visibility[propertyName] = visible;
        layout.AddRow(enabled, visible);
    }

    private void OnEscape(object? sender, EventArgs args) => Close(false);
    private void Schedule()
    {
        _generation++; _dirty = true; Prepared = null; _keep.Enabled = false;
        _cancellation?.Cancel();
        _status.Text = _inputError ?? "Settings changed — updating preview…";
    }

    private async void OnTick(object? sender, EventArgs args)
    {
        if (!_dirty || _running || _closed || _inputError != null) return;
        if (Settings.KerbMax < Settings.KerbMin || Settings.MaximumThreshold < Settings.InitialThreshold)
        { _status.Text = "A maximum height must be at least its minimum height."; return; }
        _running = true; _dirty = false; _refresh.Enabled = false;
        int generation = _generation;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        void Dispatch(Action action) => Application.Instance.AsyncInvoke(() => { if (!_closed && generation == _generation) action(); });
        try
        {
            _pipeline = null; UpdatePreview();
            var result = await _compute(Settings with { }, cancellation.Token,
                text => Dispatch(() => _status.Text = text),
                pipeline => Dispatch(() => { _pipeline = pipeline; UpdatePreview(); }));
            if (_closed || generation != _generation || cancellation.IsCancellationRequested) return;
            Prepared = result; _pipeline = result.Pipeline; UpdatePreview(); _keep.Enabled = true;
            var ground = result.Pipeline.Ground;
            _status.Text = $"Ready: {Count(result.Pipeline.Buildings?.Buildings.Count ?? 0, "building")} " +
                $"({Count(result.Pipeline.Buildings?.Buildings.Count(b => b.IsFallback) ?? 0, "fallback")}), {Count(result.Pipeline.Walls.Count, "wall")}; " +
                $"{Count(result.Recovery.Contours.Count, "contour")}, {Count(result.Recovery.SpotPointCount, "spot")}. " + result.Recovery.Diagnostic +
                $" Filled {ground.FilledShare:P0}." + (ground.ClassifiedGroundNodes == 0 ? "" : $" Supplied ground: {ground.ClassifiedGroundNodes:N0} cells; {ground.ClassifiedGroundDisagreements:N0} filter disagreements.");
        }
        catch (OperationCanceledException) { if (!_closed && generation == _generation) _status.Text = "Preview cancelled."; }
        catch (Exception error) { if (!_closed && generation == _generation) { _status.Text = error.Message; Prepared = null; _keep.Enabled = false; } }
        finally
        {
            _running = false; if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            if (!_closed) _refresh.Enabled = true;
        }
    }

    private void UpdatePreview()
    {
        if (_closed) return;
        _preview.Update(_pipeline, Prepared?.Recovery, _visibility.Where(pair => pair.Value.Checked == true).Select(pair => pair.Key).ToHashSet());
        _doc.Views.Redraw();
    }

    private static string Count(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}";
}
