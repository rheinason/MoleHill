using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MoleHill.Core.Sculpting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Proves the attribute-driven <see cref="TerrainUnitScaler"/> scales exactly what the frozen hand-listed
/// scaler it replaced did: every concrete definition type, every double property filled with a distinct
/// value, several factors, identical results.
/// </summary>
public sealed class TerrainUnitScalerEquivalenceTests
{
    private static readonly double[] Factors = { 0.001, 0.3048, 1.0, 2.5, 25.4, 1000.0 };

    private static IEnumerable<Type> ConcreteTypes<T>() =>
        typeof(T).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(T).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

    [Fact]
    public void Modifiers_EveryConcreteType_ScaleLikeTheLegacyScaler()
    {
        var diffs = new List<string>();
        foreach (Type type in ConcreteTypes<ModifierDefinition>())
        {
            foreach (double factor in Factors)
            {
                var legacy = (ModifierDefinition)Fill(Activator.CreateInstance(type)!);
                var current = (ModifierDefinition)Fill(Activator.CreateInstance(type)!);
                LegacyTerrainUnitScalerOracle.Scale(legacy, factor);
                TerrainUnitScaler.Scale(current, factor);
                Compare(legacy, current, $"{type.Name} x{factor}", diffs, 0);
            }
        }

        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [Fact]
    public void Analyses_EveryConcreteType_ScaleLikeTheLegacyScaler()
    {
        var diffs = new List<string>();
        foreach (Type type in ConcreteTypes<AnalysisDefinition>())
        {
            foreach (double factor in Factors)
            {
                var legacy = (AnalysisDefinition)Fill(Activator.CreateInstance(type)!);
                var current = (AnalysisDefinition)Fill(Activator.CreateInstance(type)!);
                LegacyTerrainUnitScalerOracle.Scale(legacy, factor);
                TerrainUnitScaler.Scale(current, factor);
                Compare(legacy, current, $"{type.Name} x{factor}", diffs, 0);
            }
        }

        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [Fact]
    public void Annotations_EveryConcreteType_ScaleLikeTheLegacyScaler()
    {
        var diffs = new List<string>();
        foreach (Type type in ConcreteTypes<AnnotationDefinition>())
        {
            foreach (double factor in Factors)
            {
                var legacy = (AnnotationDefinition)Fill(Activator.CreateInstance(type)!);
                var current = (AnnotationDefinition)Fill(Activator.CreateInstance(type)!);
                LegacyTerrainUnitScalerOracle.Scale(legacy, factor);
                TerrainUnitScaler.Scale(current, factor);
                Compare(legacy, current, $"{type.Name} x{factor}", diffs, 0);
            }
        }

        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [Fact]
    public void Objects_EveryConcreteType_ScaleLikeTheLegacyScaler()
    {
        var diffs = new List<string>();
        foreach (Type type in ConcreteTypes<TerrainObjectDefinition>())
        {
            foreach (double factor in Factors)
            {
                var legacy = (TerrainObjectDefinition)Fill(Activator.CreateInstance(type)!);
                var current = (TerrainObjectDefinition)Fill(Activator.CreateInstance(type)!);
                LegacyTerrainUnitScalerOracle.Scale(legacy, factor);
                TerrainUnitScaler.Scale(current, factor);
                Compare(legacy, current, $"{type.Name} x{factor}", diffs, 0);
            }
        }

        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [Fact]
    public void Terrains_WithEveryDefinitionAndSummary_ScaleLikeTheLegacyScaler()
    {
        var diffs = new List<string>();
        foreach (double factor in Factors)
        {
            TerrainDefinition legacy = BuildKitchenSinkTerrain();
            TerrainDefinition current = BuildKitchenSinkTerrain();
            LegacyTerrainUnitScalerOracle.Scale(new[] { legacy }, factor);
            TerrainUnitScaler.Scale(new[] { current }, factor);
            Compare(legacy, current, $"terrain x{factor}", diffs, 0);
        }

        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [Fact]
    public void Scale_NonPositiveFactor_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainUnitScaler.Scale(new RemeshModifierDefinition(), 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainUnitScaler.Scale(new[] { new TerrainDefinition() }, double.NaN));
    }

    private static TerrainDefinition BuildKitchenSinkTerrain()
    {
        var terrain = (TerrainDefinition)Fill(new TerrainDefinition());
        foreach (Type type in ConcreteTypes<ModifierDefinition>())
            terrain.Modifiers.Add((ModifierDefinition)Fill(Activator.CreateInstance(type)!));
        foreach (Type type in ConcreteTypes<TerrainObjectDefinition>())
            terrain.Objects.Add((TerrainObjectDefinition)Fill(Activator.CreateInstance(type)!));
        foreach (Type type in ConcreteTypes<AnalysisDefinition>())
            terrain.Analyses.Add((AnalysisDefinition)Fill(Activator.CreateInstance(type)!));
        foreach (Type type in ConcreteTypes<AnnotationDefinition>())
            terrain.Annotations.Add((AnnotationDefinition)Fill(Activator.CreateInstance(type)!));

        // One summary per owner, so the owner-dependent fields are exercised for both kinds of owner.
        foreach (Guid id in terrain.Analyses.Select(item => item.Id).Concat(terrain.Annotations.Select(item => item.Id)))
        {
            var summary = (TerrainAnalysisSummary)Fill(new TerrainAnalysisSummary());
            summary.AnalysisId = id;
            terrain.LastAnalysisResults.Add(summary);
        }

        terrain.LegacyLastAnalysis = (TerrainAnalysisSummary)Fill(new TerrainAnalysisSummary());
        return terrain;
    }

    /// <summary>Gives every writable double / nullable double a distinct value, in a stable order.</summary>
    private static object Fill(object target)
    {
        int index = 1;
        foreach (PropertyInfo property in target.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetSetMethod() != null && p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.PropertyType == typeof(double) || property.PropertyType == typeof(double?))
                property.SetValue(target, 1.0 + index++ * 0.173);
        }

        if (target is TerrainObjectDefinition objectDefinition)
        {
            var transform = Enumerable.Range(0, 16).Select(i => 10.0 + i).ToArray();
            objectDefinition.PlacementStates.Add(new TerrainObjectPlacementState { LastAppliedTransform = transform });
        }

        if (target is SculptModifierDefinition sculpt)
        {
            var field = new SculptDisplacementField(sculpt.EffectiveCellSize);
            field.SetSample(0, 0, 1.5f);
            field.SetSample(70, 3, -0.25f);
            sculpt.Tiles = SculptFieldCodec.Encode(field);
        }

        return target;
    }

    private static void Compare(object? expected, object? actual, string path, List<string> diffs, int depth)
    {
        if (expected == null || actual == null)
        {
            if (!ReferenceEquals(expected, actual)) diffs.Add($"{path}: null mismatch");
            return;
        }

        Type type = expected.GetType();
        if (type == typeof(Guid) || depth > 6)
            return;

        if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
        {
            if (!expected.Equals(actual)) diffs.Add($"{path}: legacy {expected} vs current {actual}");
            return;
        }

        if (expected is double[] left)
        {
            var right = (double[])actual;
            if (left.Length != right.Length || !left.SequenceEqual(right)) diffs.Add($"{path}: array differs");
            return;
        }

        if (expected is IEnumerable sequence and not string)
        {
            var a = sequence.Cast<object?>().ToList();
            var b = ((IEnumerable)actual).Cast<object?>().ToList();
            if (a.Count != b.Count)
            {
                diffs.Add($"{path}: count {a.Count} vs {b.Count}");
                return;
            }

            for (int i = 0; i < a.Count; i++)
                Compare(a[i], b[i], $"{path}[{i}]", diffs, depth + 1);
            return;
        }

        if (type.Namespace != typeof(ModifierDefinition).Namespace)
            return;

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                continue;
            Compare(property.GetValue(expected), property.GetValue(actual), $"{path}.{property.Name}", diffs, depth + 1);
        }
    }
}
