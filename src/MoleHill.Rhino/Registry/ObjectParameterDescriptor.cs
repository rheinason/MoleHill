using System;
using System.Collections.Generic;
using System.Linq;
using MoleHill.Rhino.Model;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal enum ObjectParameterKind
{
    Sources,
    Number,
    Slider,
    Bool,
    Choice,
    ReadOnly,
    BlockMix,
}

/// <summary>Descriptor vocabulary for generated terrain-object card rows.</summary>
internal sealed class ObjectParameterDescriptor
{
    public required ObjectParameterKind Kind { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? Help { get; init; }
    public Func<TerrainObjectDefinition, string>? LabelFor { get; init; }
    public bool RebuildAfterCommit { get; init; }
    public bool LiveEdit { get; init; }
    public bool LiveScrub { get; init; }
    public int DecimalPlaces { get; init; } = 3;
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double SoftMin { get; init; }
    public double SoftMax { get; init; }
    public double? Step { get; init; }
    public RhinoObjectType ObjectFilter { get; init; }
    public IReadOnlyList<(string Key, string Label)>? ChoiceOptions { get; init; }
    public Func<TerrainObjectDefinition, IReadOnlyList<(string Key, string Label)>>? ChoiceOptionsFor { get; init; }
    public Func<TerrainObjectDefinition, double>? GetNumber { get; init; }
    public Action<TerrainObjectDefinition, double>? SetNumber { get; init; }
    public Func<TerrainObjectDefinition, bool>? GetBool { get; init; }
    public Action<TerrainObjectDefinition, bool>? SetBool { get; init; }
    public Func<TerrainObjectDefinition, string?>? GetText { get; init; }
    public Action<TerrainObjectDefinition, string?>? SetText { get; init; }
    public Func<TerrainObjectDefinition, SourceReferenceSet>? GetSources { get; init; }
    public Func<TerrainObjectDefinition, string>? GetReadOnly { get; init; }
    public Func<TerrainObjectDefinition, bool>? VisibleWhen { get; init; }

    public static ObjectParameterDescriptor Sources(
        string key,
        string label,
        Func<TerrainObjectDefinition, SourceReferenceSet> get,
        RhinoObjectType objectFilter,
        string? help = null,
        Func<TerrainObjectDefinition, string>? labelFor = null) =>
        new()
        {
            Kind = ObjectParameterKind.Sources,
            Key = key,
            Label = label,
            GetSources = get,
            ObjectFilter = objectFilter,
            Help = help,
            LabelFor = labelFor,
        };

    public static ObjectParameterDescriptor Number(
        string key,
        string label,
        Func<TerrainObjectDefinition, double> get,
        Action<TerrainObjectDefinition, double> set,
        string? help = null,
        double? min = 0,
        double? max = null,
        int decimalPlaces = 3,
        double? step = null,
        bool liveEdit = false,
        bool liveScrub = false,
        bool rebuildAfterCommit = false,
        Func<TerrainObjectDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ObjectParameterKind.Number,
            Key = key,
            Label = label,
            GetNumber = get,
            SetNumber = set,
            Help = help,
            Min = min,
            Max = max,
            DecimalPlaces = decimalPlaces,
            Step = step,
            LiveEdit = liveEdit,
            LiveScrub = liveScrub,
            RebuildAfterCommit = rebuildAfterCommit,
            VisibleWhen = visibleWhen,
        };

    public static ObjectParameterDescriptor Slider(
        string key,
        string label,
        Func<TerrainObjectDefinition, double> get,
        Action<TerrainObjectDefinition, double> set,
        double softMin,
        double softMax,
        string? help = null,
        double? hardMin = null,
        double? hardMax = null,
        int decimalPlaces = 3,
        double? step = null,
        bool liveScrub = false,
        Func<TerrainObjectDefinition, bool>? visibleWhen = null) =>
        new()
        {
            Kind = ObjectParameterKind.Slider,
            Key = key,
            Label = label,
            GetNumber = get,
            SetNumber = set,
            SoftMin = softMin,
            SoftMax = softMax,
            Min = hardMin,
            Max = hardMax,
            DecimalPlaces = decimalPlaces,
            Step = step,
            LiveScrub = liveScrub,
            VisibleWhen = visibleWhen,
            Help = help,
        };

    public static ObjectParameterDescriptor Bool(
        string key,
        string label,
        Func<TerrainObjectDefinition, bool> get,
        Action<TerrainObjectDefinition, bool> set,
        string? help = null,
        bool rebuildAfterCommit = false) =>
        new()
        {
            Kind = ObjectParameterKind.Bool,
            Key = key,
            Label = label,
            GetBool = get,
            SetBool = set,
            Help = help,
            RebuildAfterCommit = rebuildAfterCommit,
        };

    public static ObjectParameterDescriptor Choice(
        string key,
        string label,
        IReadOnlyList<(string Key, string Label)> options,
        Func<TerrainObjectDefinition, string?> get,
        Action<TerrainObjectDefinition, string?> set,
        string? help = null,
        bool rebuildAfterCommit = false,
        Func<TerrainObjectDefinition, bool>? visibleWhen = null,
        Func<TerrainObjectDefinition, IReadOnlyList<(string Key, string Label)>>? choiceOptionsFor = null) =>
        new()
        {
            Kind = ObjectParameterKind.Choice,
            Key = key,
            Label = label,
            ChoiceOptions = options,
            GetText = get,
            SetText = set,
            Help = help,
            RebuildAfterCommit = rebuildAfterCommit,
            VisibleWhen = visibleWhen,
            ChoiceOptionsFor = choiceOptionsFor,
        };

    public static ObjectParameterDescriptor ReadOnly(
        string key,
        string label,
        Func<TerrainObjectDefinition, string> get,
        string? help = null) =>
        new()
        {
            Kind = ObjectParameterKind.ReadOnly,
            Key = key,
            Label = label,
            GetReadOnly = get,
            Help = help,
        };
}

internal static class ObjectParameterCatalog
{
    public static IReadOnlyList<ObjectParameterDescriptor> Common { get; } = new[]
    {
        ObjectParameterDescriptor.Sources(
            "sources",
            "Sources",
            definition => definition.Sources,
            0,
            "Rhino objects and layers whose instances should be placed."),
        ObjectParameterDescriptor.Slider(
            "randomRotationMinDegrees",
            "Rotate Min",
            definition => definition.RandomRotationMinDegrees,
            (definition, value) => definition.RandomRotationMinDegrees = value,
            0.0,
            360.0,
            "Minimum random rotation in degrees.",
            0.0,
            360.0,
            1,
            liveScrub: true),
        ObjectParameterDescriptor.Slider(
            "randomRotationMaxDegrees",
            "Rotate Max",
            definition => definition.RandomRotationMaxDegrees,
            (definition, value) => definition.RandomRotationMaxDegrees = value,
            0.0,
            360.0,
            "Maximum random rotation in degrees.",
            0.0,
            360.0,
            1,
            liveScrub: true),
        ObjectParameterDescriptor.Slider(
            "randomScaleMin",
            "Scale Min",
            definition => definition.RandomScaleMin,
            (definition, value) => definition.RandomScaleMin = value,
            0.25,
            2.0,
            "Minimum random uniform scale.",
            0.01,
            null,
            3,
            liveScrub: true),
        ObjectParameterDescriptor.Slider(
            "randomScaleMax",
            "Scale Max",
            definition => definition.RandomScaleMax,
            (definition, value) => definition.RandomScaleMax = value,
            0.25,
            2.0,
            "Maximum random uniform scale.",
            0.01,
            null,
            3,
            liveScrub: true),
        ObjectParameterDescriptor.Number(
            "randomSeed",
            "Seed",
            definition => definition.RandomSeed,
            (definition, value) => definition.RandomSeed = (int)Math.Round(value),
            "Stable random seed for this object card.",
            decimalPlaces: 0,
            liveEdit: true,
            liveScrub: true),
        ObjectParameterDescriptor.Number(
            "zOffset",
            "Z Offset",
            definition => definition.ZOffset,
            (definition, value) => definition.ZOffset = value,
            "Lift or sink placed objects along their placement up axis.",
            min: null,
            liveEdit: true,
            liveScrub: true),
        ObjectParameterDescriptor.ReadOnly(
            "bindings",
            "Bindings",
            definition => $"{definition.Sources.ObjectIds.Count + definition.Sources.LayerPaths.Count} source refs",
            "Explicit picks plus watched layers drive the objects in this definition."),
    };

    public static IReadOnlyList<ObjectParameterDescriptor> CommonTransforms => Common.Skip(1).Take(6).ToArray();
}
