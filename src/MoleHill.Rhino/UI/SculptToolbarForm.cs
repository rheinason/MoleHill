using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Sculpting;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The floating sculpt mini-toolbar: a small borderless, non-activating window pinned over the active
/// viewport's corner while a sculpt session runs. It only edits session preferences (brush, radius,
/// strength, falloff); all document mutation goes through the session controller. Non-activating +
/// explicit focus return keep viewport keys (F, Esc, Ctrl+Z) working.
/// </summary>
internal sealed class SculptToolbarForm : Form
{
    private readonly Dictionary<SculptBrushKind, Button> _brushButtons = new();
    private readonly Slider _radiusSlider;
    private readonly Label _radiusValue;
    private readonly Slider _strengthSlider;
    private readonly Label _strengthValue;
    private readonly DropDown _falloffDropDown;
    private readonly double _radiusReference;
    private bool _isRefreshing;
    private PointF? _dragOffset;

    public event Action<SculptBrushKind>? BrushChanged;
    public event Action<double>? RadiusChanged;
    public event Action<double>? StrengthChanged;
    public event Action<SculptFalloff>? FalloffChanged;
    public event Action? DoneRequested;

    public SculptToolbarForm(RhinoDoc doc, double radiusReference)
    {
        _radiusReference = Math.Max(radiusReference, 1e-6);

        Title = "Sculpt";
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

        var brushRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AddBrushButton(brushRow, SculptBrushKind.Draw, "Draw", "Raise terrain (Ctrl lowers). Shortcut: hold Shift for temporary Smooth.");
        AddBrushButton(brushRow, SculptBrushKind.Subtract, "Sub", "Lower terrain (Ctrl raises).");
        AddBrushButton(brushRow, SculptBrushKind.Erase, "Erase", "Remove sculpt displacement and restore the incoming terrain locally.");
        AddBrushButton(brushRow, SculptBrushKind.Smooth, "Smooth", "Relax terrain toward its neighbors.");
        AddBrushButton(brushRow, SculptBrushKind.Flatten, "Flat", "Move terrain toward the height under the brush at stroke start.");
        AddBrushButton(brushRow, SculptBrushKind.Grab, "Grab", "Drag the terrain under the brush up or down rigidly.");
        AddBrushButton(brushRow, SculptBrushKind.Clay, "Clay", "Build up terrain toward a plane just above the surface.");
        AddBrushButton(brushRow, SculptBrushKind.Noise, "Noise", "Add natural height variation.");

        _radiusSlider = new Slider { MinValue = 0, MaxValue = 100, Width = 90, ToolTip = "Brush radius (F in the viewport adjusts interactively)." };
        _radiusValue = MakeValueLabel();
        _radiusSlider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;
            RadiusChanged?.Invoke(SliderToRadius(_radiusSlider.Value));
            RhinoApp.SetFocusToMainWindow();
        };

