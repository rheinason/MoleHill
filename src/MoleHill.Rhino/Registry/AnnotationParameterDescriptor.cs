using System.Collections.Generic;
using MoleHill.Rhino.Model;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// One declarative parameter on an annotation type — the annotation-side counterpart of
/// <see cref="AnalysisParameterDescriptor"/>. Drives the schema card builder in the panel; kept separate
/// because accessors are typed against <see cref="AnnotationDefinition"/> instead of
/// <see cref="AnalysisDefinition"/>. Shares <see cref="ParameterKind"/> since the editor vocabulary is
/// the same, minus <c>ColorRamp</c> — annotations draw, they do not colour-map.
/// </summary>
internal sealed class AnnotationParameterDescriptor
{
    public required ParameterKind Kind { get; init; }

    /// <summary>Property name on the definition — stable id for GH params and debugging.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }
    public string? Help { get; init; }

    /// <summary>Overrides <see cref="Label"/> when the row label depends on live definition state
    /// (e.g. "Low %" vs "Low deg" once the slope unit is changed).</summary>
    public Func<AnnotationDefinition, string>? LabelFor { get; init; }

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
    public Func<AnnotationDefinition, IReadOnlyList<(string Key, string Label)>>? ChoiceOptionsFor { get; init; }

    // Color. Fallback/default-text take the owning terrain too: color rows resolve "by layer" against the
    // analysis's own output layer, falling back to the terrain's annotation layer.
    public Func<AnnotationDefinition, int?>? GetColor { get; init; }
    public Action<AnnotationDefinition, int?>? SetColor { get; init; }
    public Func<TerrainDefinition, AnnotationDefinition, int?>? FallbackColor { get; init; }
    public Func<TerrainDefinition, AnnotationDefinition, string>? ColorDefaultTextFor { get; init; }

    // Text
    public bool TrimText { get; init; } = true;

    // Accessors (the relevant pair for this kind is set)
    public Func<AnnotationDefinition, double>? GetNumber { get; init; }
    public Action<AnnotationDefinition, double>? SetNumber { get; init; }
    public Func<AnnotationDefinition, bool>? GetBool { get; init; }
    public Action<AnnotationDefinition, bool>? SetBool { get; init; }
    public Func<AnnotationDefinition, string?>? GetText { get; init; }
    public Action<AnnotationDefinition, string?>? SetText { get; init; }
    public Func<AnnotationDefinition, SourceReferenceSet>? GetSources { get; init; }
    public Func<AnnotationDefinition, string>? GetReadOnly { get; init; }

    public static AnnotationParameterDescriptor Sources(
        string key,
        string label,
        Func<AnnotationDefinition, SourceReferenceSet> get,
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

    public static AnnotationParameterDescriptor Number(
        string key,
        string label,
        Func<AnnotationDefinition, double> get,
        Action<AnnotationDefinition, double> set,
        string? help = null,
        double? min = 0,
        double? max = null,
        int decimalPlaces = 3,
        bool refreshOnly = false,
        bool incrementalCommit = false,
        Func<AnnotationDefinition, string>? labelFor = null) =>
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

    public static AnnotationParameterDescriptor Bool(
        string key,
        string label,
        Func<AnnotationDefinition, bool> get,
        Action<AnnotationDefinition, bool> set,
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

    public static AnnotationParameterDescriptor Choice(
        string key,
        string label,
        IReadOnlyList<(string Key, string Label)>? options,
        Func<AnnotationDefinition, string?> get,
        Action<AnnotationDefinition, string?> set,
        string? help = null,
        bool refreshOnly = false,
        bool incrementalCommit = false,
        Func<AnnotationDefinition, IReadOnlyList<(string Key, string Label)>>? optionsFor = null) =>
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

    public static AnnotationParameterDescriptor Color(
        string key,
        string label,
        Func<AnnotationDefinition, int?> get,
        Action<AnnotationDefinition, int?> set,
        string? help = null,
        Func<TerrainDefinition, AnnotationDefinition, int?>? fallbackColor = null,
        Func<TerrainDefinition, AnnotationDefinition, string>? defaultText = null,
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

    public static AnnotationParameterDescriptor Text(
        string key,
        string label,
        Func<AnnotationDefinition, string?> get,
        Action<AnnotationDefinition, string?> set,
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

    public static AnnotationParameterDescriptor ReadOnly(
        string key,
        string label,
        Func<AnnotationDefinition, string> get,
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
