using Eto.Drawing;

namespace MoleHill.Rhino.UI;

/// <summary>
/// Theme-aware color tokens for the MoleHill panel.
/// Detects dark vs light from the system control-background luminance so the
/// panel adapts automatically when Rhino switches themes.
/// </summary>
internal static class UiTheme
{
    // ── Detection ──────────────────────────────────────────────────────────
    /// <summary>
    /// Forces a theme instead of asking Rhino. Set only by the layout harness, which renders panel
    /// controls outside a Rhino host to check them at the supported dock widths in both themes — there is
    /// no Rhino to ask, and a check that only ever saw one theme would be half a check. Null (the default)
    /// means "follow Rhino", which is always the case in the running plug-in.
    /// </summary>
    public static bool? DarkOverride { get; set; }

    /// <summary>
    /// True when the panel should draw dark: Rhino paints its panels dark. Read from the colour Rhino
    /// actually paints panel backgrounds with, because that is what the MoleHill panel sits on. The
    /// command-prompt background was used before; its colour is set separately from the theme, so after
    /// switching Rhino to light it could stay dark and the whole panel kept its dark surfaces and light
    /// icons on a light Rhino (found in the manual test run). It remains the fallback, then the system.
    /// </summary>
    public static bool IsDark
    {
        get
        {
            if (DarkOverride is { } forced)
                return forced;

            try
            {
                var panel = global::Rhino.ApplicationSettings.AppearanceSettings.GetPaintColor(
                    global::Rhino.ApplicationSettings.PaintColor.PanelBackground);
                if (panel.A > 0)
                    return Luminance(panel.R, panel.G, panel.B) < 0.5f;
            }
            catch { }

            try
            {
                var c = global::Rhino.ApplicationSettings.AppearanceSettings.CommandPromptBackgroundColor;
                return Luminance(c.R, c.G, c.B) < 0.5f;
            }
            catch { }

            // Fallback: system color luminance (may not reflect Rhino's independent theme).
            var bg = SystemColors.ControlBackground;
            return (0.2126f * bg.R + 0.7152f * bg.G + 0.0722f * bg.B) < 0.5f;
        }
    }

    private static float Luminance(int r, int g, int b) => 0.2126f * (r / 255f) + 0.7152f * (g / 255f) + 0.0722f * (b / 255f);

    // ── Surfaces ───────────────────────────────────────────────────────────
    public static Color CardBackground     => IsDark ? Color.FromArgb(48,  48,  48)  : Color.FromArgb(255, 255, 255);
    public static Color HeaderBackground   => IsDark ? Color.FromArgb(38,  38,  38)  : Color.FromArgb(220, 220, 220);
    public static Color ToolbarBackground  => IsDark ? Color.FromArgb(40,  40,  40)  : Color.FromArgb(228, 228, 228);
    public static Color InputBackground    => IsDark ? Color.FromArgb(36,  36,  36)  : Color.FromArgb(250, 250, 250);
    public static Color BaseCardBackground => IsDark ? Color.FromArgb(52,  54,  60)  : Color.FromArgb(238, 241, 250);
    public static Color PillBackground     => IsDark ? Color.FromArgb(58,  58,  58)  : Color.FromArgb(210, 210, 210);
    public static Color ZoneStripColor     => IsDark ? Color.FromArgb(85,  85,  85)  : Color.FromArgb(185, 185, 185);

    // ── Text ───────────────────────────────────────────────────────────────
    public static Color PrimaryText => IsDark ? Color.FromArgb(224, 224, 224) : Color.FromArgb(30,  30,  30);
    public static Color MutedText   => IsDark ? Color.FromArgb(148, 148, 148) : Color.FromArgb(110, 110, 110);
    public static Color InputText   => IsDark ? Color.FromArgb(220, 220, 220) : Color.FromArgb(20,  20,  20);

    // ── Interaction ────────────────────────────────────────────────────────
    public static Color DragHighlight => IsDark ? Color.FromArgb(70,  110, 195, 255) : Color.FromArgb(100, 150, 230, 255);
    public static Color SepHighlight  => IsDark ? Color.FromArgb(110, 170, 255, 255) : Color.FromArgb(80,  130, 220, 255);
    public static Color ActiveBadge   => IsDark ? Color.FromArgb(255, 210, 80)       : Color.FromArgb(160, 100, 0);
    public static Color ListSelectionBackground => IsDark ? Color.FromArgb(58, 78, 108) : Color.FromArgb(220, 236, 255);

    /// <summary>The drag grip's dots: quiet like secondary text, never the system's black control ink.</summary>
    public static Color GripDots => MutedText;

    /// <summary>Icon actions are bare glyphs at rest; these are their hover and pressed fills.</summary>
    public static Color IconHover   => IsDark ? Color.FromArgb(66,  66,  66)  : Color.FromArgb(208, 208, 208);
    public static Color IconPressed => IsDark ? Color.FromArgb(84,  84,  84)  : Color.FromArgb(190, 190, 190);

    /// <summary>Segmented mode controls: resting segments, their outline, and the selected segment.</summary>
    public static Color SegmentBackground   => IsDark ? Color.FromArgb(56,  56,  56)  : Color.FromArgb(232, 232, 232);
    public static Color SegmentBorder       => IsDark ? Color.FromArgb(92,  92,  92)  : Color.FromArgb(176, 176, 176);
    public static Color SegmentSelected     => RampAccent;
    public static Color SegmentSelectedText => RampAccentText;

