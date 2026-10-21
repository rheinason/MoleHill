using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The floating mhRelaxRoute panel, pinned over the active viewport while the session runs, and the one
/// place its settings live: the score against the sketch with its history; the route's rules (grade
/// limit, minimum radius, corridor width); the compromise triangle; how firmly to keep to the sketch and
/// how plain to be; the figures and the stretches the route does not fit; the output layer; and the
/// actions. Settings were split between this panel and the command line at first, which a user found
/// confusing (2026-10-17); the command line now only takes Enter and Esc.
/// <para>It only gathers intent and shows state; the session owns the band and the document. Like the
/// sculpt toolbar it is non-activating and hands focus back to Rhino after every change, so the viewport
/// keeps its keys. Every row has a fixed height and text that grows scrolls inside its own area, so the
/// window never outgrows the size it opened at: it did, and clipped its buttons.</para>
/// </summary>
internal sealed class RouteRelaxForm : Form
{
    private const int PanelWidth = 300;
    private const int ContentWidth = PanelWidth - 20;
    private const int CaptionWidth = 96;
    private const int ValueWidth = 40;
    private const int ControlWidth = ContentWidth - CaptionWidth - ValueWidth - 12;

    private readonly Label _score;
    private readonly ScoreSparkline _history = new();
    private readonly Label _status;
    private readonly TextBox _maxGrade;
    private readonly TextBox _minimumRadius;
    private readonly TextBox _corridorWidth;
    private readonly RouteWeightTriangle _triangle = new();
    private readonly Slider _fidelity;
    private readonly Label _fidelityValue;
    private readonly Slider _simplicity;
    private readonly Label _simplicityValue;
    private readonly Label _figures;
    private readonly Label _curve;
    private readonly StackLayout _issues;
    private readonly Button _layer;
    private readonly List<Button> _actions = new();
    private Button? _cancel;
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
            Width = ContentWidth,
            Height = 24,
        };
        _history.Size = new Size(ContentWidth, 36);
        _status = MakeMuted("Relaxing…", 32);

        _maxGrade = MakeField("The steepest the route may climb. Type it in any slope unit: 8%, 80prom, 1:12.5 or 4.6deg.");
        _minimumRadius = MakeField("The tightest bend the route may take, in model units. 0 for none.");
        _corridorWidth = MakeField("The road's width: what cut and fill are measured over.");
        CommitOnEnterOrLeave(_maxGrade, text => MaxGradeTyped?.Invoke(text));
        CommitOnEnterOrLeave(_minimumRadius, text => MinimumRadiusTyped?.Invoke(text));
        CommitOnEnterOrLeave(_corridorWidth, text => CorridorWidthTyped?.Invoke(text));

        _triangle.Size = new Size(ContentWidth, 150);
        _triangle.WeightsChanged += (distance, steepness, earthworks, commit) =>
        {
            WeightsChanged?.Invoke(distance, steepness, earthworks, commit);
            if (commit)
                RhinoApp.SetFocusToMainWindow();
        };

        _fidelity = new Slider { MinValue = 0, MaxValue = 100, Width = ControlWidth, ToolTip = "How firmly the route keeps to your sketch. Loose lets it find its own way between the pins." };
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

        _simplicity = new Slider { MinValue = 0, MaxValue = 20, Width = ControlWidth, ToolTip = "Plainer: fewer, broader bends and longer grades, for more earthwork." };
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

        _figures = MakeMuted(string.Empty, 32);
        _curve = MakeMuted(string.Empty, 32);
        _issues = new StackLayout { Orientation = Orientation.Vertical, Spacing = 4, Width = ContentWidth - 18 };
        var issueArea = new Scrollable
        {
            Content = _issues,
            Size = new Size(ContentWidth, 76),
            Border = BorderType.None,
            ExpandContentWidth = false,
            BackgroundColor = UiTheme.ToolbarBackground,
        };

        _layer = new Button { Text = "Current layer", Width = ControlWidth + ValueWidth + 6, ToolTip = "The layer the kept route goes on.", BackgroundColor = UiTheme.PillBackground };
        _layer.Click += (_, _) =>
        {
            LayerRequested?.Invoke();
            RhinoApp.SetFocusToMainWindow();
        };

        var help = MakeAction("Help me", "Search for a route near your sketch and through your pins: it finds the switchbacks a sketch has no room for.", () => HelpRequested?.Invoke());
        var reset = MakeAction("Reset", "Back to your sketch as drawn.", () => ResetRequested?.Invoke());
        var unpin = MakeAction("Unpin all", "Release every pin but the ends.", () => UnpinAllRequested?.Invoke());
        var keep = MakeAction("Keep", "Add the route as a curve (Enter in the viewport).", () => KeepRequested?.Invoke());
        _cancel = MakeAction("Cancel", "Leave without adding anything (Esc in the viewport).", () => CancelRequested?.Invoke());
        keep.Font = SystemFonts.Bold();

        var title = new Label { Text = "⠿  Relax Route", Font = SystemFonts.Bold(), TextColor = UiTheme.MutedText, Cursor = Cursors.Move };
        MakeDraggable(title);
        MakeDraggable(this);

        Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Padding = new Padding(10, 8),
            Spacing = 6,
            Items =
            {
                title,
                _score,
                _history,
                _status,
                MakeSeparator(),
                Row(MakeCaption("Max grade"), _maxGrade),
                Row(MakeCaption("Min radius"), _minimumRadius),
                Row(MakeCaption("Corridor width"), _corridorWidth),
                MakeSeparator(),
                MakeCaption("Balance"),
                _triangle,
                Row(MakeCaption("Keep to sketch"), _fidelity, _fidelityValue),
                Row(MakeCaption("Plainness"), _simplicity, _simplicityValue),
                MakeSeparator(),
                _figures,
                _curve,
                issueArea,
                MakeSeparator(),
                Row(MakeCaption("Layer"), _layer),
                ButtonRow(help, reset, unpin),
                ButtonRow(keep, _cancel),
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Escape)
            {
                e.Handled = true;
                CancelRequested?.Invoke();
            }
        };
    }

    public event Action<double, double, double, bool>? WeightsChanged;

    public event Action<double>? FidelityChanged;

    public event Action<double>? SimplicityChanged;

    /// <summary>Raised with the text typed into a field; the session parses it and syncs the field back.</summary>
    public event Action<string>? MaxGradeTyped;

    public event Action<string>? MinimumRadiusTyped;

    public event Action<string>? CorridorWidthTyped;

    public event Action? LayerRequested;

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

    /// <summary>Mirrors the session's settings into the controls; the texts come formatted in the user's units.</summary>
    public void SyncSettings(
        string maxGrade,
        string minimumRadius,
        string corridorWidth,
        double distance,
        double steepness,
        double earthworks,
        double fidelity,
        double simplicity,
        string layer)
    {
        _isRefreshing = true;
        try
        {
            if (!_maxGrade.HasFocus)
                _maxGrade.Text = maxGrade;
            if (!_minimumRadius.HasFocus)
                _minimumRadius.Text = minimumRadius;
            if (!_corridorWidth.HasFocus)
                _corridorWidth.Text = corridorWidth;
            _triangle.SetWeights(distance, steepness, earthworks);
            _fidelity.Value = FidelityToSlider(fidelity);
            _fidelityValue.Text = FormatFidelity(fidelity);
            _simplicity.Value = (int)Math.Round(Math.Clamp(simplicity, 0.0, 10.0) * 2.0);
            _simplicityValue.Text = simplicity.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            _layer.Text = layer;
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
        if (issues.Count == 0)
        {
            _issues.Items.Add(new Label { Text = "The route fits the ground within the limit.", TextColor = UiTheme.MutedText, Wrap = WrapMode.Word, Width = ContentWidth - 18 });
            return;
        }

        foreach (string issue in issues)
        {
            _issues.Items.Add(new Label
            {
                Text = issue,
                Wrap = WrapMode.Word,
                Width = ContentWidth - 18,
                TextColor = UiTheme.WarningText,
                BackgroundColor = UiTheme.WarningBackground,
            });
        }
    }

    /// <summary>Busy while Help me searches or Keep fits: the actions wait (but Cancel), the panel says why.</summary>
    public void SetBusy(string? message, bool allowCancel = true)
    {
        if (message != null)
            _status.Text = message;
        foreach (Button button in _actions)
            button.Enabled = message == null || (allowCancel && button == _cancel);
    }

    // Fidelity is logarithmic: 0 is off, then 0.01 to 100 across the slider, 1 in the middle-right.
    private static double SliderToFidelity(int value) => value <= 0 ? 0.0 : Math.Pow(10.0, ((value - 1) / 99.0 * 4.0) - 2.0);

    private static int FidelityToSlider(double fidelity) =>
        fidelity <= 0.0 ? 0 : (int)Math.Round(Math.Clamp(((Math.Log10(fidelity) + 2.0) / 4.0 * 99.0) + 1.0, 1.0, 100.0));

    private static string FormatFidelity(double fidelity) =>
        fidelity <= 0.0 ? "off" : fidelity.ToString(fidelity < 0.1 ? "0.00" : fidelity < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);

    private static StackLayout Row(params Control[] controls)
    {
        var row = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center, Height = 26 };
        foreach (Control control in controls)
            row.Items.Add(control);
        return row;
    }

    private static StackLayout ButtonRow(params Button[] buttons)
    {
        int width = (ContentWidth - (6 * (buttons.Length - 1))) / buttons.Length;
        foreach (Button button in buttons)
            button.Width = width;
        return Row(buttons);
    }

    private Button MakeAction(string text, string tooltip, Action click)
    {
        var button = new Button { Text = text, ToolTip = tooltip, Height = 26, BackgroundColor = UiTheme.PillBackground };
        button.Click += (_, _) =>
        {
            click();
            RhinoApp.SetFocusToMainWindow();
        };
        _actions.Add(button);
        return button;
    }

    private static TextBox MakeField(string tooltip) => new()
    {
        Width = ControlWidth + ValueWidth + 6,
        ToolTip = tooltip,
        BackgroundColor = UiTheme.InputBackground,
        TextColor = UiTheme.InputText,
    };

    /// <summary>A field commits on Enter or when it loses focus, and only when its text changed.</summary>
    private void CommitOnEnterOrLeave(TextBox box, Action<string> commit)
    {
        string? shown = null;
        box.GotFocus += (_, _) => shown = box.Text;
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter)
                return;
            e.Handled = true;
            shown = box.Text;
            commit(box.Text);
            RhinoApp.SetFocusToMainWindow();
        };
        box.LostFocus += (_, _) =>
        {
            if (!_isRefreshing && box.Text != shown)
                commit(box.Text);
        };
    }

    private static Label MakeCaption(string text) => new()
    {
        Text = text,
        TextColor = UiTheme.MutedText,
        VerticalAlignment = VerticalAlignment.Center,
        Width = CaptionWidth,
    };

    private static Label MakeMuted(string text, int height) => new()
    {
        Text = text,
        TextColor = UiTheme.MutedText,
        Wrap = WrapMode.Word,
        Width = ContentWidth,
        Height = height,
    };

    private static Label MakeValueLabel() => new()
    {
        TextColor = UiTheme.PrimaryText,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right,
        Width = ValueWidth,
    };

    private static Control MakeSeparator() => new Panel
    {
        Height = 1,
        Width = ContentWidth,
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
