using Eto.Drawing;

namespace MoleHill.Rhino.UI;

internal static class UiMetrics
{
    private static float _characterWidth = -1f;

    private static float CharacterWidth
    {
        get
        {
            if (_characterWidth >= 0f)
                return _characterWidth;

            using var font = new Font(SystemFont.Bold);
            float measured = font.MeasureString("0000000000").Width / 10f;
            _characterWidth = measured > 0f ? measured : 7f;
            return _characterWidth;
        }
    }

    /// <summary>Call when Rhino's UI font or theme may have changed.</summary>
    public static void Invalidate() => _characterWidth = -1f;

    public static int Chs(double count) => (int)Math.Ceiling(count * CharacterWidth);

    // Semantic spacing scale. UI code should use the intent-bearing aliases below instead of
    // introducing another local 4/6/8 pixel decision for every row and card.
    public const int SpaceHairline = 1;
    public const int SpaceXSmall = 2;
    public const int SpaceSmall = 4;
    public const int SpaceMedium = 6;
    public const int SpaceLarge = 8;
    public const int SpaceXLarge = 12;

    // Every padding and spacing number in the UI comes from the scale above, through these names where
    // the intent recurs (UiTokenGuardTests fails on a bare number). A one-off value is a scale step, not a
    // new number: the point is that changing the panel's rhythm is an edit here, not a sweep.

    /// <summary>Gap between cards in a stack; also the height of the drop target between them.</summary>
    public const int StackGap = SpaceLarge;

    /// <summary>Inset around a dialog window's content.</summary>
    public const int DialogPadding = SpaceXLarge;

    /// <summary>Gap between a dialog's groups, and between its buttons.</summary>
    public const int DialogSpacing = SpaceLarge;

    /// <summary>Gap between the controls inside one dialog row.</summary>
    public const int DialogRowSpacing = SpaceMedium;

    public static Size DialogGap => new(DialogSpacing, DialogSpacing);

    public const int ControlHeight = 26;
    public const int CompactControlHeight = 22;

    /// <summary>
    /// Glyph size and button box for icon actions, matched to Rhino's own panel toolbars (the Layers
    /// panel sits right beside ours). The previous 16px glyph in a 28px box read as noticeably chunkier
    /// than everything McNeel ships, which made the panel look like it came from somewhere else.
    /// </summary>
    public const int IconSize = 14;

    public const int IconButtonWidth = 22;

    /// <summary>The ▴▾ pair at the end of a number field: each arrow is this wide and half a row tall.</summary>
    public const int SpinnerWidth = 14;
    public const int SpinnerGlyph = 9;

    // ── Card header (design review Steps 13–20) ─────────────────────────────
    // Two fixed lines at every width: grip, arrow, enable, type icon and actions centre on the first line
    // (the name); the type/summary line sits under the name. No line ever hides, wraps or is added.

    /// <summary>Width of the disclosure arrow's slot; the arrow itself is <see cref="DisclosureGlyph"/>.</summary>
    public const int DisclosureSize = 12;
    public const int DisclosureGlyph = 8;

    /// <summary>The bare type glyph beside the card name (no plate behind it).</summary>
    public const int CardTypeIconSize = 10;

    /// <summary>Gap between the name line and the type/summary line.</summary>
    public const int CardHeaderLineGap = SpaceXSmall;
    public const int CardAccentWidth = 5;
    public const int CardHorizontalPadding = 8;
    public const int CardVerticalPadding = 6;
    public const int SectionHorizontalPadding = 8;
    public const int SectionTopPadding = 8;
    public const int SectionBottomPadding = 4;
    public const int PropertyRowVerticalPadding = 3;

    /// <summary>The narrowest supported dock-panel content width, in logical Eto pixels.</summary>
    public const int CompactPanelTarget = 240;

    /// <summary>
    /// Width of the label column in a property row. Sized for the longest labels the cards actually use
    /// ("Exact TIN Mesh", "Contour Mode"), which were being clipped mid-word at the old width. Costs
    /// nothing at the compact target, where rows stack below <see cref="RowBreak"/> and the label gets a
    /// full line to itself anyway.
    /// </summary>
    public static int LabelColumn => Chs(13);

    public static int ShortLabel => Chs(8);

    /// <summary>A fixed-width number box outside the card rows (card rows span the editor column).</summary>
    public static int NumericField => Chs(9);

    public static int DropDown => Chs(13);

    public static int SliderMin => Chs(10);

    /// <summary>The text box beside a slider; holds a value and its unit ("30.000 °").</summary>
    public static int SliderText => Chs(10);

    // Keep ordinary label/editor rows side-by-side at Blender-like narrow panel widths. Only below
    // this point is a second line more usable than two compressed columns.
    public static int RowBreak => Chs(30);

    public static int ControlGroupBreak => Chs(44);

    // Secondary card metadata disappears before essential title/actions are squeezed.
    public static int HeaderStatusBreak => Chs(42);

    public static int Hysteresis => Chs(1);
}
