using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Core.Scan;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>One command-scoped floating settings panel. Pure work runs asynchronously; stale previews
/// cannot become accepted output. Viewport resources and callbacks belong to this panel's lifetime.
/// <para>The panel is modeless and the command waits in a command-line get (<see cref="ShowAndWait"/>), so the
/// views stay free to pan, zoom and rotate around the preview; a semi-modal dialog locked them. The panel's
/// buttons reach the get as custom messages, the way Relax Route's do.</para></summary>
internal sealed partial class TerrainCurveExtractionPanel : Form
{
    private enum Request { Keep, Cancel, Refresh }

    private readonly RhinoDoc _doc;
    private readonly ModelUnitContext _units;
    private readonly Func<TerrainCurveExtractionSettings, CancellationToken, Action<string>, Action<TerrainCurveExtractionPrepared>, Task<TerrainCurveExtractionPrepared>> _compute;
    private readonly TerrainCurveExtractionPreview _preview;
    private readonly UITimer _timer = new() { Interval = .5 };
    private readonly Button _keep = new() { Text = "Keep", Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel" };
    private readonly Button _refresh;
    private readonly Label _summary;
    private readonly Label _status = new() { Text = "Preparing preview…", Wrap = WrapMode.Word, Width = 460 };
    private readonly Dictionary<string, CheckBox> _visibility = new();
    private readonly Dictionary<string, NumericStepper> _numbers = new();
    private readonly Dictionary<DynamicLayout, DynamicLayout> _noteHosts = new();
    private readonly Task<string>? _loading;
    private readonly string _prompt;
    private CancellationTokenSource? _cancellation;
    private TerrainCurveExtractionPrepared? _previewState;
    private readonly bool _manual;
    private bool _closed, _ending, _running, _dirty, _loaded;
    private int _generation;
    private string? _inputError;

    public TerrainCurveExtractionSettings Settings { get; }
    public TerrainCurveExtractionPrepared? Prepared { get; private set; }

    /// <param name="loading">Work the first run needs, still in progress when the panel opens (reading a scan and its
    /// texture took seconds of frozen Rhino before the panel appeared). Its result replaces <paramref name="summary"/>.</param>
    /// <param name="manualRecompute">Runs only on Run, never by itself: on open or when a setting changes.</param>
    public TerrainCurveExtractionPanel(RhinoDoc doc, ModelUnitContext units, string title, string sourceName, string summary,
        TerrainCurveExtractionSettings settings,
        Func<TerrainCurveExtractionSettings, CancellationToken, Action<string>, Action<TerrainCurveExtractionPrepared>, Task<TerrainCurveExtractionPrepared>> compute,
        Guid? sourceId = null, bool manualRecompute = false, Task<string>? loading = null)
    {
        _doc = doc; _units = units; Settings = settings; _compute = compute; _manual = manualRecompute; _loading = loading;
        _loaded = loading == null;
        _refresh = new Button { Text = manualRecompute ? "Run" : "Recompute", Enabled = _loaded };
        _summary = new Label { Text = summary, Wrap = WrapMode.Word, Width = 460 };
        _prompt = title + (manualRecompute
            ? " — set up the panel and Run; Enter keeps a ready preview, Esc cancels"
            : " — adjust the floating panel; Enter keeps a ready preview, Esc cancels");
        _preview = new TerrainCurveExtractionPreview(doc.RuntimeSerialNumber, sourceId);
        Title = title + " — " + sourceName;
        Resizable = true; Padding = 12; Size = new Size(660, 790); MinimumSize = new Size(540, 520);
        ShowInTaskbar = false;
        Owner = RhinoEtoApp.MainWindowForDocument(doc);
        this.UseRhinoStyle();
        // The scrollable carries the margin, not the body: ExpandContentWidth stretches the body to the viewport
        // and then adds the body's own padding outside it, which scrolled sideways by that much (found live).
        var body = new DynamicLayout { Spacing = new Size(8, 7) };
        body.AddRow(_summary);
        body.AddRow(new Label { Text = manualRecompute
            ? "Adjust settings, then Run; Stop ends a run. The views stay free to pan, zoom and rotate. Keep writes the layers; Cancel discards them."
            : "Adjust settings to update the preview. The views stay free to pan, zoom and rotate. Keep writes the layers; Cancel discards them.", Wrap = WrapMode.Word, Width = 460 });
        if (sourceId.HasValue)
        {
            // Preview curves draw over everything, so the source is context, never in the way.
            var showSource = new CheckBox { Text = "Show source", Checked = true };
            _preview.ShowSource = true;
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
        if (scan) Note(contours, "Dark green: horizontal contours, one elevation per curve. Muted area outlines carry ground height.");
        Number(contours, scan ? "Terrain accuracy · " + length : "Vertical tolerance · " + length, nameof(settings.Tolerance));
        var detail = scan ? Section(_scanAdvanced, "Recovery details") : contours;
        Number(detail, scan ? "Natural-boundary plan tolerance · " + length : "Plan tolerance · " + length, nameof(settings.PlanTolerance));
        // Contours at an interval, then spot heights where the rebuild still misses.
        Number(contours, "Contour interval · " + length, nameof(settings.ContourInterval), _units.FromMeters(.01));
        if (settings is ScanToTerrainSettings scanSettings)
            // Paving is carried by its outline breaklines.
            Number(contours, "Paved accuracy · " + length, nameof(scanSettings.PavedTolerance), _units.FromMeters(.01));
        else
        {
            var creasePreview = new CheckBox { Text = "Preview creases", Checked = true };
            _visibility["Creases"] = creasePreview;
            creasePreview.CheckedChanged += (_, _) => UpdatePreview(); detail.AddRow(creasePreview);
        }
        Number(detail, "Recovery inputs per hectare", nameof(settings.InputsPerHectare), 1, 100000, 0);
        body.AddRow(new Label { Text = scan ? "Tolerance describes the inferred ground, not surveyed ground beneath removed objects. Roof fallbacks remain marked for review." : "Tolerance is measured at the reference terrain vertices; walls are carried by their crease lines and not measured. A spent input cap reports the remaining error.", Wrap = WrapMode.Word, Width = 460 });
        body.AddRow(new Label { Text = "Kept output goes under 'MoleHill <name>::Input': Contours, Spots, Breaklines (every line the terrain must keep), Boundary and Zones — the layers a MoleHill terrain of that name reads." + (scan ? " Buildings and drawing-only outlines go under '::Context'." : ""), Wrap = WrapMode.Word, Width = 460 });
        if (scan) body.AddRow(new Expander { Header = "Advanced", Content = _scanAdvanced, Expanded = false });
        var shell = new DynamicLayout { Spacing = new Size(8, 8) };
        var scroll = new Scrollable { Content = body, ExpandContentWidth = true, Padding = 4 };
        UiControls.DisableHorizontalScrolling(scroll);
        shell.Add(scroll, yscale: true);
        shell.AddRow(_status);
        shell.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Items = { _refresh, new StackLayoutItem(null, true), _cancel, _keep }
        });
        Content = shell;
        _keep.Click += (_, _) => { if (CanKeep) Post(Request.Keep); };
        _cancel.Click += (_, _) => Post(Request.Cancel);
        _refresh.Click += (_, _) => { if (_running) Stop(); else Recompute(); };
        KeyDown += (_, e) => { if (e.Key == Keys.Escape) { e.Handled = true; Post(Request.Cancel); } };
        _timer.Elapsed += OnTick;
        Shown += async (_, _) =>
        {
            _preview.Enabled = true; _timer.Start();
            if (await AwaitLoading() && !_manual) Recompute();
        };
        Closed += (_, _) =>
        {
            _closed = true; _generation++; _timer.Stop(); _timer.Elapsed -= OnTick; _timer.Dispose();
            _cancellation?.Cancel();
            _preview.Dispose(); _doc.Views.Redraw();
            // Closed from its title bar: the command is still waiting.
            if (!_ending) Post(Request.Cancel);
        };
    }

