namespace MoleHill.Rhino.Model;

/// <summary>
/// One layer in a template: where it is, what MoleHill output lands on it, and how that output
/// looks.
///
/// Every appearance field is nullable, and null means <em>inherit</em> — from the parent role, and
/// ultimately from the role's built-in default in <c>LayerRoleRegistry</c>. That is what lets a
/// template override a single print width without restating everything beside it.
///
/// The fields split by what a Rhino layer can actually hold. Colour, print colour, print width and
/// linetype are real <c>Rhino.DocObjects.Layer</c> properties, seeded onto the layer when it is
/// created and owned by the user afterwards. The rest have no layer equivalent and are applied to
/// the generated object instead.
/// </summary>
public sealed class LayerTemplateEntry
{
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The <c>LayerRoleRegistry</c> id whose output lands here, or null for a plain layer the
    /// template creates but nothing routes to — the layers a user draws their own inputs on
    /// (<c>Inputs::Spots</c>, <c>Features::Walls</c>).
    ///
    /// Stored as the string id rather than the enum so that a template written by a newer build
    /// keeps its unknown roles as plain layers instead of failing to load.
    /// </summary>
    public string? Role { get; set; }

    // ── Rhino Layer properties (seeded at creation, then owned by the user) ──

    public int? ColorArgb { get; set; }

    /// <summary>Print colour. Null follows the display colour.</summary>
    public int? PrintColorArgb { get; set; }

    /// <summary>Print width in millimetres of printed line, unaffected by model units.</summary>
    public double? PlotWeight { get; set; }

    public string? LinetypeName { get; set; }

    // ── MoleHill object appearance (no Rhino layer equivalent) ──

    /// <summary>Dimension style generated text on this layer binds to. Text roles only.</summary>
    public string? AnnotationStyleName { get; set; }

    /// <summary>Hatch pattern for filled regions. Fill roles only.</summary>
    public string? HatchPatternName { get; set; }

    public double? HatchScale { get; set; }

    public double? HatchRotationDegrees { get; set; }

    /// <summary>
    /// Viewport thickness in pixels. Null derives it from <see cref="PlotWeight"/>, which is what
    /// almost every layer should do — set it only where the derived value would change how the
    /// layer already looks.
    /// </summary>
    public int? PreviewWidthPx { get; set; }
}
