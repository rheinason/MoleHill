using Eto.Drawing;
using Eto.Forms;

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
    public static bool IsDark
    {
        get
        {
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
}