    private bool CanKeep => Prepared != null && !_dirty && !_running && _inputError == null;

    /// <summary>
    /// Shows the panel and waits at the command line until the user keeps a ready preview (Keep, or Enter) or
    /// leaves (Cancel, Esc, closing the panel). True when kept; <see cref="Prepared"/> is then the accepted result.
    /// </summary>
    public bool ShowAndWait()
    {
        Show();
        try
        {
            while (!_closed)
            {
                var get = new GetOption();
                get.SetCommandPrompt(_prompt);
                get.AcceptNothing(true);
                get.AcceptCustomMessage(true);
                int run = _loaded ? get.AddOption(_running ? "Stop" : "Run") : -1;
                int keep = CanKeep ? get.AddOption("Keep") : -1;
                GetResult result = get.Get();
                if (result == GetResult.CustomMessage && get.CustomMessage() is Request request)
                {
                    if (request == Request.Keep && CanKeep) return true;
                    if (request == Request.Cancel) return false;
                    continue;
                }
                if (result == GetResult.Option)
                {
                    int index = get.OptionIndex();
                    if (index == keep && CanKeep) return true;
                    if (index == run) { if (_running) Stop(); else Recompute(); }
                    continue;
                }
                if (result == GetResult.Nothing)
                {
                    if (CanKeep) return true;
                    RhinoApp.WriteLine(_running ? "The preview is still running; Keep is available when it is ready." : "No ready preview to keep yet: Run first.");
                    continue;
                }
                return false; // Esc, or a get ended by Rhino.
            }
            return false;
        }
        finally
        {
            _ending = true;
            if (!_closed) Close();
        }
    }

