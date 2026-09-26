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
    /// Returns true when Rhino is running in dark mode.
    /// Uses Rhino's command-prompt background color, which reliably tracks the
    /// Rhino theme independently of Windows system colors.
    /// Falls back to system control-background luminance if that call fails.
    /// </summary>
    /// <summary>
    /// Forces a theme instead of asking Rhino. Set only by the layout harness, which renders panel
    /// controls outside a Rhino host to check them at the supported dock widths in both themes — there is
    /// no Rhino to ask, and a check that only ever saw one theme would be half a check. Null (the default)
    /// means "follow Rhino", which is always the case in the running plug-in.
    /// </summary>
    public static bool? DarkOverride { get; set; }

    public static bool IsDark
    {
        get
        {
            if (DarkOverride is { } forced)
                return forced;

            try
            {
                var c = global::Rhino.ApplicationSettings.AppearanceSettings.CommandPromptBackgroundColor;
                float lum = 0.2126f * (c.R / 255f) + 0.7152f * (c.G / 255f) + 0.0722f * (c.B / 255f);
                return lum < 0.5f;
            }
            catch { }

            // Fallback: system color luminance (may not reflect Rhino's independent theme).
            var bg = SystemColors.ControlBackground;
            return (0.2126f * bg.R + 0.7152f * bg.G + 0.0722f * bg.B) < 0.5f;
        }
    }

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
}
