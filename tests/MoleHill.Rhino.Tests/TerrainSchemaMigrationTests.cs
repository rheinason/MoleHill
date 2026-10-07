using System.Linq;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// One hand-written document per historical schema step in <c>TerrainSerializer</c>, asserting both
/// sides of each version gate: a document older than the step is migrated, one at or past it is left
/// as written.
/// </summary>
public class TerrainSchemaMigrationTests
{
    private static TerrainDefinition Load(int version, string terrainBody)
    {
        string json = "{ \"schemaVersion\": " + version + ", \"terrains\": [ { \"schemaVersion\": " + version +
                      ", \"terrainId\": \"77777777-7777-7777-7777-777777777777\", \"name\": \"T\", " + terrainBody + " } ] }";
        return TerrainSerializer.Deserialize(json).Single();
    }

    [Theory]
    [InlineData(26, false)]
    [InlineData(27, true)]
    public void Contour_SeparateMajorMinorLayers_IsOffBeforeSchema27(int version, bool expected)
    {
        TerrainDefinition terrain = Load(version, "\"annotations\": [ { \"$type\": \"contour\", \"interval\": 1.0 } ]");

        var contour = Assert.IsType<ContourAnnotationDefinition>(Assert.Single(terrain.Annotations));
        Assert.Equal(expected, contour.SeparateMajorMinorLayers);
        Assert.Equal(expected, contour.FollowsAnnotationStyle);
    }

    [Theory]
    [InlineData(26, false)]
    [InlineData(27, true)]
    public void Marker_FollowsAnnotationStyle_IsOffBeforeSchema27(int version, bool expected)
    {
        TerrainDefinition terrain = Load(version, "\"markers\": [ { \"$type\": \"elevation\", \"name\": \"M1\", \"blockScale\": 2.0 } ]");

        Assert.Equal(expected, Assert.Single(terrain.Markers).FollowsAnnotationStyle);
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(25, true)]
    public void Analysis_ExplicitRange_TurnsAutoRangeOffBeforeSchema25(int version, bool expectedAuto)
    {
        TerrainDefinition terrain = Load(version, "\"analyses\": [ { \"$type\": \"slope\", \"rangeLow\": 5.0, \"rangeHigh\": 25.0 } ]");

        var slope = Assert.IsType<SlopeAnalysisDefinition>(Assert.Single(terrain.Analyses));
        Assert.Equal(expectedAuto, slope.AutoColorRange);
        Assert.Equal(25.0, slope.RangeHigh);
    }

    [Theory]
    [InlineData(28, true)]
    [InlineData(29, false)]
    public void GradePath_WidthEdges_ImplyVariableWidthBeforeSchema29(int version, bool expected)
    {
        TerrainDefinition terrain = Load(version,
            "\"modifiers\": [ { \"$type\": \"triangulate\" }, " +
            "{ \"$type\": \"grade-path\", \"widthEdges\": { \"objectIds\": [], \"layerPaths\": [ \"Edges\" ] } } ]");

        var path = Assert.IsType<GradePathModifierDefinition>(terrain.Modifiers.Single(m => m is GradePathModifierDefinition));
        Assert.Equal(expected, path.UseVariableWidth);
        Assert.Equal("Edges", Assert.Single(path.WidthEdges.LayerPaths));
    }

    [Fact]
    public void Remesh_BeforeSchema22_ZeroesLegacyMinAngleAndMaxArea()
    {
        TerrainDefinition terrain = Load(21,
            "\"modifiers\": [ { \"$type\": \"triangulate\" }, { \"$type\": \"remesh\", \"maxArea\": 20.0, \"minAngle\": 20.0 } ]");

        var remesh = Assert.IsType<RemeshModifierDefinition>(terrain.Modifiers.Single(m => m is RemeshModifierDefinition));
        Assert.Equal(0.0, remesh.MaxArea, 9);
        Assert.Equal(0.0, remesh.MinAngle, 9);
        Assert.True(remesh.EdgeLength > 0.0);
    }

    [Fact]
    public void Remesh_AtSchema22_KeepsMinAngleAndMaxArea()
    {
        TerrainDefinition terrain = Load(22,
            "\"modifiers\": [ { \"$type\": \"triangulate\" }, { \"$type\": \"remesh\", \"edgeLength\": 3.0, \"maxArea\": 20.0, \"minAngle\": 20.0 } ]");

        var remesh = Assert.IsType<RemeshModifierDefinition>(terrain.Modifiers.Single(m => m is RemeshModifierDefinition));
        Assert.Equal(20.0, remesh.MaxArea, 9);
        Assert.Equal(20.0, remesh.MinAngle, 9);
        Assert.Equal(3.0, remesh.EdgeLength, 9);
    }

    [Fact]
    public void Remesh_BlankModeBeforeSchema23_BecomesIsotropic()
    {
        TerrainDefinition terrain = Load(22,
            "\"modifiers\": [ { \"$type\": \"triangulate\" }, { \"$type\": \"remesh\", \"edgeLength\": 3.0, \"mode\": \"\" } ]");

        var remesh = Assert.IsType<RemeshModifierDefinition>(terrain.Modifiers.Single(m => m is RemeshModifierDefinition));
        Assert.Equal("isotropic", remesh.Mode);
    }