    /// <summary>Ring round a drawn control while it has keyboard focus.</summary>
    public static Color FocusRing => IsDark ? Color.FromArgb(110, 170, 255) : Color.FromArgb(53, 96, 163);

    // ── Colour ramp card ───────────────────────────────────────────────────
    // The ramp card is a dense instrument panel: many small chips and fields packed together, where the
    // colours being edited are the content. Its chrome therefore needs to be quieter than a normal card's,
    // or the swatches stop reading. The dark values come from the card's design; the light ones are their
    // counterparts at the same contrast against a light ground.
    public static Color RampSurface     => IsDark ? Color.FromArgb(29,  29,  29)  : Color.FromArgb(247, 247, 247);
    public static Color RampHeader      => IsDark ? Color.FromArgb(48,  48,  48)  : Color.FromArgb(226, 226, 226);
    public static Color RampControl     => IsDark ? Color.FromArgb(84,  84,  84)  : Color.FromArgb(214, 214, 214);
    public static Color RampControlHot  => IsDark ? Color.FromArgb(97,  97,  97)  : Color.FromArgb(200, 200, 200);
    public static Color RampPopup       => IsDark ? Color.FromArgb(44,  44,  44)  : Color.FromArgb(252, 252, 252);
    public static Color RampAccent      => IsDark ? Color.FromArgb(71,  114, 179) : Color.FromArgb(53,  96,  163);
    public static Color RampDivider     => IsDark ? Color.FromArgb(47,  47,  47)  : Color.FromArgb(214, 214, 214);
    public static Color RampControlText => IsDark ? Color.FromArgb(230, 230, 230) : Color.FromArgb(28,  28,  28);
    public static Color RampAccentText  => Color.FromArgb(255, 255, 255);

    /// <summary>Histogram bars sit behind the ramp; they must read as context, never as data to click.</summary>
    public static Color RampHistogram => IsDark ? Color.FromArgb(140, 122, 122, 122) : Color.FromArgb(140, 150, 150, 150);

    /// <summary>Outline drawn around every swatch and grip so a stop the colour of the panel still has edges.</summary>
    public static Color RampSwatchOutline => IsDark ? Color.FromArgb(180, 0, 0, 0) : Color.FromArgb(150, 0, 0, 0);

    /// <summary>The vertical line marking a stop's position over the ramp bar.</summary>
    public static Color RampStopLine         => IsDark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(255, 255, 255);
    public static Color RampStopLineInactive => IsDark ? Color.FromArgb(140, 255, 255, 255) : Color.FromArgb(190, 255, 255, 255);
    public static Color WarningBackground => IsDark ? Color.FromArgb(86, 67, 25) : Color.FromArgb(255, 240, 196);
    public static Color WarningText => IsDark ? Color.FromArgb(255, 220, 132) : Color.FromArgb(112, 68, 0);

    // Every colour the UI draws is named here; UiTokenGuardTests fails on a colour literal anywhere else
    // in UI/. Colours that come from data (a layer, a zone, a ramp stop) are converted, not named.

    // ── Status ─────────────────────────────────────────────────────────────
    public static Color StatusError   => Color.FromArgb(255, 0, 0);
    public static Color StatusWarning => Color.FromArgb(200, 120, 0);

    // ── Cards ──────────────────────────────────────────────────────────────
    /// <summary>A card's strip while another card is dragged over it.</summary>
    public static Color CardDropHighlight => Color.FromArgb(100, 120, 200);

    /// <summary>A card family's type accent from its descriptor's ARGB, opaque; unknown types are neutral.</summary>
    public static Color TypeAccent(int? argb)
    {
        int value = argb ?? Registry.TypeAccent.NeutralArgb;
        return Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

    // ── Swatches ───────────────────────────────────────────────────────────
    /// <summary>Stands in for a colour that cannot be read (no document, a missing layer).</summary>
    public static Color SwatchPlaceholder => Color.FromArgb(80, 80, 80);

    /// <summary>Edge drawn round a small colour swatch so a colour matching the panel still shows.</summary>
    public static Color SwatchOutline => Color.FromArgb(0, 0, 0, 90);

    // ── Brand ──────────────────────────────────────────────────────────────
    /// <summary>The toolbar icons' green accent (#199D49), used where a control draws a single highlight.</summary>
    public static Color BrandAccent => Color.FromArgb(25, 157, 73);
    public static Color BrandAccentFill => Color.FromArgb(25, 157, 73, 70);
    public static Color BrandAccentFaint => Color.FromArgb(25, 157, 73, 140);
    public static Color BrandAccentOutline => Color.FromArgb(255, 255, 255);

    // ── Charts (curve review profile) ──────────────────────────────────────
    public static Color ChartTerrain      => Color.FromArgb(125, 132, 140);
    public static Color ChartVerticalBreak => Color.FromArgb(196, 96, 32);
    public static Color ChartSharpTurn    => Color.FromArgb(232, 84, 64);
    public static Color ChartViolationBand => new(0.86f, 0.32f, 0.25f, 0.16f);
    public static Color ChartReadoutBackground => new(0f, 0f, 0f, 0.55f);
    public static Color ChartReadoutText  => Color.FromArgb(238, 238, 238);

    // ── Block thumbnails ───────────────────────────────────────────────────
    public static Color ThumbnailFace  => Color.FromArgb(150, 170, 190);
    public static Color ThumbnailEdge  => Color.FromArgb(60, 70, 80);
    public static Color ThumbnailCurve => Color.FromArgb(40, 50, 60);
}
