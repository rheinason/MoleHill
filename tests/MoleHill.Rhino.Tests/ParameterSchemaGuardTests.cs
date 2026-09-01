using System;
using System.Collections.Generic;
using System.Linq;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Structural guards over the declarative parameter schema, across all four families at once.
///
/// Every family declares its card rows as <see cref="ParameterDescriptor{TDefinition}"/> values, and a
/// row is only as good as the accessor pair its <see cref="ParameterKind"/> needs: a Number with no
/// setter, or a Choice with neither an option list nor an options callback, compiles fine and then throws
/// (or silently renders an unusable row) the first time someone opens that card in Rhino. These tests are
/// the cheap version of clicking through every card — one generic helper covers all four families,
/// which is what the single generic descriptor bought.
/// </summary>
public class ParameterSchemaGuardTests
{
    public static TheoryData<string, string, object> AllSchemas()
    {
        var data = new TheoryData<string, string, object>();

        foreach (var descriptor in TerrainTypeRegistry.Modifiers)
            foreach (var parameter in descriptor.Parameters)
                data.Add("modifier", descriptor.Kind, parameter);

        foreach (var descriptor in AnalysisTypeRegistry.Analyses)
            foreach (var parameter in descriptor.Parameters)
                data.Add("analysis", descriptor.Kind, parameter);

        foreach (var descriptor in AnnotationTypeRegistry.Annotations)
            foreach (var parameter in descriptor.Parameters)
                data.Add("annotation", descriptor.Kind, parameter);

        foreach (var descriptor in ObjectTypeRegistry.Objects)
            foreach (var parameter in descriptor.Parameters)
                data.Add("object", descriptor.Kind, parameter);

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSchemas))]
    public void EveryParameter_HasTheAccessorsItsKindRequires(string family, string typeKind, object parameter)
    {
        // The descriptor is generic in its family's definition type, so reach the shared surface through
        // reflection rather than making this test generic four times over.
        Type type = parameter.GetType();
        string where = $"{family}/{typeKind}/{Get<string>(type, parameter, "Key")}";

        var kind = Get<ParameterKind>(type, parameter, "Kind");

        Assert.False(string.IsNullOrWhiteSpace(Get<string>(type, parameter, "Key")), $"{where}: Key is blank.");
        Assert.False(string.IsNullOrWhiteSpace(Get<string>(type, parameter, "Label")), $"{where}: Label is blank.");

        switch (kind)
        {
            case ParameterKind.Sources:
                AssertSet(type, parameter, "GetSources", where);
                break;

            case ParameterKind.Number:
            case ParameterKind.Slider:
                AssertSet(type, parameter, "GetNumber", where);
                AssertSet(type, parameter, "SetNumber", where);
                break;

            case ParameterKind.OptionalNumber:
                AssertSet(type, parameter, "GetNumber", where);
                AssertSet(type, parameter, "SetNumber", where);
                AssertSet(type, parameter, "InheritedValue", where);
                break;

            case ParameterKind.Bool:
                AssertSet(type, parameter, "GetBool", where);
                AssertSet(type, parameter, "SetBool", where);
                break;

            case ParameterKind.Choice:
                AssertSet(type, parameter, "GetText", where);
                AssertSet(type, parameter, "SetText", where);
                Assert.True(
                    Get<object?>(type, parameter, "ChoiceOptions") != null ||
                    Get<object?>(type, parameter, "ChoiceOptionsFor") != null,
                    $"{where}: Choice has neither ChoiceOptions nor ChoiceOptionsFor.");
                break;

            case ParameterKind.Text:
                AssertSet(type, parameter, "GetText", where);
                AssertSet(type, parameter, "SetText", where);
                break;

            case ParameterKind.Color:
                AssertSet(type, parameter, "GetColor", where);
                AssertSet(type, parameter, "SetColor", where);
                break;

            case ParameterKind.ReadOnly:
                AssertSet(type, parameter, "GetReadOnly", where);
                break;

            case ParameterKind.ColorRamp:
            case ParameterKind.BlockMix:
                // Whole-control kinds edit the definition directly; they deliberately carry no accessors.
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(parameter), kind, $"{where}: unhandled kind.");
        }
    }

    [Fact]
    public void ParameterKeys_AreUnique_WithinEachType()
    {
        var offenders = new List<string>();

        void Check(string family, string typeKind, IEnumerable<string> keys)
        {
            foreach (var duplicate in keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
                offenders.Add($"{family}/{typeKind}: '{duplicate.Key}' x{duplicate.Count()}");
        }

        foreach (var d in TerrainTypeRegistry.Modifiers)
            Check("modifier", d.Kind, d.Parameters.Select(p => p.Key));
        foreach (var d in AnalysisTypeRegistry.Analyses)
            Check("analysis", d.Kind, d.Parameters.Select(p => p.Key));
        foreach (var d in AnnotationTypeRegistry.Annotations)
            Check("annotation", d.Kind, d.Parameters.Select(p => p.Key));
        foreach (var d in ObjectTypeRegistry.Objects)
            Check("object", d.Kind, d.Parameters.Select(p => p.Key));

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// ColorRamp is the analysis family's alone (annotations draw rather than colour-map, modifiers change
    /// geometry) and BlockMix is Scatter's alone. The row builder renders both through a per-family
    /// bespoke hook, so a type in the wrong family declaring one would render an empty row.
    /// </summary>
    [Fact]
    public void WholeControlKinds_AreDeclaredOnlyByTheFamilyThatRendersThem()
    {
        Assert.True(!TerrainTypeRegistry.Modifiers.SelectMany(d => d.Parameters)
            .Any(p => p.Kind is ParameterKind.ColorRamp or ParameterKind.BlockMix));
        Assert.True(!AnnotationTypeRegistry.Annotations.SelectMany(d => d.Parameters)
            .Any(p => p.Kind is ParameterKind.ColorRamp or ParameterKind.BlockMix));
        Assert.True(!AnalysisTypeRegistry.Analyses.SelectMany(d => d.Parameters)
            .Any(p => p.Kind == ParameterKind.BlockMix));
        Assert.True(!ObjectTypeRegistry.Objects.SelectMany(d => d.Parameters)
            .Any(p => p.Kind == ParameterKind.ColorRamp));

        // ...and the two that do exist are still declared, so this test fails loudly if they are dropped.
        Assert.Contains(AnalysisTypeRegistry.Analyses.SelectMany(d => d.Parameters),
            p => p.Kind == ParameterKind.ColorRamp);
        Assert.Contains(ObjectTypeRegistry.Objects.SelectMany(d => d.Parameters),
            p => p.Kind == ParameterKind.BlockMix);
    }

    private static T Get<T>(Type type, object instance, string property) =>
        (T)type.GetProperty(property)!.GetValue(instance)!;

    private static void AssertSet(Type type, object instance, string property, string where) =>
        Assert.True(type.GetProperty(property)!.GetValue(instance) != null, $"{where}: {property} is null.");
}
