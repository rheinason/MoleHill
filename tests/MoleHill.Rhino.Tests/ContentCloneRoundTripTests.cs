using System.Text.Json;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Duplicating an analysis or annotation card (`TerrainController.CloneAnalysis`/`CloneAnnotation`)
/// round-trips the concrete definition through JSON with <see cref="TerrainSerializer.SharedOptions"/>
/// in both directions. Mixing option sets (camelCase out, PascalCase in) binds no property and the
/// duplicate comes back as a default card. The controller itself is not linked here, so these pin the
/// round-trip it relies on.
/// </summary>
public class ContentCloneRoundTripTests
{
    [Fact]
    public void ConcreteTypeRoundTrip_SharedOptionsBothWays_PreservesAnnotationSettings()
    {
        var section = new TerrainSectionAnnotationDefinition
        {
            StationTickInterval = 12.5,
            ShowStationLabels = false,
            CutColorArgb = unchecked((int)0xFF112233),
            CutFillOpacityPercent = 55
        };

        string json = JsonSerializer.Serialize(section, section.GetType(), TerrainSerializer.SharedOptions);
        var clone = Assert.IsType<TerrainSectionAnnotationDefinition>(
            JsonSerializer.Deserialize(json, section.GetType(), TerrainSerializer.SharedOptions));

        Assert.Equal(12.5, clone.StationTickInterval);
        Assert.False(clone.ShowStationLabels);
        Assert.Equal(unchecked((int)0xFF112233), clone.CutColorArgb);
        Assert.Equal(55, clone.CutFillOpacityPercent);
    }

    [Fact]
    public void ConcreteTypeRoundTrip_EveryAnnotationType_IsLossless()
    {
        foreach (AnnotationTypeDescriptor descriptor in AnnotationTypeRegistry.Annotations)
        {
            AnnotationDefinition original = descriptor.Create();
            AssertLosslessRoundTrip(original, descriptor.Kind);
        }
    }

    [Fact]
    public void ConcreteTypeRoundTrip_EveryAnalysisType_IsLossless()
    {
        foreach (AnalysisTypeDescriptor descriptor in AnalysisTypeRegistry.Analyses)
        {
            AnalysisDefinition original = descriptor.Create();
            AssertLosslessRoundTrip(original, descriptor.Kind);
        }
    }

    private static void AssertLosslessRoundTrip(object original, string kind)
    {
        Type type = original.GetType();
        string json = JsonSerializer.Serialize(original, type, TerrainSerializer.SharedOptions);
        object? clone = JsonSerializer.Deserialize(json, type, TerrainSerializer.SharedOptions);

        Assert.NotNull(clone);
        Assert.IsType(type, clone);
        string again = JsonSerializer.Serialize(clone, type, TerrainSerializer.SharedOptions);
        Assert.True(json == again, $"'{kind}' did not round-trip losslessly.");
    }
}
