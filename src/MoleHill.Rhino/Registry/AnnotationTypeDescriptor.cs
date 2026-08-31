using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Self-registering descriptor for one annotation type (Annotations tab). Supplies the JSON
/// discriminator, factory, add-menu grouping, and card chrome. The card body and the per-type collapsed
/// summary stay bespoke in the panel (they're genuinely type-specific). Discovered by reflection in
/// <see cref="AnnotationTypeRegistry"/>.
///
/// The analysis-side counterpart is <see cref="AnalysisTypeDescriptor"/>. Neither carries an
/// "is this the other kind?" flag any more — which family a type belongs to is now settled by which
/// descriptor base it derives from, so the two cannot disagree.
/// </summary>
internal abstract class AnnotationTypeDescriptor
{
    /// <summary>Stable JSON discriminator (must match the historical `$type` value).</summary>
    public abstract string Kind { get; }

    public abstract Type DefinitionType { get; }

    /// <summary>Short label shown in the card header.</summary>
    public abstract string TypeLabel { get; }

    /// <summary>Label shown in the add-menu (may differ from <see cref="TypeLabel"/>).</summary>
    public abstract string MenuLabel { get; }

    /// <summary>Icon-plate glyph text (e.g. "CT", "SH"), used as a fallback when <see cref="IconName"/> is null.</summary>
    public abstract string IconLabel { get; }

    /// <summary>Optional panel icon resource name (loaded via <c>PanelIcons.Load</c>). Null falls back to <see cref="IconLabel"/>.</summary>
    public virtual string? IconName => null;

    /// <summary>Accent strip ARGB color for the card.</summary>
    public abstract int AccentArgb { get; }

    /// <summary>Card subtitle.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Ordering within the add-menu.</summary>
    public virtual int SortOrder => 0;

    public abstract AnnotationDefinition Create();

    /// <summary>
    /// Declarative parameter schema for this type's card body. The panel generates rows from this list via
    /// the schema builder; anything not expressible here (summaries, the insertion-origin picker) stays in
    /// the panel's bespoke before/after hooks. Empty means "schema covers nothing" — the panel falls back
    /// entirely to bespoke rows for this type.
    /// </summary>
    public virtual IReadOnlyList<AnnotationParameterDescriptor> Parameters => Array.Empty<AnnotationParameterDescriptor>();

    /// <summary>
    /// Why this annotation cannot produce anything yet, or null when it is ready.
    ///
    /// Several annotations need something the user has to supply before they can do their job — a section
    /// needs a cut line, spot heights need points — and until then they run, succeed, and emit nothing.
    /// That silence is the worst part: the card fills with plausible controls and placeholder numbers, and
    /// nothing anywhere says which of them is the one holding it up. A descriptor answers that here and
    /// the panel puts it at the top of the card, above the controls.
    ///
    /// Phrase it as the thing to do ("Needs a curve to sample along…"), not as a failure.
    /// </summary>
    public virtual string? DescribeBlocker(TerrainDefinition terrain, AnnotationDefinition annotation) => null;

    /// <summary>
    /// A quiet statement of how this annotation is currently set up, or null when there is nothing worth
    /// saying. Rendered as muted text, not as a warning.
    /// </summary>
    public virtual string? DescribeBasis(TerrainDefinition terrain, AnnotationDefinition annotation) => null;
}
