namespace MoleHill.Rhino.Registry;

/// <summary>
/// A role's built-in appearance — the bottom of the inheritance chain, used when neither the role's
/// own template entry nor any ancestor's supplies a value.
///
/// Every field is nullable and inherited independently, so a template entry can override just the
/// print width and keep the annotation style it inherits. A null here means "leave Rhino's default",
/// which is not the same as "inherit": the chain has already ended by the time these are read.
/// </summary>
/// <param name="ColorArgb">Display colour, or null to leave Rhino's layer default.</param>
/// <param name="PrintColorArgb">Print colour; null falls back to the display colour.</param>
/// <param name="PlotWeight">Print width in millimetres of printed line, deliberately unaffected by
/// model units — a 0.5 mm line is 0.5 mm on paper whatever the drawing is measured in. Null leaves
/// Rhino's default.</param>
/// <param name="SuppressInheritedPlotWeight">Stops a print width being inherited from the parent
/// role. Text carries no line weight, so a label layer left to inherit its parent drawing weight
/// would print heavier than Rhino's default — which is what these layers have always used.</param>
/// <param name="PreviewWidthPx">Viewport thickness in pixels. Null derives it from
/// <paramref name="PlotWeight"/>, which is what almost every role should do; set it only where the
/// derived value would change today's appearance.</param>
internal sealed record LayerAppearanceDefaults(
    int? ColorArgb = null,
    int? PrintColorArgb = null,
    double? PlotWeight = null,
    int? PreviewWidthPx = null,
    bool SuppressInheritedPlotWeight = false,
    string? LinetypeName = null,
    string? AnnotationStyleName = null,
    string? HatchPatternName = null,
    double? HatchScale = null,
    double? HatchRotationDegrees = null);