    [Fact]
    public void Legacy_ShowSlopePreview_BecomesASlopeAnalysis()
    {
        TerrainDefinition terrain = Load(20,
            "\"showSlopePreview\": true, \"slopeColorLowPercent\": 4.0, \"slopeColorHighPercent\": 30.0");

        var slope = Assert.IsType<SlopeAnalysisDefinition>(Assert.Single(terrain.Analyses));
        Assert.True(slope.IsEnabled);
        Assert.Equal(4.0, slope.RangeLow);
        Assert.Equal(30.0, slope.RangeHigh);
        Assert.False(terrain.ShowSlopePreview);
    }

    [Fact]
    public void Legacy_EarthworkSources_MoveOntoADisabledEarthworkAnalysis()
    {
        TerrainDefinition terrain = Load(20,
            "\"earthworkReference\": { \"objectIds\": [], \"layerPaths\": [ \"Ref\" ] }, " +
            "\"earthworkBoundary\": { \"objectIds\": [], \"layerPaths\": [ \"Bnd\" ] }");

        var earthwork = Assert.IsType<EarthworkAnalysisDefinition>(Assert.Single(terrain.Analyses));
        Assert.False(earthwork.IsEnabled);
        Assert.Equal("Ref", Assert.Single(earthwork.Reference.LayerPaths));
        Assert.Equal("Bnd", Assert.Single(earthwork.Boundary.LayerPaths));
        Assert.Null(terrain.LegacyEarthworkReference);
        Assert.Null(terrain.LegacyEarthworkBoundary);
    }

    [Fact]
    public void Legacy_TriangulateTolerance_PromotesToGlobalTolerance()
    {
        TerrainDefinition terrain = Load(TerrainDefinition.CurrentSchemaVersion,
            "\"modifiers\": [ { \"$type\": \"triangulate\", \"tolerance\": 0.5 } ]");

        Assert.Equal(0.5, terrain.GlobalTolerance, 9);
        Assert.Equal(0.0, terrain.Modifiers.OfType<TriangulateModifierDefinition>().First().Tolerance, 9);
    }

    [Fact]
    public void Legacy_BoundaryOnBaseTriangulate_BecomesOuterBeforeSchema32()
    {
        TerrainDefinition terrain = Load(31,
            "\"modifiers\": [ { \"$type\": \"triangulate\", \"boundary\": { \"objectIds\": [], \"layerPaths\": [ \"Crop\" ] } } ]");

        var triangulate = Assert.IsType<TriangulateModifierDefinition>(terrain.Modifiers[0]);
        Assert.Equal("Crop", Assert.Single(triangulate.OuterBoundaries.LayerPaths));
        Assert.Null(triangulate.LegacyBoundary);
    }

    [Fact]
    public void Boundary_AtSchema32_IsNotReinterpretedAsOuter()
    {
        TerrainDefinition terrain = Load(32,
            "\"modifiers\": [ { \"$type\": \"triangulate\", \"boundary\": { \"objectIds\": [], \"layerPaths\": [ \"Crop\" ] } } ]");

        var triangulate = Assert.IsType<TriangulateModifierDefinition>(terrain.Modifiers[0]);
        Assert.Empty(triangulate.OuterBoundaries.LayerPaths);
        Assert.Null(triangulate.LegacyBoundary);
    }

    [Fact]
    public void Document_WithoutAnySchemaVersion_AppliesEveryGate()
    {
        const string json = """
        { "terrains": [ { "schemaVersion": 0, "terrainId": "88888888-8888-8888-8888-888888888888", "name": "Oldest",
          "markers": [ { "$type": "elevation", "name": "M" } ],
          "analyses": [ { "$type": "contour", "interval": 1.0 }, { "$type": "slope", "rangeLow": 1.0, "rangeHigh": 9.0 } ] } ] }
        """;

        TerrainDefinition terrain = TerrainSerializer.Deserialize(json).Single();

        Assert.False(Assert.Single(terrain.Markers).FollowsAnnotationStyle);
        Assert.False(Assert.IsType<ContourAnnotationDefinition>(Assert.Single(terrain.Annotations)).FollowsAnnotationStyle);
        Assert.False(Assert.IsType<SlopeAnalysisDefinition>(Assert.Single(terrain.Analyses)).AutoColorRange);
        Assert.Equal(TerrainDefinition.CurrentSchemaVersion, terrain.SchemaVersion);
    }

    [Fact]
    public void MigrationSteps_AreInVersionOrder_AndNeverPastTheCurrentSchema()
    {
        int[] versions = TerrainSchemaMigrations.Steps.Select(step => step.ToVersion).ToArray();

        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.All(versions, v => Assert.InRange(v, 1, TerrainDefinition.CurrentSchemaVersion));
        Assert.All(TerrainSchemaMigrations.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Description)));
        Assert.All(TerrainSchemaMigrations.JsonSteps, step => Assert.False(string.IsNullOrWhiteSpace(step.Description)));
    }
}
