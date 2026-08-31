using System.Linq;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Schema 31 moved annotations out of <c>TerrainDefinition.Analyses</c> into their own family. Every
/// document saved before that carries both kinds in one "analyses" array, and annotation discriminators
/// are no longer registered under <see cref="AnalysisDefinition"/> — so without the pre-bind split in
/// <c>TerrainSerializer</c> those documents fail to load outright. These lock that path down.
/// </summary>
public class AnnotationFamilyMigrationTests
{
    private const string LegacyMixedJson = """
    {
      "schemaVersion": 30,
      "terrains": [
        {
          "schemaVersion": 30,
          "terrainId": "22222222-2222-2222-2222-222222222222",
          "name": "Legacy",
          "showAnalysisOutputs": true,
          "lastAnalysis": { "generatedOutputCount": 42 },
          "analyses": [
            { "$type": "slope", "id": "33333333-3333-3333-3333-333333333333", "label": "Slope" },
            { "$type": "contour", "id": "44444444-4444-4444-4444-444444444444", "label": "Contours", "interval": 2.5 },
            { "$type": "earthwork", "id": "55555555-5555-5555-5555-555555555555", "label": "Earthworks" },
            { "$type": "longitudinal-section", "id": "66666666-6666-6666-6666-666666666666", "label": "Section Along Curve" }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void LegacyMixedDocument_SplitsEachTypeIntoItsOwnFamily()
    {
        var terrain = TerrainSerializer.Deserialize(LegacyMixedJson).Single();

        Assert.Equal(
            new[] { typeof(SlopeAnalysisDefinition), typeof(EarthworkAnalysisDefinition) },
            terrain.Analyses.Select(a => a.GetType()).ToArray());
        Assert.Equal(
            new[] { typeof(ContourAnnotationDefinition), typeof(LongitudinalSectionAnnotationDefinition) },
            terrain.Annotations.Select(a => a.GetType()).ToArray());
    }

    [Fact]
    public void LegacyMixedDocument_PreservesEachDefinitionsSettings()
    {
        var terrain = TerrainSerializer.Deserialize(LegacyMixedJson).Single();

        var contour = Assert.IsType<ContourAnnotationDefinition>(terrain.Annotations[0]);
        Assert.Equal(2.5, contour.Interval, 9);
        Assert.Equal("Contours", contour.Label);
        Assert.Equal("44444444-4444-4444-4444-444444444444", contour.Id.ToString());
    }

    [Fact]
    public void LegacyLastAnalysisSummary_IsRetainedForBothFamilies()
    {
        var terrain = TerrainSerializer.Deserialize(LegacyMixedJson).Single();

        Assert.Equal(4, terrain.LastAnalysisResults.Count);
        Assert.All(terrain.Analyses.Cast<ITerrainContentItem>().Concat(terrain.Annotations), item =>
        {
            var summary = terrain.LastAnalysisResults.Single(result => result.AnalysisId == item.Id);
            Assert.Equal(42, summary.GeneratedOutputCount);
        });
    }

    /// <summary>
    /// An annotation has no terrain-level visibility gate at all: it is the drawing. Only its own card
    /// checkbox can silence it. This is the whole original bug — a document with analysis output hidden
    /// must still draw its contours and sections.
    /// </summary>
    [Fact]
    public void LegacyDocumentWithAnalysisOutputHidden_StillDrawsItsAnnotations()
    {
        string json = LegacyMixedJson.Replace("\"showAnalysisOutputs\": true", "\"showAnalysisOutputs\": false");

        var terrain = TerrainSerializer.Deserialize(json).Single();

        Assert.False(terrain.ShowAnalysisOutputs);
        var contour = terrain.Annotations.OfType<ContourAnnotationDefinition>().Single();
        Assert.True(TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(
            terrain,
            new GeneratedRhinoObject { Name = "Contour", Role = LayerRole.Contours, AnalysisId = contour.Id }));
    }

    [Fact]
    public void HidingAnalysisOutput_HidesAnalysisOutput_ButNotAnnotations()
    {
        var terrain = new TerrainDefinition { ShowAnalysisOutputs = false };
        var contour = new ContourAnnotationDefinition();
        var slope = new SlopeAnalysisDefinition();
        terrain.Annotations.Add(contour);
        terrain.Analyses.Add(slope);

        // No Geometry: the visibility gate reads only Kind and AnalysisId, and constructing real Rhino
        // geometry would drag the native runtime into a pure model test.
        Assert.True(TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(
            terrain,
            new GeneratedRhinoObject { Name = "Contour", Role = LayerRole.Contours, AnalysisId = contour.Id }));
        Assert.False(TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(
            terrain,
            new GeneratedRhinoObject { Name = "Slope", Role = LayerRole.Contours, AnalysisId = slope.Id }));
    }

    /// <summary>The card checkbox is the only thing that silences an annotation.</summary>
    [Fact]
    public void DisabledAnnotationCard_IsTheOnlyWayToSilenceIt()
    {
        var terrain = new TerrainDefinition();
        var contour = new ContourAnnotationDefinition { IsEnabled = false };
        terrain.Annotations.Add(contour);

        Assert.False(TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(
            terrain,
            new GeneratedRhinoObject { Name = "Contour", Role = LayerRole.Contours, AnalysisId = contour.Id }));
    }

    [Fact]
    public void CurrentDocument_RoundTripsWithoutReSplitting()
    {
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(new SlopeAnalysisDefinition());
        terrain.Annotations.Add(new ContourAnnotationDefinition { Interval = 5.0 });

        var restored = TerrainSerializer.Deserialize(TerrainSerializer.Serialize(new[] { terrain })).Single();

        Assert.IsType<SlopeAnalysisDefinition>(Assert.Single(restored.Analyses));
        var contour = Assert.IsType<ContourAnnotationDefinition>(Assert.Single(restored.Annotations));
        Assert.Equal(5.0, contour.Interval, 9);
    }

    /// <summary>
    /// Discriminators had to survive the move: they are the identity a saved document refers to, so
    /// renaming the C# types must not have renamed them.
    /// </summary>
    [Theory]
    [InlineData("contour", typeof(ContourAnnotationDefinition))]
    [InlineData("terrain-section", typeof(TerrainSectionAnnotationDefinition))]
    [InlineData("cross-section-station", typeof(CrossSectionStationAnnotationDefinition))]
    [InlineData("longitudinal-section", typeof(LongitudinalSectionAnnotationDefinition))]
    [InlineData("curve-elevation-label", typeof(CurveElevationLabelAnnotationDefinition))]
    [InlineData("curve-slope-label", typeof(CurveSlopeLabelAnnotationDefinition))]
    [InlineData("projected-elevation-label", typeof(ProjectedElevationLabelAnnotationDefinition))]
    [InlineData("point-slope-label", typeof(PointSlopeLabelAnnotationDefinition))]
    [InlineData("slope-arrows", typeof(SlopeArrowAnnotationDefinition))]
    [InlineData("grade-callout", typeof(GradeBetweenPointsAnnotationDefinition))]
    public void AnnotationKinds_AreUnchangedFromWhenTheyLivedInTheAnalysisFamily(string kind, System.Type expected)
    {
        Assert.IsType(expected, AnnotationTypeRegistry.Create(kind));
        Assert.Null(AnalysisTypeRegistry.Create(kind));
    }
}
