namespace MoleHill.Rhino.Services;

/// <summary>
/// Default appearance for the sublayers MoleHill creates for drawing output: the print width the layer is
/// seeded with, and the viewport thickness the preview conduit draws its objects at.
///
/// Print widths are applied once, when the layer is first created, and then belong to the layer: the user
/// edits them in Rhino's Layers panel (including per-detail overrides) and their edits stick. This is the
/// opposite of stamping <see cref="GeneratedRhinoObject.PlotWeight"/> onto every generated object, which
/// re-froze the value on every build and made the layer table powerless.
///
/// Widths are millimetres of printed line and are deliberately unaffected by model units — a 0.5 mm line
/// is 0.5 mm on paper whatever the drawing is measured in.
///
/// Preview widths are pixels, and exist so the viewport shows the same hierarchy the print does: a major
/// contour reads heavier than a minor one on screen as well as on paper. Everything used to draw at a flat
/// 2 px, which made a section grid line indistinguishable from its profile.
/// </summary>
internal static class GeneratedLayerDefaults
{
    /// <summary>The thickness for generated geometry on a layer with no entry of its own.</summary>
    public const int DefaultPreviewWidth = 2;

    /// <summary>
    /// Cut and fill are the one place a generated layer's colour carries meaning rather than taste: a
    /// section that shades both the same is unreadable, and that is what happened before these were
    /// seeded — the layers were created with Rhino's default black and stayed that way until someone
    /// found "Bake Layers". Matched to the office layer template so both routes agree.
    /// </summary>
    private const int CutColorArgb = unchecked((int)0xFFEB462D);

    private const int FillColorArgb = unchecked((int)0xFF4C849E);

    /// <summary>
    /// Sublayer suffix → default print width in millimetres (null leaves Rhino's default) and preview
    /// thickness in pixels. Matched longest-first so <c>::CutFill::Cut</c> is not shadowed by a shorter
    /// suffix, and so <c>::Contours::Major</c> wins over a bare <c>::Contours</c>.
    /// </summary>
    private static readonly (string Suffix, double? PlotWeight, int PreviewWidth, int? ColorArgb)[] Defaults =
    {
        // A fill is a region, not a line: it prints hairline so the pattern reads without the boundary
        // competing with the profiles crossing it.
        ("::CutFill::Cut", 0.13, 1, CutColorArgb),
        ("::CutFill::Fill", 0.13, 1, FillColorArgb),
        ("::Contours::Major", 0.35, 3, null),
        ("::Contours::Minor", 0.13, 1, null),
        ("::Sections::CutFill::Cut", 0.13, 1, CutColorArgb),
        ("::Sections::CutFill::Fill", 0.13, 1, FillColorArgb),
        // Existing ground is context: a light grey line the proposed profile is read against.
        ("::Sections::Existing", 0.18, 2, unchecked((int)0xFF8C8C8C)),
        ("::Sections::Cuts", 0.50, 4, null),
        ("::Sections::Grid", 0.13, 1, unchecked((int)0xFFB4B4B4)),
        ("::Sections::Ticks", 0.18, 1, unchecked((int)0xFF8C8C8C)),
        ("::Sections::Labels", null, 1, null),
        // The proposed terrain is the subject of the drawing: the heaviest line on it, and black.
        ("::Sections", 0.70, 5, unchecked((int)0xFF000000)),
        ("::Waterflow", 0.30, 3, null),
        ("::Labels", null, 1, null),
        ("::Ticks", 0.18, 1, null),
        ("::Grid", 0.13, 1, null),
        ("::Cuts", 0.50, 4, null)
    };

    /// <summary>Longest suffix first, so a specific path is never shadowed by a shorter one that happens
    /// to be its tail (<c>::Sections::Cuts</c> vs <c>::Cuts</c>).</summary>
    private static readonly (string Suffix, double? PlotWeight, int PreviewWidth, int? ColorArgb)[] OrderedDefaults =
        Defaults.OrderByDescending(entry => entry.Suffix.Length).ToArray();

    /// <summary>
    /// The print width to seed a newly created layer with, or null to leave Rhino's default. Only matches
    /// MoleHill's own generated sublayer suffixes; a user's chosen root output layer is never restyled.
    /// </summary>
    public static double? GetPlotWeight(string? fullPath)
    {
        return TryMatch(fullPath, out var entry) ? entry.PlotWeight : null;
    }

    /// <summary>
    /// Viewport thickness in pixels for generated geometry on this layer, before the terrain's preview
    /// line weight multiplier is applied.
    /// </summary>
    public static int GetPreviewWidth(string? fullPath)
    {
        return TryMatch(fullPath, out var entry) ? entry.PreviewWidth : DefaultPreviewWidth;
    }

    /// <summary>
    /// The colour to seed a newly created layer with, or null to leave Rhino's default. Applied once, at
    /// creation, exactly like the print width — after that the layer owns it and the user's edits stick.
    /// </summary>
    public static int? GetColorArgb(string? fullPath)
    {
        return TryMatch(fullPath, out var entry) ? entry.ColorArgb : null;
    }

    /// <summary>
    /// Applies the terrain's preview line weight to a base thickness. Clamped to what Rhino's display
    /// pipeline accepts, and never thinner than one pixel — a line the user turned down should get
    /// lighter, not disappear.
    /// </summary>
    public static int ScalePreviewWidth(int baseWidth, double previewLineWeight)
    {
        double weight = double.IsFinite(previewLineWeight) && previewLineWeight > 0.0 ? previewLineWeight : 1.0;
        return (int)Math.Clamp(Math.Round(baseWidth * weight), 1, 32);
    }

    /// <summary>Layer-driven preview thickness, already scaled by the terrain's multiplier.</summary>
    public static int ResolvePreviewWidth(string? fullPath, double previewLineWeight)
    {
        return ScalePreviewWidth(GetPreviewWidth(fullPath), previewLineWeight);
    }

    private static bool TryMatch(
        string? fullPath,
        out (string Suffix, double? PlotWeight, int PreviewWidth, int? ColorArgb) match)
    {
        match = default;
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        foreach (var entry in OrderedDefaults)
        {
            if (fullPath.EndsWith(entry.Suffix, StringComparison.OrdinalIgnoreCase))
            {
                match = entry;
                return true;
            }
        }

        return false;
    }
}
