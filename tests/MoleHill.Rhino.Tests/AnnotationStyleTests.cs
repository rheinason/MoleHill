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

        Assert.All(terrain.Analyses, analysis => Assert.False(analysis.FollowsAnnotationStyle));
        Assert.All(terrain.Markers, marker => Assert.False(marker.FollowsAnnotationStyle));
    }

    [Fact]
    public void NewDefinitions_FollowTheAnnotationStyleByDefault()
    {
        Assert.True(new ContourAnalysisDefinition().FollowsAnnotationStyle);
        Assert.True(new ElevationMarkerDefinition().FollowsAnnotationStyle);
    }

    [Fact]
    public void Roundtrip_PreservesAnnotationStyleName()
    {
        var terrain = new TerrainDefinition { Name = "T", AnnotationStyleName = "Site Plan" };

        string json = TerrainSerializer.Serialize(new[] { terrain });
        TerrainDefinition restored = TerrainSerializer.Deserialize(json).Single();

        Assert.Equal("Site Plan", restored.AnnotationStyleName);
        Assert.True(restored.Analyses.All(a => a.FollowsAnnotationStyle));
    }
}
