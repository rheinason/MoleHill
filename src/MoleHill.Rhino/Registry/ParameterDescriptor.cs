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
    /// Not an input: the heading of a collapsible group of rows (Settings, Boundaries, Variation). The rows
    /// declared after it, up to the next section, belong to it. Whether it is open is a per-user preference
    /// per card type, so a section changes nothing a build reads.
    /// </summary>
    Section,

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

    /// <summary>
    /// A point in plan (world X and Y), picked in the viewport: a Pick Point pill, a Picked pill (click to
    /// pick again, coordinates in its tooltip) and an x that returns it to world 0,0. The Rhino prompt
    /// also takes typed coordinates. Design review Step 53.
    /// </summary>
    PlanPoint,

    /// <summary>
    /// A file on disk: its full path in a text box beside a Browse button. The path lives in the text
    /// accessors; <see cref="ParameterDescriptor{TDefinition}.FileFilterName"/> and
    /// <see cref="ParameterDescriptor{TDefinition}.FileExtensions"/> narrow the browse dialog.
    /// </summary>
    FilePath,
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

    /// <summary>OptionalNumber: an inherited value that needs the terrain, such as a size the last build
    /// resolved and stored in its summary. Wins over <see cref="InheritedValue"/> when set; 0 shows no
    /// placeholder (nothing resolved yet).</summary>
    public Func<TerrainDefinition, TDefinition, double>? InheritedValueFromTerrain { get; init; }

    /// <summary>
    /// Whether the row means anything on this terrain (design review Step 38: a report section with nothing
    /// to report). A row that does not is hidden, and returns as soon as it applies; use it only for a row
    /// whose value has no effect while it does not apply, so hiding it never hides a working setting.
    /// </summary>
    public Func<TerrainDefinition, bool>? AppliesTo { get; init; }

    /// <summary>A <see cref="ParameterKind.PlanPoint"/> row's world X and Y.</summary>
    public Func<TDefinition, (double X, double Y)>? GetPlanPoint { get; init; }

    /// <summary>Stores a picked (or cleared, 0,0) <see cref="ParameterKind.PlanPoint"/>.</summary>
    public Action<TDefinition, double, double>? SetPlanPoint { get; init; }

    /// <summary>Greyed text an empty Text field shows: what leaving it empty does (a legend's automatic title).</summary>
    public Func<TerrainDefinition, TDefinition, string?>? PlaceholderFor { get; init; }

    /// <summary>What an optional number's blank field says when nothing is inherited ("Auto", "No limit").</summary>
    public string BlankText { get; init; } = "Auto";

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

    /// <summary>
    /// Keeps a fixed 2–3 option choice as a drop-down instead of segments, for options whose labels are too
    /// long to sit side by side. Every other fixed choice of two or three modes is shown segmented.
    /// </summary>
    public bool PreferDropDown { get; init; }

    /// <summary>
    /// Shows a choice whose options depend on the card (<see cref="ChoiceOptionsFor"/>) as segments when it
    /// has two or three. Such lists are drop-downs by default, because most of them follow the document
    /// (terrains, presets); a mode whose options merely vary with the card opts in.
    /// </summary>
    public bool PreferSegments { get; init; }

    /// <summary>
    /// What each option does, by option key: shown as the tooltip of its segment, so a mode's trade-off is
    /// explained where it is picked (the row's <see cref="Help"/> still describes the choice as a whole).
    /// </summary>
    public IReadOnlyDictionary<string, string>? ChoiceOptionHelp { get; init; }

    /// <summary>
    /// A card's primary mode: the choice takes the whole row with no field label beside it (design review
    /// Step 37), so its segments get the full card width.
    /// </summary>
    public bool FullRow { get; init; }

    // ---- Section ----

    /// <summary>Whether a section starts open before the user has opened or closed it.</summary>
    public bool ExpandedByDefault { get; init; }

    /// <summary>Short state shown after the section title ("2 active"), so a closed section still says
    /// whether anything inside it is set.</summary>
    public Func<TDefinition, string?>? SummaryFor { get; init; }

    // ---- Color ----
    // Fallback and default text take the owning terrain as well as the definition: a colour row resolves
    // "by layer" against the item's own output layer, falling back to the terrain's layer for the family.

    public Func<TDefinition, int?>? GetColor { get; init; }
    public Action<TDefinition, int?>? SetColor { get; init; }
    public Func<TerrainDefinition, TDefinition, int?>? FallbackColor { get; init; }
    public Func<TerrainDefinition, TDefinition, string>? ColorDefaultTextFor { get; init; }

    // ---- Text ----

    public bool TrimText { get; init; } = true;

    // ---- FilePath ----

    /// <summary>The browse dialog's filter name ("Images").</summary>
    public string? FileFilterName { get; init; }

    /// <summary>The extensions the browse dialog offers, with their dots (".png").</summary>
    public IReadOnlyList<string>? FileExtensions { get; init; }

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
        Func<TDefinition, bool>? visibleWhen = null,
        Func<TerrainDefinition, TDefinition, double>? inheritedValueFromTerrain = null,
        string blankText = "Auto") =>
        new()
        {
            Kind = ParameterKind.OptionalNumber,
            BlankText = blankText,
            Key = key,
            Label = label,
            GetNumber = get,
            SetNumber = set,
            InheritedValue = inheritedValue,
            InheritedValueFromTerrain = inheritedValueFromTerrain,
            Help = help,
            Unit = unit,
            DecimalPlaces = decimalPlaces,
            VisibleWhen = visibleWhen,
        };

    /// <summary>A plan point picked in the viewport; 0,0 reads as not picked (design review Step 53).</summary>
    public static ParameterDescriptor<TDefinition> PlanPoint(
        string key,
        string label,
        Func<TDefinition, (double X, double Y)> get,
        Action<TDefinition, double, double> set,
        string? help = null,
        Func<TDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ParameterKind.PlanPoint,
            Key = key,
            Label = label,
            GetPlanPoint = get,
            SetPlanPoint = set,
            Help = help,
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
        bool refreshOnly = false,
        Func<TDefinition, string>? labelFor = null,
        Func<TerrainDefinition, bool>? appliesTo = null) =>
        new()
        {
            Kind = ParameterKind.Bool,
            LabelFor = labelFor,
            AppliesTo = appliesTo,
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
        Func<TDefinition, IReadOnlyList<(string Key, string Label)>>? optionsFor = null,
        bool preferDropDown = false,
        IReadOnlyDictionary<string, string>? optionHelp = null,
        bool fullRow = false,
        bool segmented = false) =>
        new()
        {
            Kind = ParameterKind.Choice,
            PreferDropDown = preferDropDown,
            PreferSegments = segmented,
            FullRow = fullRow,
            ChoiceOptionHelp = optionHelp,
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

    /// <summary>
    /// Opens a collapsible section titled <paramref name="title"/>; the rows declared after it, up to the
    /// next section, belong to it. <paramref name="key"/> names it in the per-user open/closed preference.
    /// </summary>
    public static ParameterDescriptor<TDefinition> Section(
        string key,
        string title,
        bool expanded = false,
        Func<TDefinition, string?>? summary = null,
        Func<TDefinition, bool>? visibleWhen = null,
        Func<TDefinition, string>? titleFor = null) =>
        new()
        {
            Kind = ParameterKind.Section,
            Key = key,
            Label = title,
            LabelFor = titleFor,
            ExpandedByDefault = expanded,
            SummaryFor = summary,
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
    public static ParameterDescriptor<TDefinition> ColorRamp(string? help = null, Func<TDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ParameterKind.ColorRamp,
            Key = "ColorRamp",
            Label = "Colour",
            Help = help,
            RefreshOnly = true,
            VisibleWhen = visibleWhen,
        };

    public static ParameterDescriptor<TDefinition> Text(
        string key,
        string label,
        Func<TDefinition, string?> get,
        Action<TDefinition, string?> set,
        string? help = null,
        bool trim = true,
        Func<TDefinition, bool>? visibleWhen = null,
        bool refreshOnly = false,
        Func<TerrainDefinition, TDefinition, string?>? placeholder = null) =>
        new()
        {
            Kind = ParameterKind.Text,
            PlaceholderFor = placeholder,
            Key = key,
            Label = label,
            GetText = get,
            SetText = set,
            TrimText = trim,
            Help = help,
            RefreshOnly = refreshOnly,
            VisibleWhen = visibleWhen,
        };

    /// <summary>A file path typed or browsed to. Stored as the full path in the text accessors.</summary>
    public static ParameterDescriptor<TDefinition> FilePath(
        string key,
        string label,
        Func<TDefinition, string?> get,
        Action<TDefinition, string?> set,
        string filterName,
        IReadOnlyList<string> extensions,
        string? help = null,
        Func<TDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ParameterKind.FilePath,
            Key = key,
            Label = label,
            GetText = get,
            SetText = set,
            FileFilterName = filterName,
            FileExtensions = extensions,
            Help = help,
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
