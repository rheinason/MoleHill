using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The floating mhRelaxRoute panel, pinned over the active viewport while the session runs: the score
/// against the sketch with its history, the compromise triangle, how firmly to keep to the sketch, how
/// plain the route should be, the figures, the stretches the route does not fit, and the session's
/// actions. It only gathers intent and shows state; the session owns the band and the document. Like the
/// sculpt toolbar it is non-activating and hands focus back to Rhino after every change, so the viewport
/// keeps its keys.
/// </summary>
internal sealed class RouteRelaxForm : Form
{
    private const int PanelWidth = 260;

    private readonly Label _score;
    private readonly ScoreSparkline _history = new();
    private readonly Label _status;
    private readonly RouteWeightTriangle _triangle = new();
    private readonly Slider _fidelity;
    private readonly Label _fidelityValue;
    private readonly Slider _simplicity;
    private readonly Label _simplicityValue;
    private readonly Label _figures;
    private readonly Label _curve;
    private readonly StackLayout _issues;
    private bool _isRefreshing;
    private PointF? _dragOffset;

    public RouteRelaxForm(RhinoDoc doc)
    {
        Title = "Relax Route";
        WindowStyle = WindowStyle.None;
        Resizable = false;
        Maximizable = false;
        Minimizable = false;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Owner = RhinoEtoApp.MainWindowForDocument(doc);
        this.UseRhinoStyle();
        BackgroundColor = UiTheme.ToolbarBackground;

        _score = new Label
        {
            Text = "as your sketch",
            Font = SystemFonts.Bold(13f),
            TextColor = UiTheme.PrimaryText,
            Wrap = WrapMode.Word,
            Width = PanelWidth - 20,
        };
        _status = MakeMuted("Relaxing…");

        _fidelity = new Slider { MinValue = 0, MaxValue = 100, Width = 150, ToolTip = "How firmly the route keeps to your sketch. Loose lets it find its own way between the pins." };
        _fidelityValue = MakeValueLabel();
        _fidelity.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;
            double fidelity = SliderToFidelity(_fidelity.Value);
            _fidelityValue.Text = FormatFidelity(fidelity);
            FidelityChanged?.Invoke(fidelity);
            RhinoApp.SetFocusToMainWindow();
        };

