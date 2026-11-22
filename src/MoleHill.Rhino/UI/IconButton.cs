using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// An icon-only action: the glyph alone at rest, a fill on hover and press, and a ring while it has
/// keyboard focus (design review Step 18). Every icon action in the panel is one, so they all look and
/// behave alike.
///
/// It is drawn rather than a styled <see cref="Button"/>, which is how Rhino's own Layers toolbar does it:
/// a native WPF button always paints its chrome, and the old attempt to flatten it set a WinForms-only
/// property that did nothing on WPF, so every icon action kept its outline.
///
/// The box is <see cref="UiMetrics.IconButtonWidth"/> × <see cref="UiMetrics.CompactControlHeight"/>, the
/// size Rhino's Layers panel uses (measured live: 22 × 22 with 16 px images).
/// </summary>
internal sealed class IconButton : Drawable
{
    private Image? _image;
    private bool _hover;
    private bool _pressed;

    public IconButton()
    {
        Size = new Size(UiMetrics.IconButtonWidth, UiMetrics.CompactControlHeight);
        CanFocus = true;
        Paint += (_, e) => Draw(e.Graphics);

        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; _pressed = false; Invalidate(); };
        MouseDown += (_, e) =>
        {
            // Handled, or the press would also reach a card header behind it and collapse the card.
            e.Handled = true;
            if (!Enabled || e.Buttons != MouseButtons.Primary)
                return;

            _pressed = true;
            Focus();
            Invalidate();
        };
        MouseUp += (_, e) =>
        {
            e.Handled = true;
            bool fire = _pressed && _hover && Enabled;
            _pressed = false;
            Invalidate();
            if (fire)
                Click?.Invoke(this, EventArgs.Empty);
        };
        KeyDown += (_, e) =>
        {
            if (!Enabled || (e.Key != Keys.Enter && e.Key != Keys.Space))
                return;

            e.Handled = true;
            Click?.Invoke(this, EventArgs.Empty);
        };
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
        EnabledChanged += (_, _) => Invalidate();
    }

    public event EventHandler<EventArgs>? Click;

    public Image? Image
    {
        get => _image;
        set
        {
            _image = value;
            Invalidate();
        }
    }

    private void Draw(Graphics g)
    {
        var box = new RectangleF(0, 0, Width, Height);
        if (Enabled && (_pressed || _hover))
            g.FillRectangle(_pressed ? UiTheme.IconPressed : UiTheme.IconHover, box);
        if (HasFocus)
            g.DrawRectangle(UiTheme.FocusRing, 0.5f, 0.5f, Width - 1f, Height - 1f);

        if (_image == null)
            return;

        float x = (Width - _image.Width) / 2f;
        float y = (Height - _image.Height) / 2f;
        g.DrawImage(_image, (float)Math.Round(x), (float)Math.Round(y));
    }
}
