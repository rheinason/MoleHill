using System.Collections.Generic;
using MoleHill.Rhino.Model;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// One declarative parameter on an analysis/annotation type — the analysis-side counterpart of
/// <see cref="ParameterDescriptor"/>. Drives the schema card builder in the panel; kept separate from
/// the modifier descriptor because accessors are typed against <see cref="AnalysisDefinition"/> instead
/// of <see cref="ModifierDefinition"/>. Shares <see cref="ParameterKind"/> since the editor vocabulary is
/// the same.
/// </summary>
internal sealed class AnalysisParameterDescriptor
{
    public required ParameterKind Kind { get; init; }

    /// <summary>Property name on the definition — stable id for GH params and debugging.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }
    public string? Help { get; init; }

    /// <summary>Overrides <see cref="Label"/> when the row label depends on live definition state
    /// (e.g. "Low %" vs "Low deg" once the slope unit is changed).</summary>
    public Func<AnalysisDefinition, string>? LabelFor { get; init; }

    // Numeric
    public int DecimalPlaces { get; init; } = 3;
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }

    /// <summary>Commit as a cheap preview refresh (recolor/re-legend) instead of scheduling a full analysis rebuild.</summary>
    public bool RefreshOnly { get; init; }

    /// <summary>Commit without a full rebuild, then run the type's own incremental rebuild (contour-style: cheaper
    /// than reprocessing every analysis). Dispatched by kind in the schema row builder.</summary>
    public bool IncrementalCommit { get; init; }

    // Sources
    public RhinoObjectType ObjectFilter { get; init; }

    // Choice
    public IReadOnlyList<(string Key, string Label)>? ChoiceOptions { get; init; }

    /// <summary>Overrides <see cref="ChoiceOptions"/> when the option list depends on live state (e.g. the
    /// value-format dropdown appends a "Custom" entry for a format string outside the standard presets).</summary>
    public Func<AnalysisDefinition, IReadOnlyList<(string Key, string Label)>>? ChoiceOptionsFor { get; init; }

    // Color. Fallback/default-text take the owning terrain too: color rows resolve "by layer" against the
    // analysis's own output layer, falling back to the terrain's annotation layer.
    public Func<AnalysisDefinition, int?>? GetColor { get; init; }
    public Action<AnalysisDefinition, int?>? SetColor { get; init; }
    public Func<TerrainDefinition, AnalysisDefinition, int?>? FallbackColor { get; init; }
    public Func<TerrainDefinition, AnalysisDefinition, string>? ColorDefaultTextFor { get; init; }

    // Text
    public bool TrimText { get; init; } = true;

    // Accessors (the relevant pair for this kind is set)
    public Func<AnalysisDefinition, double>? GetNumber { get; init; }
    public Action<AnalysisDefinition, double>? SetNumber { get; init; }
    public Func<AnalysisDefinition, bool>? GetBool { get; init; }
    public Action<AnalysisDefinition, bool>? SetBool { get; init; }
    public Func<AnalysisDefinition, string?>? GetText { get; init; }
    public Action<AnalysisDefinition, string?>? SetText { get; init; }
    public Func<AnalysisDefinition, SourceReferenceSet>? GetSources { get; init; }
    public Func<AnalysisDefinition, string>? GetReadOnly { get; init; }

    public static AnalysisParameterDescriptor Sources(
        string key,
        string label,
        Func<AnalysisDefinition, SourceReferenceSet> get,
        RhinoObjectType objectFilter,
        string? help = null) =>
        new()
        {
            Kind = ParameterKind.Sources,
            Key = key,
            Label = label,
            GetSources = get,
            ObjectFilter = objectFilter,
            Help = help,
        };

    public static AnalysisParameterDescriptor Number(
        string key,
        string label,
        Func<AnalysisDefinition, double> get,
        Action<AnalysisDefinition, double> set,
        string? help = null,
        double? min = 0,
        double? max = null,
        int decimalPlaces = 3,
        bool refreshOnly = false,
        bool incrementalCommit = false,
        Func<AnalysisDefinition, string>? labelFor = null) =>
        new()
        {
            Kind = ParameterKind.Number,
            Key = key,
            Label = label,
            LabelFor = labelFor,
            GetNumber = get,
            SetNumber = set,
            Help = help,
            Min = min,
            Max = max,
            DecimalPlaces = decimalPlaces,
            RefreshOnly = refreshOnly,
            IncrementalCommit = incrementalCommit,
        };

    /// <summary>
    /// The colour-ramp card: stops, linear/stepped mode, band interval, and the mapped range, in one
    /// control. Needs no accessors — every field it edits lives on <see cref="AnalysisDefinition"/> itself,
    /// so declaring this on a type is the whole of "this analysis is colour-mapped".
    ///
    /// Always a refresh-only commit: colour is a display concern, and re-running the analysis to change a
    /// swatch would make dragging a stop unusable.
    /// </summary>
    public static AnalysisParameterDescriptor ColorRamp(string? help = null) =>
        new()
        {
            Kind = ParameterKind.ColorRamp,
            Key = "ColorRamp",
            Label = "Colour",
            Help = help,
            RefreshOnly = true,
        };

    public static AnalysisParameterDescriptor Bool(
        string key,
        string label,
        Func<AnalysisDefinition, bool> get,
        Action<AnalysisDefinition, bool> set,
        string? help = null,
        bool incrementalCommit = false) =>
        new()
        {
            Kind = ParameterKind.Bool,
            Key = key,
            Label = label,
            GetBool = get,
            SetBool = set,
            Help = help,
            IncrementalCommit = incrementalCommit,
        };

    public static AnalysisParameterDescriptor Layer(
        string key,
        string label,
        Func<AnalysisDefinition, string?> get,
        Action<AnalysisDefinition, string?> set,
        string? help = null,
        bool incrementalCommit = false) =>
        new()
        {
            Kind = ParameterKind.Layer,
            Key = key,
            Label = label,
            GetText = get,
            SetText = set,
            Help = help,
            IncrementalCommit = incrementalCommit,
        };

    public static AnalysisParameterDescriptor Choice(
        string key,
        string label,
        IReadOnlyList<(string Key, string Label)>? options,
        Func<AnalysisDefinition, string?> get,
        Action<AnalysisDefinition, string?> set,
        string? help = null,
        bool refreshOnly = false,
        bool incrementalCommit = false,
        Func<AnalysisDefinition, IReadOnlyList<(string Key, string Label)>>? optionsFor = null) =>
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
        };

    public static AnalysisParameterDescriptor Color(
        string key,
        string label,
        Func<AnalysisDefinition, int?> get,
        Action<AnalysisDefinition, int?> set,
        string? help = null,
        Func<TerrainDefinition, AnalysisDefinition, int?>? fallbackColor = null,
        Func<TerrainDefinition, AnalysisDefinition, string>? defaultText = null,
        bool incrementalCommit = false) =>
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
            IncrementalCommit = incrementalCommit,
        };

    public static AnalysisParameterDescriptor Text(
        string key,
        string label,
        Func<AnalysisDefinition, string?> get,
        Action<AnalysisDefinition, string?> set,
        string? help = null,
        bool trim = true) =>
        new()
        {
            Kind = ParameterKind.Text,
            Key = key,
            Label = label,
            GetText = get,
            SetText = set,
            TrimText = trim,
            Help = help,
        };

    public static AnalysisParameterDescriptor ReadOnly(
        string key,
        string label,
        Func<AnalysisDefinition, string> get,
        string? help = null) =>
        new()
        {
            Kind = ParameterKind.ReadOnly,
            Key = key,
            Label = label,
            GetReadOnly = get,
            Help = help,
        };
}
