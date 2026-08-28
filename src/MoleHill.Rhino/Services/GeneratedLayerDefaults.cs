namespace MoleHill.Rhino.Services;

/// <summary>
/// Default print widths for the sublayers MoleHill creates for drawing output.
///
/// These are applied once, when the layer is first created, and then belong to the layer: the user edits
/// them in Rhino's Layers panel (including per-detail overrides) and their edits stick. This is the
/// opposite of stamping <see cref="GeneratedRhinoObject.PlotWeight"/> onto every generated object, which
/// re-froze the value on every build and made the layer table powerless.
///
/// Widths are millimetres of printed line and are deliberately unaffected by model units — a 0.5 mm line
/// is 0.5 mm on paper whatever the drawing is measured in.
/// </summary>
internal static class GeneratedLayerDefaults
{
    /// <summary>Sublayer suffix → default print width in millimetres. Ordered longest-first at lookup so
    /// <c>::CutFill::Cut</c> is not shadowed by a shorter suffix.</summary>
    private static readonly (string Suffix, double? PlotWeight)[] Defaults =
    {
        ("::CutFill::Cut", null),
        ("::CutFill::Fill", null),
        ("::Contours::Major", 0.35),
        ("::Contours::Minor", 0.13),
        ("::Labels", null),
        ("::Ticks", 0.18),
        ("::Grid", 0.13),
        ("::Cuts", 0.50)
    };

    /// <summary>
    /// The print width to seed a newly created layer with, or null to leave Rhino's default. Only matches
    /// MoleHill's own generated sublayer suffixes; a user's chosen root output layer is never restyled.
    /// </summary>
    public static double? GetPlotWeight(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return null;

        foreach ((string suffix, double? plotWeight) in Defaults)
        {
            if (fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return plotWeight;
        }

        return null;
    }
}