        _strengthSlider = new Slider { MinValue = 0, MaxValue = 100, Width = 70, ToolTip = "Brush strength (Shift+F in the viewport adjusts interactively)." };
        _strengthValue = MakeValueLabel();
        _strengthSlider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;
            StrengthChanged?.Invoke(_strengthSlider.Value / 100.0);
            RhinoApp.SetFocusToMainWindow();
        };

        _falloffDropDown = new DropDown { Width = 84, ToolTip = "Brush falloff profile from center to rim." };
        foreach (SculptFalloff falloff in Enum.GetValues<SculptFalloff>())
            _falloffDropDown.Items.Add(new ListItem { Text = falloff.ToString(), Key = falloff.ToString() });
        _falloffDropDown.SelectedIndexChanged += (_, _) =>
        {
            if (_isRefreshing || _falloffDropDown.SelectedIndex < 0)
                return;
            FalloffChanged?.Invoke(Enum.GetValues<SculptFalloff>()[_falloffDropDown.SelectedIndex]);
            RhinoApp.SetFocusToMainWindow();
        };

        var doneButton = new Button { Text = "Done", Width = 52, ToolTip = "End the sculpt session (Enter or Esc in the viewport also ends it)." };
        doneButton.Click += (_, _) => DoneRequested?.Invoke();

        var dragHandle = new Label
        {
            Text = "⠿",
            TextColor = UiTheme.MutedText,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Drag to move the sculpt toolbar.",
            Cursor = Cursors.Move,
        };
        MakeDraggable(dragHandle);
        MakeDraggable(this); // padding/background areas drag too

        Content = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Padding = new Padding(8, 5),
            Spacing = 8,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                dragHandle,
                brushRow,
                MakeSeparator(),
                MakeCaption("Radius"),
                _radiusSlider,
                _radiusValue,
                MakeCaption("Strength"),
                _strengthSlider,
                _strengthValue,
                _falloffDropDown,
                MakeSeparator(),
                doneButton,
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key is Keys.Escape or Keys.Enter)
            {
                e.Handled = true;
                DoneRequested?.Invoke();
            }
        };
    }

    /// <summary>Places the toolbar just inside the top-left corner of the active view.</summary>
    public void PositionOverActiveView(RhinoDoc doc)
    {
        var view = doc.Views.ActiveView;
        if (view == null)
            return;

        var rect = view.ScreenRectangle;
        Location = new Point(rect.Left + 12, rect.Top + 12);
    }

    /// <summary>Mirrors the session's current settings into the controls (used at open and while
    /// F/Shift+F adjust modes scrub values from the viewport).</summary>
    public void SyncFromSession(SculptBrushKind brush, double radius, double strength, SculptFalloff falloff)
    {
        _isRefreshing = true;
        try
        {
            foreach (var (kind, button) in _brushButtons)
                button.BackgroundColor = kind == brush ? UiTheme.ListSelectionBackground : UiTheme.PillBackground;

            _radiusSlider.Value = RadiusToSlider(radius);
            _radiusValue.Text = radius.ToString(radius >= 10 ? "N0" : "N2");
            _strengthSlider.Value = (int)Math.Round(Math.Clamp(strength, 0.0, 1.0) * 100);
            _strengthValue.Text = strength.ToString("N2");
            _falloffDropDown.SelectedIndex = Array.IndexOf(Enum.GetValues<SculptFalloff>(), falloff);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    // Radius slider is logarithmic around the session's reference radius: 0 => 0.1x, 50 => 1x, 100 => 10x.
    private double SliderToRadius(int sliderValue) => _radiusReference * Math.Pow(10.0, (sliderValue - 50) / 50.0);

    private int RadiusToSlider(double radius)
    {
        double t = Math.Log10(Math.Max(radius, 1e-9) / _radiusReference) * 50.0 + 50.0;
        return (int)Math.Round(Math.Clamp(t, 0.0, 100.0));
    }

    private void AddBrushButton(StackLayout row, SculptBrushKind kind, string text, string tooltip)
    {
        var button = new Button
        {
            Text = text,
            MinimumSize = new Size(42, 24),
            ToolTip = tooltip,
            BackgroundColor = UiTheme.PillBackground,
        };
        button.Click += (_, _) =>
        {
            BrushChanged?.Invoke(kind);
            RhinoApp.SetFocusToMainWindow();
        };
        _brushButtons[kind] = button;
        row.Items.Add(button);
    }

    private static Label MakeCaption(string text) => new()
    {
        Text = text,
        TextColor = UiTheme.MutedText,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Label MakeValueLabel() => new()
    {
        TextColor = UiTheme.PrimaryText,
        VerticalAlignment = VerticalAlignment.Center,
        Width = 40,
    };

    private static Control MakeSeparator() => new Panel
    {
        Width = 1,
        Height = 22,
        BackgroundColor = UiTheme.ZoneStripColor,
    };

    /// <summary>Window-drag for the borderless form: press on the handle (or bar background) and move.</summary>
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
            PointF target = new(Mouse.Position.X - offset.X, Mouse.Position.Y - offset.Y);
            Location = new Point((int)Math.Round(target.X), (int)Math.Round(target.Y));
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
