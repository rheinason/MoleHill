using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class GradePathVariableWidthModelTests
{
    [Fact]
    public void Serialize_VariableWidthSourcesAndDistance_RoundTrips()
    {
        Guid centerline = Guid.NewGuid();
        Guid edgeA = Guid.NewGuid();
        Guid edgeB = Guid.NewGuid();
        var modifier = new GradePathModifierDefinition
        {
            Width = 2.5,
            UseVariableWidth = true,
            MaxEdgeDistance = 15.0,
            Paths = new SourceReferenceSet { ObjectIds = [centerline] },
            WidthEdges = new SourceReferenceSet { ObjectIds = [edgeA, edgeB] }
        };
        var terrain = new TerrainDefinition { Modifiers = [new TriangulateModifierDefinition(), modifier] };

        string json = TerrainSerializer.Serialize([terrain]);
        TerrainDefinition restored = Assert.Single(TerrainSerializer.Deserialize(json));
        GradePathModifierDefinition path = Assert.IsType<GradePathModifierDefinition>(restored.Modifiers[1]);

        Assert.True(path.UseVariableWidth);
        Assert.Equal(15.0, path.MaxEdgeDistance);
        Assert.Equal([centerline], path.Paths.ObjectIds);
        Assert.Equal([edgeA, edgeB], path.WidthEdges.ObjectIds);
        Assert.Contains(path.EnumerateSourceSets(), sources => ReferenceEquals(sources, path.WidthEdges));
    }

    [Fact]
    public void Descriptor_ExposesGeometryAndAdvancedMatchingParameters()
    {
        ModifierTypeDescriptor descriptor = TerrainTypeRegistry.Modifiers.Single(
            item => item.DefinitionType == typeof(GradePathModifierDefinition));

        Assert.Contains(descriptor.Parameters, parameter => parameter.Key == "Paths" && parameter.Label == "Centerlines");
        Assert.Contains(descriptor.Parameters, parameter => parameter.Key == "UseVariableWidth");
        Assert.Contains(descriptor.Parameters, parameter => parameter.Key == "WidthEdges");
        Assert.Contains(descriptor.Parameters, parameter => parameter.Key == "MaxEdgeDistance");
    }

    [Fact]
    public void Descriptor_UseVariableWidthOff_HidesWidthEdgeParameters()
    {
        ModifierTypeDescriptor descriptor = TerrainTypeRegistry.Modifiers.Single(
            item => item.DefinitionType == typeof(GradePathModifierDefinition));
        var modifier = new GradePathModifierDefinition { UseVariableWidth = false };

        foreach (string key in new[] { "WidthEdges", "MaxEdgeDistance" })
        {
            ParameterDescriptor parameter = descriptor.Parameters.Single(item => item.Key == key);
            Assert.NotNull(parameter.VisibleWhen);
            Assert.False(parameter.VisibleWhen!(modifier));

            modifier.UseVariableWidth = true;
            Assert.True(parameter.VisibleWhen(modifier));
            modifier.UseVariableWidth = false;
        }
    }

    [Fact]
    public void Descriptor_CenterlinesWidthAndToggle_AreAlwaysVisible()
    {
        ModifierTypeDescriptor descriptor = TerrainTypeRegistry.Modifiers.Single(
            item => item.DefinitionType == typeof(GradePathModifierDefinition));

        foreach (string key in new[] { "Paths", "Width", "UseVariableWidth" })
            Assert.Null(descriptor.Parameters.Single(item => item.Key == key).VisibleWhen);
    }

    [Fact]
    public void Deserialize_LegacyDocumentWithWidthEdges_EnablesVariableWidth()
    {
        Guid edge = Guid.NewGuid();
        var legacy = new GradePathModifierDefinition
        {
            WidthEdges = new SourceReferenceSet { ObjectIds = [edge] }
        };
        var terrain = new TerrainDefinition { Modifiers = [new TriangulateModifierDefinition(), legacy] };
        string json = TerrainSerializer.Serialize([terrain])
            .Replace("\"schemaVersion\":29", "\"schemaVersion\":28")
            .Replace("\"schemaVersion\": 29", "\"schemaVersion\": 28");
        Assert.DoesNotContain("\"schemaVersion\":29", json);

        TerrainDefinition restored = Assert.Single(TerrainSerializer.Deserialize(json));
        GradePathModifierDefinition path = Assert.Single(
            restored.Modifiers.OfType<GradePathModifierDefinition>());

        Assert.True(path.UseVariableWidth);
        Assert.Equal([edge], path.WidthEdges.ObjectIds);
    }

    [Fact]
    public void Deserialize_CurrentDocumentWithToggleOff_KeepsWidthEdgesParked()
    {
        Guid edge = Guid.NewGuid();
        var modifier = new GradePathModifierDefinition
        {
            UseVariableWidth = false,
            WidthEdges = new SourceReferenceSet { ObjectIds = [edge] }
        };
        var terrain = new TerrainDefinition { Modifiers = [new TriangulateModifierDefinition(), modifier] };

        string json = TerrainSerializer.Serialize([terrain]);
        TerrainDefinition restored = Assert.Single(TerrainSerializer.Deserialize(json));
        GradePathModifierDefinition path = Assert.IsType<GradePathModifierDefinition>(restored.Modifiers[1]);

        Assert.False(path.UseVariableWidth);
        Assert.Equal([edge], path.WidthEdges.ObjectIds);
    }
}
