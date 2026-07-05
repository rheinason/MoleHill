using System.Collections.Generic;
using MoleHill.Rhino.Model;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

/// <summary>Editor kind a <see cref="ParameterDescriptor"/> maps to (Eto card row today, GH param later).</summary>
internal enum ParameterKind
{
    Sources,
    Number,
    OptionalNumber,
    Slider,
    Bool,
    Layer,
    ReadOnly,
    Choice,
    Color,
    Text,
}

/// <summary>Semantic unit for a numeric parameter — drives suffix labels and formatting once threaded through.</summary>
internal enum ParameterUnit
{
    None,
    Length,
    Angle,
    Percent,
    Factor,
    Count,
}

/// <summary>
/// One declarative parameter on a modifier type — the Blender-style "the type declares its inputs and the
/// UI is generated" unit. A descriptor's ordered <see cref="ModifierTypeDescriptor.Parameters"/> drives the
/// panel card (via the schema card builder) and is the shared contract that will also generate the
/// Grasshopper component. Values are read/written through typed accessor delegates against the concrete
/// definition (cast inside, mirroring the existing hand-written card mutations); the panel wraps each
/// setter in its <c>MutateModifier</c> so caching/rebuild/save behavior is unchanged.
/// </summary>
internal sealed class ParameterDescriptor
{
    public required ParameterKind Kind { get; init; }

    /// <summary>Property name on the definition — stable id for GH params and debugging.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }
    public string? Help { get; init; }

    // Numeric (Number / OptionalNumber / Slider)
    public int DecimalPlaces { get; init; } = 3;
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double SoftMin { get; init; }
    public double SoftMax { get; init; }

    /// <summary>Stepper/slider increment. Defaults from <see cref="DecimalPlaces"/> when unset (0 decimals → 1, else 0.1).</summary>
    public double? Step { get; init; }

    /// <summary>Semantic unit — drives suffix labels/defaults once threaded through the editors.</summary>
    public ParameterUnit Unit { get; init; } = ParameterUnit.None;

    /// <summary>Slider: defer document save + suppress UI refresh while scrubbing (live drag).</summary>
    public bool LiveScrub { get; init; }

    /// <summary>OptionalNumber: the value shown as the greyed "inherited" placeholder when blank.</summary>
    public Func<ModifierDefinition, double>? InheritedValue { get; init; }

    // Sources
    public RhinoObjectType ObjectFilter { get; init; }

    // Choice
    public IReadOnlyList<(string Key, string Label)>? ChoiceOptions { get; init; }

    // Color
    public Func<ModifierDefinition, int?>? GetColor { get; init; }
    public Action<ModifierDefinition, int?>? SetColor { get; init; }
    public Func<ModifierDefinition, int?>? FallbackColor { get; init; }
    public string ColorDefaultText { get; init; } = "(by layer)";

    // Text
    public bool TrimText { get; init; } = true;

    // Accessors (the relevant pair for this kind is set)
    public Func<ModifierDefinition, double>? GetNumber { get; init; }
    public Action<ModifierDefinition, double>? SetNumber { get; init; }
    public Func<ModifierDefinition, bool>? GetBool { get; init; }
    public Action<ModifierDefinition, bool>? SetBool { get; init; }
    public Func<ModifierDefinition, string?>? GetText { get; init; }
    public Action<ModifierDefinition, string?>? SetText { get; init; }
    public Func<ModifierDefinition, SourceReferenceSet>? GetSources { get; init; }
    public Func<ModifierDefinition, string>? GetReadOnly { get; init; }

    public static ParameterDescriptor Sources(
        string key,
        string label,
        Func<ModifierDefinition, SourceReferenceSet> get,
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

    public static ParameterDescriptor Number(
        string key,
        string label,
        Func<ModifierDefinition, double> get,
        Action<ModifierDefinition, double> set,
        string? help = null,
        double? min = 0,
        double? max = null,
        int decimalPlaces = 3) =>
        new()
        {
            Kind = ParameterKind.Number,
            Key = key,
            Label = label,
            GetNumber = get,
            SetNumber = set,
            Help = help,
            Min = min,
            Max = max,
            DecimalPlaces = decimalPlaces,
        };

    public static ParameterDescriptor OptionalNumber(
        string key,
        string label,
        Func<ModifierDefinition, double> get,
        Action<ModifierDefinition, double> set,
        Func<ModifierDefinition, double> inheritedValue,
        string? help = null,
        int decimalPlaces = 3) =>
        new()
        {
            Kind = ParameterKind.OptionalNumber,
            Key = key,
            Label = label,
            GetNumber = get,
            SetNumber = set,
            InheritedValue = inheritedValue,
            Help = help,
            DecimalPlaces = decimalPlaces,
        };

    public static ParameterDescriptor Slider(
        string key,
        string label,
        Func<ModifierDefinition, double> get,
        Action<ModifierDefinition, double> set,
        double softMin,
        double softMax,
        string? help = null,
        double? hardMin = null,
        double? hardMax = null,
        int decimalPlaces = 3,
        bool liveScrub = false) =>
        new()
        {
            Kind = ParameterKind.Slider,
            Key = key,
            Label = label,
            GetNumber = get,
            SetNumber = set,
            SoftMin = softMin,
            SoftMax = softMax,
            Min = hardMin,
            Max = hardMax,
            DecimalPlaces = decimalPlaces,
            LiveScrub = liveScrub,
            Help = help,
        };

    public static ParameterDescriptor Bool(
        string key,
        string label,
        Func<ModifierDefinition, bool> get,
        Action<ModifierDefinition, bool> set,
        string? help = null) =>
        new()
        {
            Kind = ParameterKind.Bool,
            Key = key,
            Label = label,
            GetBool = get,
            SetBool = set,
            Help = help,
        };

    public static ParameterDescriptor Layer(
        string key,
        string label,
        Func<ModifierDefinition, string?> get,
        Action<ModifierDefinition, string?> set,
        string? help = null) =>
        new()
        {
            Kind = ParameterKind.Layer,
            Key = key,
            Label = label,
            GetText = get,
            SetText = set,
            Help = help,
        };

    public static ParameterDescriptor Choice(
        string key,
        string label,
        IReadOnlyList<(string Key, string Label)> options,
        Func<ModifierDefinition, string?> get,
        Action<ModifierDefinition, string?> set,
        string? help = null) =>
        new()
        {
            Kind = ParameterKind.Choice,
            Key = key,
            Label = label,
            ChoiceOptions = options,
            GetText = get,
            SetText = set,
            Help = help,
        };

    public static ParameterDescriptor Color(
        string key,
        string label,
        Func<ModifierDefinition, int?> get,
        Action<ModifierDefinition, int?> set,
        string? help = null,
        Func<ModifierDefinition, int?>? fallbackColor = null,
        string defaultText = "(by layer)") =>
        new()
        {
            Kind = ParameterKind.Color,
            Key = key,
            Label = label,
            GetColor = get,
            SetColor = set,
            FallbackColor = fallbackColor,
            ColorDefaultText = defaultText,
            Help = help,
        };

    public static ParameterDescriptor Text(
        string key,
        string label,
        Func<ModifierDefinition, string?> get,
        Action<ModifierDefinition, string?> set,
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

    public static ParameterDescriptor ReadOnly(
        string key,
        string label,
        Func<ModifierDefinition, string> get,
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