        _simplicity = new Slider { MinValue = 0, MaxValue = 20, Width = 150, ToolTip = "Plainer: fewer, broader bends and longer grades, for more earthwork." };
        _simplicityValue = MakeValueLabel();
        _simplicity.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;
            double simplicity = _simplicity.Value / 2.0;
            _simplicityValue.Text = simplicity.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            SimplicityChanged?.Invoke(simplicity);
            RhinoApp.SetFocusToMainWindow();
        };

        _triangle.WeightsChanged += (distance, steepness, earthworks, commit) =>
        {
            WeightsChanged?.Invoke(distance, steepness, earthworks, commit);
            if (commit)
                RhinoApp.SetFocusToMainWindow();
        };

        _figures = MakeMuted(string.Empty);
        _curve = MakeMuted(string.Empty);
        _issues = new StackLayout { Orientation = Orientation.Vertical, Spacing = 4 };

        var help = MakeButton("Help me", "Search for a route near your sketch and through your pins: it finds the switchbacks a sketch has no room for.", () => HelpRequested?.Invoke());
        var reset = MakeButton("Reset", "Back to your sketch as drawn.", () => ResetRequested?.Invoke());
        var unpin = MakeButton("Unpin all", "Release every pin but the ends.", () => UnpinAllRequested?.Invoke());
        var keep = MakeButton("Keep", "Add the route as a curve (Enter in the viewport).", () => KeepRequested?.Invoke());
        var cancel = MakeButton("Cancel", "Leave without adding anything (Esc in the viewport).", () => CancelRequested?.Invoke());
        keep.Font = SystemFonts.Bold();

        var title = new Label { Text = "⠿  Relax Route", Font = SystemFonts.Bold(), TextColor = UiTheme.MutedText, Cursor = Cursors.Move };
        MakeDraggable(title);
        MakeDraggable(this);

        Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Padding = new Padding(10, 8),
            Spacing = 6,
            Width = PanelWidth,
            Items =
            {
                title,
                _score,
                _history,
                _status,
                MakeSeparator(),
                MakeCaption("Balance"),
                _triangle,
                Row(MakeCaption("Keep to sketch"), _fidelity, _fidelityValue),
                Row(MakeCaption("Plainness"), _simplicity, _simplicityValue),
                MakeSeparator(),
                _figures,
                _curve,
                _issues,
                MakeSeparator(),
                Row(help, reset, unpin),
                Row(keep, cancel),
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Escape)
            {
                e.Handled = true;
                CancelRequested?.Invoke();
            }
            else if (e.Key == Keys.Enter)
            {
                e.Handled = true;
                KeepRequested?.Invoke();
            }
        };
    }

    public event Action<double, double, double, bool>? WeightsChanged;

    public event Action<double>? FidelityChanged;

    public event Action<double>? SimplicityChanged;

    public event Action? HelpRequested;

    public event Action? ResetRequested;

    public event Action? UnpinAllRequested;

    public event Action? KeepRequested;

    public event Action? CancelRequested;

    /// <summary>Places the panel just inside the top-right corner of the active view.</summary>
    public void PositionOverActiveView(RhinoDoc doc)
    {
        var view = doc.Views.ActiveView;
        if (view == null)
            return;

        var rect = view.ScreenRectangle;
        Location = new Point(rect.Right - PanelWidth - 32, rect.Top + 12);
    }

    /// <summary>Mirrors the session's settings into the controls.</summary>
    public void SyncSettings(double distance, double steepness, double earthworks, double fidelity, double simplicity)
    {
        _isRefreshing = true;
        try
        {
            _triangle.SetWeights(distance, steepness, earthworks);
            _fidelity.Value = FidelityToSlider(fidelity);
            _fidelityValue.Text = FormatFidelity(fidelity);
            _simplicity.Value = (int)Math.Round(Math.Clamp(simplicity, 0.0, 10.0) * 2.0);
            _simplicityValue.Text = simplicity.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>The fast-changing part: the score and the session's status.</summary>
    public void ShowProgress(string score, double improvement, string status)
    {
        _score.Text = score;
        _status.Text = status;
        _history.Add(improvement);
    }

    public void ClearHistory() => _history.Clear();

    /// <summary>The slower part: the figures, the curve Keep will add, and the stretches the route does not fit.</summary>
    public void ShowEvaluation(string figures, string? curve, IReadOnlyList<string> issues)
    {
        _figures.Text = figures;
        _curve.Text = curve ?? "Fitting the curve when the route settles…";
        _issues.Items.Clear();
        foreach (string issue in issues)
        {
            _issues.Items.Add(new Label
            {
                Text = issue,
                Wrap = WrapMode.Word,
                Width = PanelWidth - 20,
                TextColor = UiTheme.WarningText,
                BackgroundColor = UiTheme.WarningBackground,
            });
        }
    }

    /// <summary>Busy while Help me searches: the actions wait, the panel says why.</summary>
    public void SetBusy(string? message)
    {
        bool busy = message != null;
        if (busy)
            _status.Text = message;
        foreach (Control control in Children)
        {
            if (control is Button button)
                button.Enabled = !busy || button.Text == "Cancel";
        }
    }

    // Fidelity is logarithmic: 0 is off, then 0.01 to 100 across the slider, 1 in the middle-right.
    private static double SliderToFidelity(int value) => value <= 0 ? 0.0 : Math.Pow(10.0, ((value - 1) / 99.0 * 4.0) - 2.0);

    private static int FidelityToSlider(double fidelity) =>
        fidelity <= 0.0 ? 0 : (int)Math.Round(Math.Clamp(((Math.Log10(fidelity) + 2.0) / 4.0 * 99.0) + 1.0, 1.0, 100.0));

    private static string FormatFidelity(double fidelity) =>
        fidelity <= 0.0 ? "off" : fidelity.ToString(fidelity < 0.1 ? "0.00" : fidelity < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);

    private static StackLayout Row(params Control[] controls)
    {
        var row = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (Control control in controls)
            row.Items.Add(control);
        return row;
    }

    private static Button MakeButton(string text, string tooltip, Action click)
    {
        var button = new Button { Text = text, ToolTip = tooltip, MinimumSize = new Size(70, 24), BackgroundColor = UiTheme.PillBackground };
        button.Click += (_, _) =>
        {
            click();
            RhinoApp.SetFocusToMainWindow();
        };
        return button;
    }

    private static Label MakeCaption(string text) => new()
    {
        Text = text,
        TextColor = UiTheme.MutedText,
        VerticalAlignment = VerticalAlignment.Center,
        Width = 80,
    };

    private static Label MakeMuted(string text) => new()
    {
        Text = text,
        TextColor = UiTheme.MutedText,
        Wrap = WrapMode.Word,
        Width = PanelWidth - 20,
    };

    private static Label MakeValueLabel() => new()
    {
        TextColor = UiTheme.PrimaryText,
        VerticalAlignment = VerticalAlignment.Center,
        Width = 28,
    };

    private static Control MakeSeparator() => new Panel
    {
        Height = 1,
        Width = PanelWidth - 20,
        BackgroundColor = UiTheme.ZoneStripColor,
    };

    /// <summary>Window-drag for the borderless form, as the sculpt toolbar does.</summary>
    private void MakeDraggable(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;
            _dragOffset = new PointF(Mouse.Position.X - Location.X, Mouse.Position.Y - Location.Y);
            e.Handled = true;
        };
        control.MouseMove += (_, e) =>
        {
            if (_dragOffset is not { } offset)
                return;
            Location = new Point((int)Math.Round(Mouse.Position.X - offset.X), (int)Math.Round(Mouse.Position.Y - offset.Y));
            e.Handled = true;
        };
        control.MouseUp += (_, e) =>
        {
            if (_dragOffset == null)
                return;
            _dragOffset = null;
            e.Handled = true;
            RhinoApp.SetFocusToMainWindow();
        };
    }
}
