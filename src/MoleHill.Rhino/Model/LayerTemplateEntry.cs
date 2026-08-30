using System.Text.Json.Serialization;

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
    /// The <c>LayerRoleRegistry</c> ids whose output lands here, empty for a plain layer the template
    /// creates but nothing routes to — the layers a user draws their own inputs on
    /// (<c>Inputs::Spots</c>, <c>Features::Walls</c>).
    ///
    /// A list because an office may reasonably want several kinds of output on one layer — all the
    /// section furniture together, say — rather than the sublayer-per-kind the defaults ship with.
    /// The reverse is not allowed: one role cannot land on two layers, or output would be duplicated.
    ///
    /// Stored as string ids rather than the enum so that a template written by a newer build keeps
    /// its unknown roles instead of failing to load.
    /// </summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>
    /// The single role this entry carried before a layer could receive more than one. Read on load
    /// and folded into <see cref="Roles"/>; never written.
    /// </summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyRole { get; set; }

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

    /// <summary>
    /// A deep copy. Lives here rather than at the call site so that adding a field has one obvious
    /// place to carry it — the editor used to clone entries by hand and silently dropped the role
    /// binding, which made every layer look unbound and would have erased the bindings on save.
    /// <c>LayerTemplateCopyTests</c> fails if a property is ever added without being copied.
    /// </summary>
    public LayerTemplateEntry Copy() => new()
    {
        Path = Path,
        Roles = new List<string>(Roles),
        LegacyRole = LegacyRole,
        ColorArgb = ColorArgb,
        PrintColorArgb = PrintColorArgb,
        PlotWeight = PlotWeight,
        LinetypeName = LinetypeName,
        AnnotationStyleName = AnnotationStyleName,
        HatchPatternName = HatchPatternName,
        HatchScale = HatchScale,
        HatchRotationDegrees = HatchRotationDegrees,
        PreviewWidthPx = PreviewWidthPx
    };
}