    /// <summary>Posts to the command's waiting get. Every panel event arrives on the UI thread inside that get.</summary>
    private static void Post(Request request) => GetBaseClass.PostCustomMessage(request);

    /// <summary>Waits for <see cref="_loading"/>, showing its result. False when it failed.</summary>
    private async Task<bool> AwaitLoading()
    {
        if (_loading == null) return true;
        _status.Text = "Reading the source…";
        try
        {
            string summary = await _loading;
            if (_closed) return false;
            _summary.Text = summary;
            _loaded = true; _refresh.Enabled = true;
            _status.Text = _manual ? "Ready. Adjust settings, then Run." : "Ready.";
            Post(Request.Refresh); // The command line offers Run now.
            return true;
        }
        catch (Exception error)
        {
            if (!_closed) _status.Text = error.Message;
            return false;
        }
    }

    /// <summary>
    /// A titled section. Returns the label/field grid; <see cref="Note"/> writes below it, outside the grid.
    /// </summary>
    private DynamicLayout Section(DynamicLayout body, string title)
    {
        var host = new DynamicLayout { Spacing = new Size(8, 5), Padding = 8 };
        var grid = new DynamicLayout { Spacing = new Size(8, 5) };
        host.AddRow(grid);
        _noteHosts[grid] = host;
        body.AddRow(new GroupBox { Text = title, Content = host }); return grid;
    }

    /// <summary>
    /// A wrapped note below a section's fields. Inside the grid, even as a separate row, it sat in the label
    /// column and widened it by its own width, pushing the steppers past the panel's edge (found live: the
    /// scan panel clipped its spinners and grew a horizontal scrollbar at its default size).
    /// </summary>
    private void Note(DynamicLayout section, string text) =>
        (_noteHosts.TryGetValue(section, out DynamicLayout? host) ? host : section)
            .AddRow(new Label { Text = text, Wrap = WrapMode.Word, Width = 440 });

