using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Self-registering descriptor for one analysis type (Analysis tab).
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
    public virtual IReadOnlyList<ParameterDescriptor<AnalysisDefinition>> Parameters => Array.Empty<ParameterDescriptor<AnalysisDefinition>>();

    /// <summary>
    /// Why this analysis cannot produce anything yet, or null when it is ready.
    ///
    /// Several analyses need something the user has to supply before they can do their job — cut/fill
    /// needs a surface to compare against, waterflow needs start points — and until then they run,
    /// succeed, and emit nothing. That silence is the worst part: the card fills with plausible controls
    /// and placeholder numbers, and nothing anywhere says which of them is the one holding it up. A
    /// descriptor answers that here and the panel puts it at the top of the card, above the controls.
    ///
    /// Phrase it as the thing to do ("Needs a reference surface…"), not as a failure.
    /// </summary>
    public virtual string? DescribeBlocker(TerrainDefinition terrain, AnalysisDefinition analysis) => null;

    /// <summary>
    /// A quiet statement of how this analysis is currently set up, or null when there is nothing worth
    /// saying. Rendered as muted text, not as a warning.
    ///
    /// This exists because a sensible default is invisible: cut/fill compares the terrain's initial
    /// triangulation against the finished stack whether or not a reference is set, and a card that says
    /// nothing about that reads as a card waiting to be configured. Say what it is doing, rather than
    /// asking for something it does not need.
    /// </summary>
    public virtual string? DescribeBasis(TerrainDefinition terrain, AnalysisDefinition analysis) => null;

    /// <summary>
    /// Computes this analysis's summary (and emits any generated objects onto <c>context.Build</c>) during
    /// the analysis pass, or returns null when the type produces nothing there. The pass has already
    /// checked <c>IsEnabled</c>, consulted the stage cache and prepared the mesh arrays on the context.
    /// </summary>
    public virtual TerrainAnalysisSummary? Build(AnalysisBuildContext context, AnalysisDefinition analysis) => null;
}
