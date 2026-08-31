using System.Linq;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class AnnotationStyleTests
{
    [Fact]
    public void ResolveStyleName_BlankOrWhitespace_FallsBackToDefault()
    {
        Assert.Equal(AnnotationStyleService.DefaultStyleName, AnnotationStyleService.ResolveStyleName(null));
        Assert.Equal(AnnotationStyleService.DefaultStyleName, AnnotationStyleService.ResolveStyleName(""));
        Assert.Equal(AnnotationStyleService.DefaultStyleName, AnnotationStyleService.ResolveStyleName("   "));
        Assert.Equal("Site Plan", AnnotationStyleService.ResolveStyleName("  Site Plan  "));
    }

    [Fact]
    public void GetEffectiveTextHeight_MultipliesTextHeightByDimensionScale()
    {
        // 2.5 mm of paper text at 1:100 is 250 model units. DimensionScale is exactly what Rhino's
        // annotation scaling adjusts, so block sizing derived from it stays in step with text.
        Assert.Equal(250.0, AnnotationStyleService.GetEffectiveTextHeight(2.5, 100.0), 9);
    }

    [Fact]
    public void GetEffectiveTextHeight_NonPositiveHeight_FallsBackToAuthoredHeight()
    {
        Assert.Equal(
            AnnotationStyleService.AuthoredBlockTextHeight,
            AnnotationStyleService.GetEffectiveTextHeight(0.0, 100.0),
            9);
    }

    [Fact]
    public void GetBlockScale_UnitRelativeScale_MatchesStyleTextHeight()
    {
        // Marker blocks are authored with internal text at height 1.0, so an instance scale equal to the
        // style's text height makes the symbol's own text match surrounding labels exactly.
        var snapshot = new AnnotationStyleSnapshot { StyleName = "S", TextHeight = 250.0 };

        Assert.Equal(250.0, snapshot.GetBlockScale(1.0), 9);
    }

    [Fact]
    public void GetBlockScale_RelativeScale_MultipliesStyleDerivedSize()
    {
        var snapshot = new AnnotationStyleSnapshot { StyleName = "S", TextHeight = 250.0 };

        Assert.Equal(500.0, snapshot.GetBlockScale(2.0), 9);
        Assert.Equal(125.0, snapshot.GetBlockScale(0.5), 9);
    }

    [Fact]
    public void GetBlockScale_NonPositiveRelativeScale_TreatedAsUnit()
    {
        var snapshot = new AnnotationStyleSnapshot { StyleName = "S", TextHeight = 250.0 };

        Assert.Equal(250.0, snapshot.GetBlockScale(0.0), 9);
        Assert.Equal(250.0, snapshot.GetBlockScale(-3.0), 9);
    }

    [Fact]
    public void Deserialize_LegacyDocument_KeepsExplicitAnnotationSizes()
    {
        // Pre-schema-27 documents sized annotation from stored absolute heights. Migrating them to follow
        // the dimension style would silently resize every label and symbol in existing drawings.
        const string legacyJson = """
        {
          "schemaVersion": 26,
          "terrains": [
            {
              "schemaVersion": 26,
              "name": "Legacy",
              "analyses": [
                { "$type": "contour", "interval": 1.0, "labelTextHeight": 0.75 }
              ],
              "markers": [
                { "$type": "elevation", "name": "M1", "blockScale": 2.0 }
              ]
            }
          ]
        }
        """;

        TerrainDefinition terrain = TerrainSerializer.Deserialize(legacyJson).Single();

        Assert.All(terrain.Annotations, annotation => Assert.False(annotation.FollowsAnnotationStyle));
        Assert.All(terrain.Markers, marker => Assert.False(marker.FollowsAnnotationStyle));
    }

    [Fact]
    public void NewDefinitions_FollowTheAnnotationStyleByDefault()
    {
        Assert.True(new ContourAnnotationDefinition().FollowsAnnotationStyle);
        Assert.True(new ElevationMarkerDefinition().FollowsAnnotationStyle);
    }

    /// <summary>
    /// The annotation style moved onto the layer template, where it is per role. The old per-terrain
    /// value still round-trips so the schema 30 migration can carry a chosen style across rather
    /// than dropping it on load.
    /// </summary>
    [Fact]
    public void Roundtrip_PreservesTheLegacyAnnotationStyleForMigration()
    {
        var terrain = new TerrainDefinition { Name = "T", LegacyAnnotationStyleName = "Site Plan" };
        terrain.Annotations.Add(new ContourAnnotationDefinition());

        string json = TerrainSerializer.Serialize(new[] { terrain });
        TerrainDefinition restored = TerrainSerializer.Deserialize(json).Single();

        Assert.Equal("Site Plan", restored.LegacyAnnotationStyleName);
        Assert.True(restored.Annotations.All(annotation => annotation.FollowsAnnotationStyle));
    }

    /// <summary>The style a role's text binds to now comes from the template.</summary>
    [Fact]
    public void TheTemplate_CarriesTheAnnotationStyle()
    {
        var template = new LayerTemplateDefinition
        {
            Version = 1,
            Name = "Office",
            Entries = new List<LayerTemplateEntry>
            {
                new() { Roles = { "annotation" }, Path = "Drawing", AnnotationStyleName = "Site Plan" }
            }
        };

        var table = LayerRoleTable.Build(template);

        Assert.Equal("Site Plan", table.Appearance(LayerRole.Annotation).AnnotationStyleName);
        // Section labels inherit it unless the template gives them one of their own.
        Assert.Equal("Site Plan", table.Appearance(LayerRole.SectionsLabels).AnnotationStyleName);
    }
}