    private void Number(DynamicLayout layout, string label, string propertyName, double? minimum = null, double maximum = 1e12, int decimals = 3, double? increment = null)
    {
        var property = Settings.GetType().GetProperty(propertyName)!;
        double value = Convert.ToDouble(property.GetValue(Settings));
        double min = minimum ?? _doc.ModelAbsoluteTolerance;
        var number = new NumericStepper
        {
            DecimalPlaces = decimals,
            MinValue = min,
            MaxValue = maximum,
            Value = Math.Clamp(value, min, maximum),
            Width = 140,
            Increment = increment ?? (decimals == 0 ? 1 : Math.Max(min, _units.FromMeters(.05)))
        };
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

    /// <summary>A setting changed. A manual panel waits for Run; otherwise the preview updates.</summary>
    private void Schedule()
    {
        if (!_manual) { Recompute(); return; }
        _generation++; _dirty = false; Prepared = null; _keep.Enabled = false;
        _cancellation?.Cancel();
        _status.Text = _inputError ?? (_loaded ? "Settings changed — Run to update the preview." : "Reading the source…");
        Post(Request.Refresh);
    }

    private void Recompute()
    {
        if (!_loaded) return;
        _generation++; _dirty = true; Prepared = null; _keep.Enabled = false;
        _cancellation?.Cancel();
        _status.Text = _inputError ?? "Updating preview…";
    }

    private void Stop()
    {
        _generation++; _dirty = false; Prepared = null; _keep.Enabled = false;
        _cancellation?.Cancel();
        _status.Text = "Stopped. Run to start again.";
    }

    private async void OnTick(object? sender, EventArgs args)
    {
        if (!_dirty || _running || _closed || _inputError != null) return;
        if (PreparationError() is string inputError) { _status.Text = inputError; return; }
        _running = true; _dirty = false; _refresh.Text = "Stop"; Post(Request.Refresh);
        int generation = _generation;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        void Dispatch(Action action) => Application.Instance.AsyncInvoke(() => { if (!_closed && generation == _generation) action(); });
        try
        {
            _previewState = null; UpdatePreview();
            var result = await _compute(Settings with { }, cancellation.Token,
                text => Dispatch(() => _status.Text = $"{text} · {Elapsed(clock)}"),
                state => Dispatch(() => { _previewState = state; UpdatePreview(); }));
            if (_closed || generation != _generation || cancellation.IsCancellationRequested) return;
            Prepared = result; _previewState = result; UpdatePreview(); _keep.Enabled = true;
            var recovery = result.Recovery!;
            _status.Text = $"Ready in {Elapsed(clock)}: " + PreparationSummary(result, _units.Abbreviation) +
                $"{Count(recovery.Contours.Count, "contour")}, {Count(recovery.SpotPointCount, "spot")}. " + recovery.Diagnostic +
                (recovery.OriginalMaximumDeviation is double original
                    ? FormattableString.Invariant($" Original ground miss {original:G4} {_units.Abbreviation}.") : string.Empty);
        }
        catch (OperationCanceledException) { if (!_closed && generation == _generation) _status.Text = "Preview stopped."; }
        catch (Exception error) { if (!_closed && generation == _generation) { _status.Text = error.Message; Prepared = null; _keep.Enabled = false; } }
        finally
        {
            _running = false; if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            if (!_closed) { _refresh.Text = _manual ? "Run" : "Recompute"; Post(Request.Refresh); }
        }
    }

    private void UpdatePreview()
    {
        if (_closed) return;
        _preview.Update(_previewState, _visibility.Where(pair => pair.Value.Checked == true).Select(pair => pair.Key).ToHashSet());
        _doc.Views.Redraw();
    }

    private static string Elapsed(System.Diagnostics.Stopwatch clock) =>
        clock.Elapsed.TotalMinutes >= 1
            ? FormattableString.Invariant($"{(int)clock.Elapsed.TotalMinutes}:{clock.Elapsed.Seconds:00} min")
            : FormattableString.Invariant($"{clock.Elapsed.TotalSeconds:F0} s");

    private static string Count(int count, string noun) => FormattableString.Invariant($"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");
}
