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
            case ParameterKind.BlockPicker:
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
            case ParameterKind.GoingTable:
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
    /// Every slope input declares <see cref="ParameterUnit.Slope"/>.
    ///
    /// This is the guard behind the whole unit story: a slope row that forgets to declare its unit falls
    /// back to a bare stepper showing degrees with nothing on screen saying so, which is the exact defect
    /// this vocabulary exists to remove. Matching on the name is deliberately blunt — anything a user
    /// would read as a slope has to opt in or be named on the allow-list below with a reason.
    /// </summary>
    [Fact]
    public void EveryParameterThatReadsAsASlope_DeclaresTheSlopeUnit()
    {
        // Genuine angles, not slopes: a dihedral fold between two faces has no rise over run, and the
        // Scatter/Waterflow style knobs below are labels and factors that merely contain the word.
        var notSlopes = new HashSet<string>(StringComparer.Ordinal)
        {
            "CreaseAngle",   // remesh + retopo: dihedral crease detection, always degrees
            "alignToSlope",  // a bool: orient instances to the terrain normal
            "slopeFilterEnabled",
        };

        var offenders = new List<string>();

        void Check(string family, string typeKind, string key, string label, ParameterKind kind, ParameterUnit unit)
        {
            if (notSlopes.Contains(key))
                return;

            bool readsAsSlope =
                key.Contains("Slope", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("Slope", StringComparison.OrdinalIgnoreCase);
            if (!readsAsSlope)
                return;

            // Only value rows carry a unit; a Choice of slope units or a read-only summary does not.
            if (kind is not (ParameterKind.Number or ParameterKind.OptionalNumber or ParameterKind.Slider))
                return;

            if (unit != ParameterUnit.Slope)
                offenders.Add($"{family}/{typeKind}/{key} ('{label}') declares {unit}, expected Slope.");
        }

        foreach (var d in TerrainTypeRegistry.Modifiers)
            foreach (var p in d.Parameters)
                Check("modifier", d.Kind, p.Key, p.Label, p.Kind, p.Unit);
        foreach (var d in AnalysisTypeRegistry.Analyses)
            foreach (var p in d.Parameters)
                Check("analysis", d.Kind, p.Key, p.Label, p.Kind, p.Unit);
        foreach (var d in AnnotationTypeRegistry.Annotations)
            foreach (var p in d.Parameters)
                Check("annotation", d.Kind, p.Key, p.Label, p.Kind, p.Unit);
        foreach (var d in ObjectTypeRegistry.Objects)
            foreach (var p in d.Parameters)
                Check("object", d.Kind, p.Key, p.Label, p.Kind, p.Unit);

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The grading batters are the slope rows this was built for, so name them outright: a refactor that
    /// quietly turns one back into a plain Number would otherwise only show up in Rhino.
    /// </summary>
    [Theory]
    [InlineData("grade-pad", "SlopeAngle")]
    [InlineData("grade-pad", "CutSlopeAngle")]
    [InlineData("grade-path", "SlopeAngle")]
    [InlineData("grade-path", "CutSlopeAngle")]
    [InlineData("grade-line", "SlopeAngle")]
    [InlineData("grade-line", "CutSlopeAngle")]
    [InlineData("grade-line", "LeftCutSlopeAngle")]
    [InlineData("grade-line", "LeftFillSlopeAngle")]
    [InlineData("grade-line", "RightCutSlopeAngle")]
    [InlineData("grade-line", "RightFillSlopeAngle")]
    public void GradingBatters_AreSlopeRows(string kind, string key)
    {
        var descriptor = TerrainTypeRegistry.ForModifierKind(kind);
        Assert.NotNull(descriptor);

        var parameter = descriptor!.Parameters.SingleOrDefault(p => p.Key == key);
        Assert.NotNull(parameter);
        Assert.Equal(ParameterUnit.Slope, parameter!.Unit);
    }

    [Fact]
    public void InSituStair_DoesNotExposeDaylightSlopeBeforeTerrainGradingIsSupported()
    {
        var descriptor = TerrainTypeRegistry.ForModifierKind("in-situ-stair");

        Assert.NotNull(descriptor);
        Assert.DoesNotContain(descriptor!.Parameters, parameter => parameter.Key == "SlopeAngle");
    }

    /// <summary>
    /// A slope row is stored as an angle, so its bounds must stay inside what an angle can express —
    /// otherwise a percent entry converts to 90 degrees or beyond and the batter maths divides by zero.
    /// </summary>
    [Fact]
    public void SlopeRows_StayWithinTheAngleTheyAreStoredAs()
    {
        foreach (var descriptor in TerrainTypeRegistry.Modifiers)
        {
            foreach (var parameter in descriptor.Parameters.Where(p => p.Unit == ParameterUnit.Slope))
            {
                if (parameter.Max is { } max)
                {
                    Assert.True(
                        max <= 90.0,
                        $"modifier/{descriptor.Kind}/{parameter.Key}: max {max} exceeds a vertical slope.");
                }

                if (parameter.Min is { } min)
                {
                    Assert.True(
                        min >= 0.0,
                        $"modifier/{descriptor.Kind}/{parameter.Key}: min {min} is below flat.");
                }
            }
        }
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
