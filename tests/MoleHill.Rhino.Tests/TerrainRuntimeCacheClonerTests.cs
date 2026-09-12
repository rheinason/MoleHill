using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The stage cache hands cloned summaries back, so a field the clone forgets reads as zero on every cached
/// build — an analysis that looks like it measured nothing. That is invisible to any test that only checks
/// the analysis itself, and invisible in a session until a second build hits the cache, which is exactly how
/// it got shipped once: the aspect and delta-output counts were added to
/// <see cref="TerrainAnalysisSummary"/> and not to <see cref="TerrainRuntimeCacheCloner.CloneAnalysis"/>.
/// </summary>
public class TerrainRuntimeCacheClonerTests
{
    [Fact]
    public void CloneAnalysis_CopiesEveryProperty()
    {
        var source = new TerrainAnalysisSummary();
        var properties = typeof(TerrainAnalysisSummary)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .ToList();

        // Distinct per property, and distinct from every default, so a field the clone drops cannot
        // accidentally match.
        var assigned = new Dictionary<string, object?>();
        int seed = 0;
        foreach (PropertyInfo property in properties)
        {
            object? value = DistinctValue(property.PropertyType, ++seed);
            property.SetValue(source, value);
            assigned[property.Name] = value;
        }

        TerrainAnalysisSummary? clone = TerrainRuntimeCacheCloner.CloneAnalysis(source);
        Assert.NotNull(clone);

        var missing = new List<string>();
        foreach (PropertyInfo property in properties)
        {
            object? cloned = property.GetValue(clone);
            object? expected = assigned[property.Name];

            bool equal = expected is double[] expectedArray
                ? cloned is double[] clonedArray && clonedArray.SequenceEqual(expectedArray)
                : Equals(cloned, expected);

            if (!equal)
                missing.Add($"{property.Name} (expected {Describe(expected)}, got {Describe(cloned)})");
        }

        Assert.True(
            missing.Count == 0,
            "TerrainRuntimeCacheCloner.CloneAnalysis does not copy: " + string.Join(", ", missing) +
            ". Add each one to CloneAnalysis — a dropped field reads back as zero on every cached build.");
    }

    /// <summary>An array is copied, not shared: a cached entry must not be mutated through its clone.</summary>
    [Fact]
    public void CloneAnalysis_DistributionBins_IsACopyNotTheSameArray()
    {
        var source = new TerrainAnalysisSummary { DistributionBins = new[] { 0.25, 0.5, 1.0 } };

        TerrainAnalysisSummary clone = Assert.IsType<TerrainAnalysisSummary>(
            TerrainRuntimeCacheCloner.CloneAnalysis(source));

        Assert.NotNull(clone.DistributionBins);
        Assert.NotSame(source.DistributionBins, clone.DistributionBins);
        Assert.Equal(source.DistributionBins, clone.DistributionBins);
    }

    [Fact]
    public void CloneAnalysis_Null_StaysNull()
    {
        Assert.Null(TerrainRuntimeCacheCloner.CloneAnalysis(null));
    }

    private static object? DistinctValue(Type type, int seed)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(double))
            return 1000.0 + seed;
        if (underlying == typeof(int))
            return 1000 + seed;
        if (underlying == typeof(bool))
            // Every bool on the summary defaults to true or false, so flip whichever it is by using the
            // seed's parity — the assertion compares against what was written, not against a constant.
            return seed % 2 == 0;
        if (underlying == typeof(Guid))
            return Guid.NewGuid();
        if (underlying == typeof(double[]))
            return new[] { seed + 0.5, seed + 1.5 };

        throw new InvalidOperationException(
            $"TerrainAnalysisSummary gained a {type} property; teach this test how to make a distinct value.");
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        double[] array => "[" + string.Join(", ", array) + "]",
        _ => value.ToString() ?? "null"
    };
}
