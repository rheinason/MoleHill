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
internal sealed partial class TerrainCurveExtractionDialog : Dialog<bool>
{
    private readonly RhinoDoc _doc;
    private readonly ModelUnitContext _units;
    private readonly Func<TerrainCurveExtractionSettings, CancellationToken, Action<string>, Action<TerrainCurveExtractionPrepared>, Task<TerrainCurveExtractionPrepared>> _compute;
    private readonly TerrainCurveExtractionPreview _preview;
    private readonly UITimer _timer = new() { Interval = .5 };
    private readonly Button _keep = new() { Text = "Keep", Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel" };
    private readonly Button _refresh = new() { Text = "Recompute" };
    private readonly Label _status = new() { Text = "Preparing preview…", Wrap = WrapMode.Word, Width = 460 };
    private readonly Dictionary<string, CheckBox> _visibility = new();
    private readonly Dictionary<string, NumericStepper> _numbers = new();
    private CancellationTokenSource? _cancellation;
    private TerrainCurveExtractionPrepared? _previewState;
    private bool _closed, _running, _dirty;
    private int _generation;
    private string? _inputError;

    public TerrainCurveExtractionSettings Settings { get; }
    public TerrainCurveExtractionPrepared? Prepared { get; private set; }

    public TerrainCurveExtractionDialog(RhinoDoc doc, ModelUnitContext units, string title, string sourceName, string summary,
        TerrainCurveExtractionSettings settings,
        Func<TerrainCurveExtractionSettings, CancellationToken, Action<string>, Action<TerrainCurveExtractionPrepared>, Task<TerrainCurveExtractionPrepared>> compute,
        Guid? sourceId = null)
    {
        _doc = doc; _units = units; Settings = settings; _compute = compute;
        _preview = new TerrainCurveExtractionPreview(doc.RuntimeSerialNumber, sourceId);
        Title = title + " — " + sourceName;
        Resizable = true; Padding = 12; Size = new Size(660, 790); MinimumSize = new Size(540, 520);
        this.UseRhinoStyle();
        var body = new DynamicLayout { Spacing = new Size(8, 7), Padding = 4 };
        body.AddRow(new Label { Text = summary, Wrap = WrapMode.Word, Width = 460 });
        body.AddRow(new Label { Text = "Adjust settings to update the preview. Keep writes a fresh layer tree; Cancel discards it.", Wrap = WrapMode.Word, Width = 460 });
        if (sourceId.HasValue)
        {
            var showSource = new CheckBox { Text = "Show source", Checked = false };
            showSource.CheckedChanged += (_, _) => { _preview.ShowSource = showSource.Checked == true; _doc.Views.Redraw(); };
            body.AddRow(showSource);
        }
        string length = units.Abbreviation;
        bool scan = BuildPreparationSections(body);
        Size = new Size(660, scan ? 790 : 460); MinimumSize = new Size(540, scan ? 520 : 360);
        if (!scan)
        {
            var referencePreview = new CheckBox { Text = "Preview reference mesh", Checked = false };
            _visibility["Reference"] = referencePreview;
            referencePreview.CheckedChanged += (_, _) => UpdatePreview(); body.AddRow(referencePreview);
        }
        var contours = Section(body, "Horizontal contours and spot heights"); Output(contours, "Contours and spots", nameof(settings.Contours));
        if (scan) contours.AddRow(new Label { Text = "Dark green: horizontal contours, one elevation per curve. Muted area outlines carry ground height.", Wrap = WrapMode.Word, Width = 460 });
        Number(contours, "Vertical tolerance · " + length, nameof(settings.Tolerance));
        Number(contours, "Plan tolerance · " + length, nameof(settings.PlanTolerance));
        Number(contours, "Contour budget", nameof(settings.MaxContours), 1, 100000, 0);
        var lines = new DropDown { DataStore = TerrainCurveExtractionSettings.LandformLabels, SelectedIndex = Math.Clamp(settings.RidgesValleys, 0, 2) };
        lines.SelectedIndexChanged += (_, _) => { Settings.RidgesValleys = lines.SelectedIndex; Schedule(); };
        contours.AddRow(new Label { Text = "Ridges and valleys" }, lines);
        var linePreview = new CheckBox { Text = "Preview ridges and valleys", Checked = true };
        _visibility[nameof(settings.RidgesValleys)] = linePreview;
        linePreview.CheckedChanged += (_, _) => UpdatePreview(); contours.AddRow(linePreview);
        body.AddRow(new Label { Text = scan ? "Tolerance describes the inferred ground, not surveyed ground beneath removed objects. Roof fallbacks remain marked for review." : "Tolerance is measured at the reference terrain vertices. A spent curve budget reports the remaining error.", Wrap = WrapMode.Word, Width = 460 });
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

    private void Number(DynamicLayout layout, string label, string propertyName, double? minimum = null, double maximum = 1e12, int decimals = 3, double? increment = null)
    {
        var property = Settings.GetType().GetProperty(propertyName)!;
        double value = Convert.ToDouble(property.GetValue(Settings));
        double min = minimum ?? _doc.ModelAbsoluteTolerance;
        var number = new NumericStepper { DecimalPlaces = decimals, MinValue = min, MaxValue = maximum,
            Value = Math.Clamp(value, min, maximum), Width = 140,
            Increment = increment ?? (decimals == 0 ? 1 : Math.Max(min, _units.FromMeters(.05))) };
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
        var property = Settings.GetType().GetProperty(propertyName)!;
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
        if (PreparationError() is string inputError) { _status.Text = inputError; return; }
        _running = true; _dirty = false; _refresh.Enabled = false;
        int generation = _generation;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        void Dispatch(Action action) => Application.Instance.AsyncInvoke(() => { if (!_closed && generation == _generation) action(); });
        try
        {
            _previewState = null; UpdatePreview();
            var result = await _compute(Settings with { }, cancellation.Token,
                text => Dispatch(() => _status.Text = text),
                state => Dispatch(() => { _previewState = state; UpdatePreview(); }));
            if (_closed || generation != _generation || cancellation.IsCancellationRequested) return;
            Prepared = result; _previewState = result; UpdatePreview(); _keep.Enabled = true;
            var recovery = result.Recovery!;
            _status.Text = "Ready: " + PreparationSummary(result) +
                $"{Count(recovery.Contours.Count, "contour")}, {Count(recovery.SpotPointCount, "spot")}. " + recovery.Diagnostic;
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
        _preview.Update(_previewState, _visibility.Where(pair => pair.Value.Checked == true).Select(pair => pair.Key).ToHashSet());
        _doc.Views.Redraw();
    }

    private static string Count(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}";
}
