using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

/// <summary>Editor kind a <see cref="ParameterDescriptor{TDefinition}"/> maps to (Eto card row today, GH param later).</summary>
internal enum ParameterKind
{
    Sources,
    Number,
    OptionalNumber,
    Slider,
    Bool,
    ReadOnly,
    Choice,
    Color,
    ColorRamp,
    Text,

    /// <summary>The weighted block-mix card. Objects only — Scatter is the sole declarer.</summary>
    BlockMix,

    /// <summary>
    /// A gradient compliance card's ramp going table: one row per gradient, edited in place, rows added
    /// and removed on the card. Analyses only — gradient compliance is the sole declarer.
    /// </summary>
    GoingTable,

    /// <summary>
    /// An annotation card's block: pick one of the document's block definitions, copy the built-in one
    /// into an editable block of your own, or open it in Rhino's block editor. Annotations only — the
    /// block-attribute annotations are the sole declarers. The name lives in the text accessors; the
    /// built-in block to copy comes from <see cref="ParameterDescriptor{TDefinition}.BlockTemplate"/>.
    /// </summary>
    BlockPicker,
}

/// <summary>
/// One declarative parameter on a terrain type — the Blender-style "the type declares its inputs and the
/// UI is generated" unit. A type's ordered <c>Parameters</c> list drives its panel card (via the schema
/// row builder in <c>MoleHillPanel.Schema.cs</c>) and is the shared contract that will also generate the
/// Grasshopper component.
///
/// <para><typeparamref name="TDefinition"/> is the family this parameter belongs to —
/// <see cref="ModifierDefinition"/>, <see cref="AnalysisDefinition"/>, <see cref="AnnotationDefinition"/>
/// or <see cref="TerrainObjectDefinition"/>. One generic type replaces what used to be four hand-cloned
/// copies, and the type argument is what keeps the families apart: an analysis accessor cannot be handed
/// an annotation, so the four schemas cannot be mixed and cannot drift.</para>
///
/// <para>Values are read/written through typed accessor delegates against the concrete definition (cast
/// inside, mirroring the original hand-written card mutations). Committing is not this type's job: the
/// commit-hint flags below are inert data that the family's own commit closure interprets, so each family
/// keeps its own save/rebuild/refresh semantics. A flag that one family ignores is simply unset there.</para>
/// </summary>
internal sealed class ParameterDescriptor<TDefinition>
    where TDefinition : class
{
    public required ParameterKind Kind { get; init; }

    /// <summary>Property name on the definition — stable id for GH params and debugging.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }
    public string? Help { get; init; }

    /// <summary>Overrides <see cref="Label"/> when the row label depends on live definition state
    /// (e.g. "Low %" vs "Low deg" once the slope unit is changed).</summary>
    public Func<TDefinition, string>? LabelFor { get; init; }

    /// <summary>
    /// Optional visibility gate. When set and it returns false for the current definition, the schema
    /// builder omits this row entirely — the mechanism behind mode-dependent parameters (e.g. the Remesh
    /// quality knobs that only apply to Full Rebuild). Re-evaluated on every card rebuild, and since any
    /// edit (including the gating Choice) rebuilds the card, the row appears/disappears live.
    /// </summary>
    public Func<TDefinition, bool>? VisibleWhen { get; init; }

    // ---- Numeric (Number / OptionalNumber / Slider) ----

    /// <summary>What this parameter's number is in. Drives the row's trailing unit label, and for
    /// <see cref="ParameterUnit.Slope"/> the display conversion and unit-aware parsing too.</summary>
    public ParameterUnit Unit { get; init; } = ParameterUnit.None;

    public int DecimalPlaces { get; init; } = 3;
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double SoftMin { get; init; }
    public double SoftMax { get; init; }

    /// <summary>Stepper increment. Honoured on <see cref="ParameterKind.Number"/>; the slider editor has
    /// no step of its own, so it is ignored there.</summary>
    public double? Step { get; init; }

    /// <summary>OptionalNumber: the value shown as the greyed "inherited" placeholder when blank.</summary>
    public Func<TDefinition, double>? InheritedValue { get; init; }

    // ---- Commit hints ----
    // Inert here by design: the family's commit closure reads whichever of these it honours. Leaving one
    // unset is how a family opts out, so no family has to carry another's semantics.

    /// <summary>Defer the document save and suppress the UI refresh while scrubbing (live drag).</summary>
    public bool LiveScrub { get; init; }

    /// <summary>Commit on every keystroke rather than on focus loss / Enter.</summary>
    public bool LiveEdit { get; init; }

    /// <summary>Commit as a cheap preview refresh (recolor/re-legend) instead of a full rebuild.</summary>
    public bool RefreshOnly { get; init; }

    /// <summary>Commit without a full rebuild, then run the type's own incremental rebuild (contour-style:
    /// cheaper than reprocessing every analysis).</summary>
    public bool IncrementalCommit { get; init; }

    /// <summary>Relayout the owning tab after committing — for edits that change which rows exist.</summary>
    public bool RebuildAfterCommit { get; init; }

    // ---- Sources ----

    public RhinoObjectType ObjectFilter { get; init; }

    // ---- Choice ----

    public IReadOnlyList<(string Key, string Label)>? ChoiceOptions { get; init; }

    /// <summary>Overrides <see cref="ChoiceOptions"/> when the option list depends on live state (e.g. the
    /// value-format dropdown appends a "Custom" entry for a format string outside the standard presets).</summary>
    public Func<TDefinition, IReadOnlyList<(string Key, string Label)>>? ChoiceOptionsFor { get; init; }

    // ---- Color ----
    // Fallback and default text take the owning terrain as well as the definition: a colour row resolves
    // "by layer" against the item's own output layer, falling back to the terrain's layer for the family.

    public Func<TDefinition, int?>? GetColor { get; init; }
    public Action<TDefinition, int?>? SetColor { get; init; }
    public Func<TerrainDefinition, TDefinition, int?>? FallbackColor { get; init; }
    public Func<TerrainDefinition, TDefinition, string>? ColorDefaultTextFor { get; init; }

    // ---- Text ----

    public bool TrimText { get; init; } = true;

    // ---- BlockPicker ----

    /// <summary>The built-in block a "New from default" copy is made from.</summary>
    public MarkerBlockTemplate BlockTemplate { get; init; }

    // ---- Accessors (the relevant pair for this Kind is set) ----

    public Func<TDefinition, double>? GetNumber { get; init; }
    public Action<TDefinition, double>? SetNumber { get; init; }
    public Func<TDefinition, bool>? GetBool { get; init; }
    public Action<TDefinition, bool>? SetBool { get; init; }
    public Func<TDefinition, string?>? GetText { get; init; }
    public Action<TDefinition, string?>? SetText { get; init; }
    public Func<TDefinition, SourceReferenceSet>? GetSources { get; init; }
    public Func<TDefinition, string>? GetReadOnly { get; init; }

    // ---- Factories ----
    // One set for every family. Parameters past the first few are optional and should be passed by name;
    // the leading positional run (key, label, accessors, and the kind's own required values) is identical
    // to what each family's own factory took before the merge.

    public static ParameterDescriptor<TDefinition> Sources(
        string key,
        string label,
        Func<TDefinition, SourceReferenceSet> get,
        RhinoObjectType objectFilter,
        string? help = null,
        Func<TDefinition, bool>? visibleWhen = null,
        Func<TDefinition, string>? labelFor = null) =>
        new()
        {
            Kind = ParameterKind.Sources,
            Key = key,
            Label = label,
            GetSources = get,
            ObjectFilter = objectFilter,
            Help = help,
            VisibleWhen = visibleWhen,
            LabelFor = labelFor,
        };

    public static ParameterDescriptor<TDefinition> Number(
        string key,
        string label,
        Func<TDefinition, double> get,
        Action<TDefinition, double> set,
        string? help = null,
        double? min = 0,
        double? max = null,
        int decimalPlaces = 3,
        double? step = null,
        ParameterUnit unit = ParameterUnit.None,
        bool liveEdit = false,
        bool liveScrub = false,
        bool refreshOnly = false,
        bool incrementalCommit = false,
        bool rebuildAfterCommit = false,
        Func<TDefinition, bool>? visibleWhen = null,
        Func<TDefinition, string>? labelFor = null) =>
        new()
        {
            Kind = ParameterKind.Number,
            Key = key,
            Label = label,
            GetNumber = get,
            SetNumber = set,
            Help = help,
            Unit = unit,
            Min = min,
            Max = max,
            DecimalPlaces = decimalPlaces,
            Step = step,
            LiveEdit = liveEdit,
            LiveScrub = liveScrub,
            RefreshOnly = refreshOnly,
            IncrementalCommit = incrementalCommit,
            RebuildAfterCommit = rebuildAfterCommit,
            VisibleWhen = visibleWhen,
            LabelFor = labelFor,
        };

    public static ParameterDescriptor<TDefinition> OptionalNumber(
        string key,
        string label,
        Func<TDefinition, double> get,
        Action<TDefinition, double> set,
        Func<TDefinition, double> inheritedValue,
        string? help = null,
        int decimalPlaces = 3,
        ParameterUnit unit = ParameterUnit.None,
        Func<TDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ParameterKind.OptionalNumber,
            Key = key,
            Label = label,
            GetNumber = get,
            SetNumber = set,
            InheritedValue = inheritedValue,
            Help = help,
            Unit = unit,
            DecimalPlaces = decimalPlaces,
            VisibleWhen = visibleWhen,
        };

    public static ParameterDescriptor<TDefinition> Slider(
        string key,
        string label,
        Func<TDefinition, double> get,
        Action<TDefinition, double> set,
        double softMin,
        double softMax,
        string? help = null,
        double? hardMin = null,
        double? hardMax = null,
        int decimalPlaces = 3,
        double? step = null,
        ParameterUnit unit = ParameterUnit.None,
        bool liveScrub = false,
        Func<TDefinition, bool>? visibleWhen = null,
        Func<TDefinition, string>? labelFor = null) =>
        new()
        {
            Kind = ParameterKind.Slider,
            Key = key,
            Label = label,
            GetNumber = get,
            SetNumber = set,
            Unit = unit,
            SoftMin = softMin,
            SoftMax = softMax,
            Min = hardMin,
            Max = hardMax,
            DecimalPlaces = decimalPlaces,
            Step = step,
            LiveScrub = liveScrub,
            VisibleWhen = visibleWhen,
            LabelFor = labelFor,
            Help = help,
        };

    /// <summary>
    /// A grading slope stored as an angle in degrees. Identical to <see cref="Number"/> apart from the
    /// unit: the row shows and parses it in whatever slope unit the user works in, so the same field
    /// takes 25%, 1:3 or 14° and the definition still persists degrees.
    /// </summary>
    public static ParameterDescriptor<TDefinition> Slope(
        string key,
        string label,
        Func<TDefinition, double> get,
        Action<TDefinition, double> set,
        string? help = null,
        bool rebuildAfterCommit = false,
        Func<TDefinition, bool>? visibleWhen = null) =>
        Number(
            key,
            label,
            get,
            set,
            help,
            min: 0,
            max: MoleHill.Core.Analysis.SlopeInput.MaxSlopeDegrees,
            unit: ParameterUnit.Slope,
            rebuildAfterCommit: rebuildAfterCommit,
            visibleWhen: visibleWhen);

    /// <summary>An inherit-when-blank slope — the cut-slope overrides, which fall back to the fill slope.</summary>
    public static ParameterDescriptor<TDefinition> OptionalSlope(
        string key,
        string label,
        Func<TDefinition, double> get,
        Action<TDefinition, double> set,
        Func<TDefinition, double> inheritedValue,
        string? help = null,
        Func<TDefinition, bool>? visibleWhen = null) =>
        OptionalNumber(
            key,
            label,
            get,
            set,
            inheritedValue,
            help,
            unit: ParameterUnit.Slope,
            visibleWhen: visibleWhen);

    /// <summary>
    /// A slope on a slider. The slider track stays linear in degrees over its 0-90 domain — that is the
    /// only slope unit with a bounded axis to drag along, since percent and ratio both run to infinity —
    /// while the value box beside it reads and accepts the user's unit like any other slope field.
    /// </summary>
    public static ParameterDescriptor<TDefinition> SlopeSlider(
        string key,
        string label,
        Func<TDefinition, double> get,
        Action<TDefinition, double> set,
        string? help = null,
        double softMin = 0.0,
        double softMax = MoleHill.Core.Analysis.SlopeInput.MaxSlopeDegrees,
        bool liveScrub = false,
        Func<TDefinition, bool>? visibleWhen = null) =>
        Slider(
            key,
            label,
            get,
            set,
            softMin,
            softMax,
            help,
            hardMin: 0.0,
            // Not a round 90: tan(90 deg) is ~1.6e16, so a value box showing this in percent, promille
            // or 1:n would read as an astronomical number the user cannot type back in — SlopeInput
            // rejects anything steeper than this on parse. The bound keeps display and entry symmetric.
            hardMax: MoleHill.Core.Analysis.SlopeInput.MaxSlopeDegrees,
            unit: ParameterUnit.Slope,
            liveScrub: liveScrub,
            visibleWhen: visibleWhen);

    public static ParameterDescriptor<TDefinition> Bool(
        string key,
        string label,
        Func<TDefinition, bool> get,
        Action<TDefinition, bool> set,
        string? help = null,
        bool incrementalCommit = false,
        bool rebuildAfterCommit = false,
        Func<TDefinition, bool>? visibleWhen = null,
        bool refreshOnly = false) =>
        new()
        {
            Kind = ParameterKind.Bool,
            Key = key,
            Label = label,
            GetBool = get,
            SetBool = set,
            Help = help,
            RefreshOnly = refreshOnly,
            IncrementalCommit = incrementalCommit,
            RebuildAfterCommit = rebuildAfterCommit,
            VisibleWhen = visibleWhen,
        };

    public static ParameterDescriptor<TDefinition> Choice(
        string key,
        string label,
        IReadOnlyList<(string Key, string Label)>? options,
        Func<TDefinition, string?> get,
        Action<TDefinition, string?> set,
        string? help = null,
        bool refreshOnly = false,
        bool incrementalCommit = false,
        bool rebuildAfterCommit = false,
        Func<TDefinition, bool>? visibleWhen = null,
        Func<TDefinition, IReadOnlyList<(string Key, string Label)>>? optionsFor = null) =>
        new()
        {
            Kind = ParameterKind.Choice,
            Key = key,
            Label = label,
            ChoiceOptions = options,
            ChoiceOptionsFor = optionsFor,
            GetText = get,
            SetText = set,
            Help = help,
            RefreshOnly = refreshOnly,
            IncrementalCommit = incrementalCommit,
            RebuildAfterCommit = rebuildAfterCommit,
            VisibleWhen = visibleWhen,
        };

    public static ParameterDescriptor<TDefinition> Color(
        string key,
        string label,
        Func<TDefinition, int?> get,
        Action<TDefinition, int?> set,
        string? help = null,
        Func<TerrainDefinition, TDefinition, int?>? fallbackColor = null,
        Func<TerrainDefinition, TDefinition, string>? defaultText = null,
        bool incrementalCommit = false,
        Func<TDefinition, bool>? visibleWhen = null,
        bool refreshOnly = false) =>
        new()
        {
            Kind = ParameterKind.Color,
            Key = key,
            Label = label,
            GetColor = get,
            SetColor = set,
            FallbackColor = fallbackColor,
            ColorDefaultTextFor = defaultText,
            Help = help,
            RefreshOnly = refreshOnly,
            IncrementalCommit = incrementalCommit,
            VisibleWhen = visibleWhen,
        };

    /// <summary>
    /// The colour-ramp card: stops, linear/stepped mode, band interval, and the mapped range, in one
    /// control. Needs no accessors — every field it edits lives on the definition itself, so declaring
    /// this on a type is the whole of "this analysis is colour-mapped".
    ///
    /// Always a refresh-only commit: colour is a display concern, and re-running the analysis to change a
    /// swatch would make dragging a stop unusable.
    /// </summary>
    public static ParameterDescriptor<TDefinition> ColorRamp(string? help = null) =>
        new()
        {
            Kind = ParameterKind.ColorRamp,
            Key = "ColorRamp",
            Label = "Colour",
            Help = help,
            RefreshOnly = true,
        };

    public static ParameterDescriptor<TDefinition> Text(
        string key,
        string label,
        Func<TDefinition, string?> get,
        Action<TDefinition, string?> set,
        string? help = null,
        bool trim = true,
        Func<TDefinition, bool>? visibleWhen = null,
        bool refreshOnly = false) =>
        new()
        {
            Kind = ParameterKind.Text,
            Key = key,
            Label = label,
            GetText = get,
            SetText = set,
            TrimText = trim,
            Help = help,
            RefreshOnly = refreshOnly,
            VisibleWhen = visibleWhen,
        };

    public static ParameterDescriptor<TDefinition> ReadOnly(
        string key,
        string label,
        Func<TDefinition, string> get,
        string? help = null,
        Func<TDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ParameterKind.ReadOnly,
            Key = key,
            Label = label,
            GetReadOnly = get,
            Help = help,
            VisibleWhen = visibleWhen,
        };

    /// <summary>
    /// The weighted block-mix card, declared by Scatter alone. Like <see cref="ColorRamp"/> it needs no
    /// accessors — it edits the definition's own block list — and the panel renders it through the row
    /// builder's bespoke hook.
    /// </summary>
    /// <summary>
    /// The ramp going table, declared by gradient compliance alone. Like <see cref="ColorRamp"/> it needs
    /// no accessors: it edits the definition's rule set directly, through the row builder's bespoke hook.
    /// </summary>
    public static ParameterDescriptor<TDefinition> GoingTable(
        string key,
        string label,
        string? help = null,
        Func<TDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ParameterKind.GoingTable,
            Key = key,
            Label = label,
            Help = help,
            VisibleWhen = visibleWhen,
        };

    /// <summary>
    /// The block an annotation draws at each label. Empty means the built-in block for
    /// <paramref name="template"/>; otherwise it is the name of a block definition in the document.
    /// </summary>
    public static ParameterDescriptor<TDefinition> BlockPicker(
        string key,
        string label,
        Func<TDefinition, string?> get,
        Action<TDefinition, string?> set,
        MarkerBlockTemplate template,
        string? help = null,
        Func<TDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ParameterKind.BlockPicker,
            Key = key,
            Label = label,
            GetText = get,
            SetText = set,
            BlockTemplate = template,
            Help = help,
            VisibleWhen = visibleWhen,
        };

    public static ParameterDescriptor<TDefinition> BlockMix(
        string key,
        string label,
        string? help = null,
        Func<TDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ParameterKind.BlockMix,
            Key = key,
            Label = label,
            Help = help,
            VisibleWhen = visibleWhen,
        };
}
