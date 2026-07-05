using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Self-registering descriptor for one analysis/annotation type (Analysis &amp; Annotation tabs).
/// Supplies the JSON discriminator, factory, add-menu grouping, and card chrome. The card body and the
/// per-type collapsed summary stay bespoke in the panel (they're genuinely type-specific). Discovered by
/// reflection in <see cref="AnalysisTypeRegistry"/>.
/// </summary>
internal abstract class AnalysisTypeDescriptor
{
    /// <summary>Stable JSON discriminator (must match the historical `$type` value).</summary>
    public abstract string Kind { get; }

    public abstract Type DefinitionType { get; }

    /// <summary>Short label shown in the card header.</summary>
    public abstract string TypeLabel { get; }

    /// <summary>Label shown in the add-menu (may differ from <see cref="TypeLabel"/>).</summary>
    public abstract string MenuLabel { get; }

    /// <summary>Icon-plate glyph text (e.g. "EW", "%", "Z"), used as a fallback when <see cref="IconName"/> is null.</summary>
    public abstract string IconLabel { get; }

    /// <summary>Optional panel icon resource name (loaded via <c>PanelIcons.Load</c>). Null falls back to <see cref="IconLabel"/>.</summary>
    public virtual string? IconName => null;

    /// <summary>Accent strip ARGB color for the card.</summary>
    public abstract int AccentArgb { get; }

    /// <summary>True for annotation-tab types (contours, labels, sections); false for analysis-tab types.</summary>
    public abstract bool IsAnnotation { get; }

    /// <summary>Card subtitle when the analysis is not the active preview.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Card subtitle when this analysis is the active preview (null = no active variant).</summary>
    public virtual string? ActiveSubtitle => null;

    /// <summary>Ordering within its add-menu group.</summary>
    public virtual int SortOrder => 0;

    public abstract AnalysisDefinition Create();

    /// <summary>
    /// Declarative parameter schema for this type's card body. The panel generates rows from this list via
    /// the schema builder; anything not expressible here (summaries, legends, the insertion-origin picker)
    /// stays in the panel's bespoke before/after hooks. Empty means "schema covers nothing" — the panel
    /// falls back entirely to bespoke rows for this type.
    /// </summary>
    public virtual IReadOnlyList<AnalysisParameterDescriptor> Parameters => Array.Empty<AnalysisParameterDescriptor>();
}
