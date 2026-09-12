using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// <see cref="TerrainAnalysisSummary"/> is persisted with the terrain (`LastAnalysisResults`), which makes
/// its defaults part of the document contract.
///
/// This exists because of a real failure: `AspectDominantBearing` was given a `double.NaN` default to mean
/// "no mean direction", and `System.Text.Json` refuses to write a non-finite double. Every summary carries
/// every field whether or not it measured aspect, so a *single* NaN default stopped every terrain with any
/// analysis at all from saving — and every save path runs through the undo snapshot, so it surfaced as a
/// failed edit rather than as a failed save. No unit test on the analysis itself could see it.
/// </summary>
public class TerrainSummarySerializationTests
{
    /// <summary>
    /// The premise the other tests here rest on, pinned rather than assumed: the serializer really does
    /// refuse a non-finite double, and it fails the whole document, not just the offending field. Without
    /// this, a future change to the serializer options could quietly make the guards below meaningless.
    /// </summary>
    [Fact]
    public void Serialize_SummaryHoldingANonFiniteDouble_ThrowsForTheWholeTerrain()
    {
        var terrain = new TerrainDefinition();
        terrain.LastAnalysisResults.Add(new TerrainAnalysisSummary { SurfaceArea = double.NaN });

        Assert.ThrowsAny<ArgumentException>(() => TerrainSerializer.Serialize(new[] { terrain }));
    }

    /// <summary>The bug, stated directly: a default-constructed summary has to survive a save.</summary>
    [Fact]
    public void Serialize_TerrainWithADefaultSummary_Succeeds()
    {
        var terrain = new TerrainDefinition();
        terrain.LastAnalysisResults.Add(new TerrainAnalysisSummary { AnalysisId = Guid.NewGuid() });

        string json = TerrainSerializer.Serialize(new[] { terrain });

        TerrainDefinition restored = Assert.Single(TerrainSerializer.Deserialize(json));
        Assert.Single(restored.LastAnalysisResults);
    }

    /// <summary>
    /// The same for every registered analysis type, since each one adds its own card and its own summary
    /// alongside the fields the others left at their defaults.
    /// </summary>
    [Fact]
    public void Serialize_TerrainWithEveryAnalysisTypeAndItsSummary_Succeeds()
    {
        var terrain = new TerrainDefinition();
        foreach (AnalysisTypeDescriptor descriptor in AnalysisTypeRegistry.Analyses)
        {
            AnalysisDefinition analysis = descriptor.Create();
            terrain.Analyses.Add(analysis);
            terrain.LastAnalysisResults.Add(new TerrainAnalysisSummary { AnalysisId = analysis.Id });
        }

        string json = TerrainSerializer.Serialize(new[] { terrain });

        TerrainDefinition restored = Assert.Single(TerrainSerializer.Deserialize(json));
        Assert.Equal(AnalysisTypeRegistry.Analyses.Count, restored.Analyses.Count);
        Assert.Equal(AnalysisTypeRegistry.Analyses.Count, restored.LastAnalysisResults.Count);
    }

    /// <summary>
    /// The general rule, so the next field cannot repeat it: nothing on a persisted summary may default to
    /// a value JSON cannot represent. Use a nullable to mean "not measured" — null is what "there is no
    /// value" looks like in a document.
    /// </summary>
    [Fact]
    public void TerrainAnalysisSummary_HasNoNonFiniteDefaults()
    {
        var summary = new TerrainAnalysisSummary();
        var offenders = new List<string>();

        foreach (PropertyInfo property in typeof(TerrainAnalysisSummary)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? value = property.GetValue(summary);
            if (value is double number && !double.IsFinite(number))
                offenders.Add($"{property.Name} = {number}");
        }

        Assert.True(
            offenders.Count == 0,
            "System.Text.Json cannot write these, so the whole terrain fails to save: " +
            string.Join(", ", offenders) + ". Make the property nullable and use null instead.");
    }
}
