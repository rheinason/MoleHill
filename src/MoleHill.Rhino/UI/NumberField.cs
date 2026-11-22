using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.UI;

/// <summary>
/// Every number a user types in MoleHill's UI: a native text box showing the value with its unit inside
/// ("2.500 m") and ▴▾ arrows. It replaces Rhino's numeric stepper everywhere, card rows and dialogs alike,
/// and keeps the stepper's surface (<see cref="Value"/>, <see cref="DecimalPlaces"/>, <see cref="MinValue"/>,
/// <see cref="MaxValue"/>, <see cref="Increment"/>, <see cref="ValueChanged"/>), so swapping one for the
/// other is a declaration change.
///
/// Typing, selection and clipboard are Windows' own. The reading is ours: <see cref="UserNumber"/> reads the
/// text, so "3.5", "3,5" and "2,5 m" mean what they say on any machine. The stepper's parser chose point or
/// comma by culture and read "3,5" (or a numpad decimal key on a fo-FO keyboard) as 35.
///
/// A commit (Enter, leaving the field, an arrow, Up/Down; Shift steps ×10) reads the text, clamps it,
/// writes it back in canonical form and raises <see cref="ValueChanged"/>; unlike the stepper, never per
/// keystroke. Text that does not read as a number reverts rather than committing a guess.
/// <see cref="Edited"/> reports typing for the few rows that preview live.
/// </summary>
internal sealed class NumberField : Panel
{
    private double _value;
    private int _decimals = 3;
    private string? _unit;
    private bool _echoing;

    public NumberField()
    {
        TextBox = new TextBox();
        Echo();

        Content = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new StackLayoutItem(TextBox, expand: true),
                new StackLayout
                {
                    Orientation = Orientation.Vertical,
                    Spacing = 0,
                    Items = { Arrow(PanelButtonIcon.ChevronUp, +1), Arrow(PanelButtonIcon.ChevronDown, -1) }
                }
            }
        };

        TextBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Keys.Enter:
                    Commit();
                    e.Handled = true;
                    break;
                case Keys.Up:
                case Keys.Down:
                    Step(e.Key == Keys.Up ? +1 : -1, e.Modifiers.HasFlag(Keys.Shift) ? 10 : 1);
                    e.Handled = true;
                    break;
            }
        };
        TextBox.LostFocus += (_, _) => Commit();
        TextBox.TextChanged += (_, _) =>
        {
            if (!_echoing)
                Edited?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The native text box, for help text and focus wiring.</summary>
    public TextBox TextBox { get; }

    /// <summary>The committed value. Setting it shows the value without raising <see cref="ValueChanged"/>.</summary>
    public double Value
    {
        get => _value;
        set
        {
            _value = Clamp(value);
            Echo();
        }
    }

    public int DecimalPlaces
    {
        get => _decimals;
        set
        {
            _decimals = Math.Max(0, value);
            Echo();
        }
    }

    /// <summary>The unit written inside the field after the number ("m", "°", "%").</summary>
    public string? Unit
    {
        get => _unit;
        set
        {
            _unit = value;
            Echo();
        }
    }

    public double MinValue { get; set; } = double.MinValue;
    public double MaxValue { get; set; } = double.MaxValue;
    public double Increment { get; set; } = 1.0;

    /// <summary>Raised after a commit: Enter, leaving the field, an arrow, or Up/Down.</summary>
    public event EventHandler<EventArgs>? ValueChanged;

    /// <summary>Raised as the user types, before anything is committed.</summary>
    public event EventHandler? Edited;

    /// <summary>Reads the text as typed so far, clamped, without committing it (for live previews).</summary>
    public bool TryReadTyped(out double value)
    {
        bool ok = UserNumber.TryParseLeading(TextBox.Text, out value);
        value = Clamp(value);
        return ok;
    }

    private void Commit()
    {
        if (TryReadTyped(out double typed))
            _value = typed;

        Echo();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Step(int direction, int multiple = 1)
    {
        double from = TryReadTyped(out double typed) ? typed : _value;
        double step = Increment > 0 ? Increment : 1.0;
        _value = Clamp(from + direction * multiple * step);
        Echo();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private double Clamp(double value) => Math.Min(MaxValue, Math.Max(MinValue, value));

    private void Echo()
    {
        if (TextBox == null)
            return;

        _echoing = true;
        TextBox.Text = UserNumber.WithUnit(UserNumber.Format(_value, "F" + _decimals), _unit);
        _echoing = false;
    }

    private IconButton Arrow(PanelButtonIcon icon, int direction)
    {
        var button = new IconButton
        {
            Size = new Size(UiMetrics.SpinnerWidth, UiMetrics.ControlHeight / 2),
            Image = PanelButtonIcons.Get(icon, size: UiMetrics.SpinnerGlyph)
        };
        button.Click += (_, _) => Step(direction);
        return button;
    }
}
