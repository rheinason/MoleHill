using System.Linq;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Schema 34 merged five label cards into Spot Heights and Spot Slope, each with a Points / Curves / Grid
/// placement. A saved card must come back as the same card - same id, name, place in the stack, enabled
/// state, sources, block and formatting - only under its new type, with the placement it used to be.
/// One characterization per old type, from JSON written the way schema 33 wrote it.
/// </summary>
public class SpotLabelMigrationTests
{
    private const string ObjectId = "0b0b0b0b-0000-0000-0000-000000000001";

    // Every field a block-label card carries, set away from its default so a field the migration drops
    // shows up as a default.
    private static string Card(string kind, string id, string label, bool enabled, string extra) => $$"""
        {
          "$type": "{{kind}}",
          {{extra}}
          "sources": { "objectIds": [ "{{ObjectId}}" ], "layerPaths": [ "Survey::Spots" ] },
          "id": "{{id}}",
          "label": "{{label}}",
          "isEnabled": {{(enabled ? "true" : "false")}},
          "followsAnnotationStyle": false,
          "colorArgb": -16744448,
          "blockDefinitionName": "Office Label",
          "blockScale": 2.5,
          "attributePrefix": "+",
          "attributeSuffix": " m",
          "valueFormat": "F3"
        }
        """;

    private static string Document(int schemaVersion, string arrayName, params string[] cards) => $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "terrains": [
            {
              "schemaVersion": {{schemaVersion}},
              "terrainId": "22222222-2222-2222-2222-222222222222",
              "name": "Site",
              "{{arrayName}}": [ {{string.Join(", ", cards)}} ]
            }
          ]
        }
        """;

    private static readonly string AllFive = Document(
        33,
        "annotations",
        Card("projected-elevation-label", "10000000-0000-0000-0000-000000000001", "Levels", true, ""),
        Card("curve-elevation-label", "10000000-0000-0000-0000-000000000002", "Kerb Levels", false, "\"interval\": 7.5,"),
        Card("point-slope-label", "10000000-0000-0000-0000-000000000003", "Falls", true, "\"unit\": 2, \"flipDirection\": true,"),
        Card("curve-slope-label", "10000000-0000-0000-0000-000000000004", "Path Falls", true, "\"interval\": 4, \"unit\": 0, \"flipDirection\": true,"),
        Card("slope-arrows", "10000000-0000-0000-0000-000000000005", "Flow Arrows", true, "\"gridSpacing\": 3, \"unit\": 3, \"flipDirection\": true,"));

    private static List<AnnotationDefinition> Load(string json) =>
        TerrainSerializer.Deserialize(json).Single().Annotations;

    private static void AssertSameCard(BlockAttributeAnnotationDefinition card, string id, string label, bool enabled)
    {
        Assert.Equal(id, card.Id.ToString());
        Assert.Equal(label, card.Label);
        Assert.Equal(enabled, card.IsEnabled);
        Assert.False(card.FollowsAnnotationStyle);
        Assert.Equal(-16744448, card.ColorArgb);
        Assert.Equal("Office Label", card.BlockDefinitionName);
        Assert.Equal(2.5, card.BlockScale, 9);
        Assert.Equal("+", card.AttributePrefix);
        Assert.Equal(" m", card.AttributeSuffix);
        Assert.Equal("F3", card.ValueFormat);
    }

    private static void AssertSources(SourceReferenceSet sources)
    {
        Assert.Equal(ObjectId, Assert.Single(sources.ObjectIds).ToString());
        Assert.Equal("Survey::Spots", Assert.Single(sources.LayerPaths));
    }

    [Fact]
    public void AllFiveOldCards_KeepTheirPlaceInTheStack()
    {
        var cards = Load(AllFive);

        Assert.Equal(
            new[]
            {
                typeof(SpotHeightsAnnotationDefinition), typeof(SpotHeightsAnnotationDefinition),
                typeof(SpotSlopeAnnotationDefinition), typeof(SpotSlopeAnnotationDefinition),
                typeof(SpotSlopeAnnotationDefinition),
            },
            cards.Select(card => card.GetType()).ToArray());
        Assert.Equal(
            Enumerable.Range(1, 5).Select(i => $"10000000-0000-0000-0000-00000000000{i}"),
            cards.Select(card => card.Id.ToString()));
    }

    [Fact]
    public void SpotHeightsPoints_BecomesSpotHeightsAtPoints()
    {
        var card = Assert.IsType<SpotHeightsAnnotationDefinition>(Load(AllFive)[0]);

        Assert.Equal(SpotPlacement.Points, card.Placement);
        AssertSameCard(card, "10000000-0000-0000-0000-000000000001", "Levels", enabled: true);
        AssertSources(card.Sources);
        Assert.False(card.CurveSources.HasReferences);
        Assert.False(card.Boundaries.HasReferences);
    }

    [Fact]
    public void SpotHeightsCurve_BecomesSpotHeightsAlongCurves()
    {
        var card = Assert.IsType<SpotHeightsAnnotationDefinition>(Load(AllFive)[1]);

        Assert.Equal(SpotPlacement.Curves, card.Placement);
        AssertSameCard(card, "10000000-0000-0000-0000-000000000002", "Kerb Levels", enabled: false);
        AssertSources(card.CurveSources);
        Assert.False(card.Sources.HasReferences);
        Assert.Equal(7.5, card.Interval, 9);
    }

    [Fact]
    public void SpotSlopePoints_BecomesSpotSlopeAtPoints()
    {
        var card = Assert.IsType<SpotSlopeAnnotationDefinition>(Load(AllFive)[2]);

        Assert.Equal(SpotPlacement.Points, card.Placement);
        AssertSameCard(card, "10000000-0000-0000-0000-000000000003", "Falls", enabled: true);
        AssertSources(card.Sources);
        Assert.Equal(SlopeAnalyzer.SlopeUnit.Degrees, card.Unit);
        Assert.False(card.FlipDirection); // inverted: arrows now point downhill unflipped, so the drawing is kept
    }

    [Fact]
    public void SpotSlopeCurve_BecomesSpotSlopeAlongCurves()
    {
        var card = Assert.IsType<SpotSlopeAnnotationDefinition>(Load(AllFive)[3]);

        Assert.Equal(SpotPlacement.Curves, card.Placement);
        AssertSameCard(card, "10000000-0000-0000-0000-000000000004", "Path Falls", enabled: true);
        AssertSources(card.CurveSources);
        Assert.False(card.Sources.HasReferences);
        Assert.Equal(4.0, card.Interval, 9);
        Assert.Equal(SlopeAnalyzer.SlopeUnit.Ratio, card.Unit);
        Assert.False(card.FlipDirection); // inverted: arrows now point downhill unflipped, so the drawing is kept
    }

    [Fact]
    public void FlowArrows_BecomesSpotSlopeOnAGrid_WithItsBoundaries()
    {
        var card = Assert.IsType<SpotSlopeAnnotationDefinition>(Load(AllFive)[4]);

        Assert.Equal(SpotPlacement.Grid, card.Placement);
        AssertSameCard(card, "10000000-0000-0000-0000-000000000005", "Flow Arrows", enabled: true);
        AssertSources(card.Boundaries);
        Assert.False(card.Sources.HasReferences);
        Assert.Equal(3.0, card.GridSpacing, 9);
        Assert.Equal(SlopeAnalyzer.SlopeUnit.Promille, card.Unit);
        Assert.False(card.FlipDirection); // inverted: arrows now point downhill unflipped, so the drawing is kept
    }

    /// <summary>
    /// A document from before schema 31 still has these cards in the shared "analyses" array. The
    /// schema-31 split has to recognise the retired kinds as annotations, or they bind as analyses and
    /// the whole document fails to load.
    /// </summary>
    [Fact]
    public void PreSchema31Document_SplitsThenMergesTheRetiredKinds()
    {
        string json = Document(
            30,
            "analyses",
            Card("slope-arrows", "10000000-0000-0000-0000-000000000005", "Flow Arrows", true, "\"gridSpacing\": 3,"),
            Card("curve-elevation-label", "10000000-0000-0000-0000-000000000002", "Kerb Levels", true, "\"interval\": 7.5,"));

        var terrain = TerrainSerializer.Deserialize(json).Single();

        Assert.Empty(terrain.Analyses);
        var arrows = Assert.IsType<SpotSlopeAnnotationDefinition>(terrain.Annotations[0]);
        Assert.Equal(SpotPlacement.Grid, arrows.Placement);
        AssertSources(arrows.Boundaries);
        var levels = Assert.IsType<SpotHeightsAnnotationDefinition>(terrain.Annotations[1]);
        Assert.Equal(SpotPlacement.Curves, levels.Placement);
        AssertSources(levels.CurveSources);
    }

    [Fact]
    public void MigratedDocument_RoundTripsUnchanged()
    {
        var first = TerrainSerializer.Deserialize(AllFive);
        string saved = TerrainSerializer.Serialize(first);
        var second = TerrainSerializer.Deserialize(saved).Single().Annotations;

        Assert.DoesNotContain("slope-arrows", saved);
        Assert.Equal(
            first.Single().Annotations.Select(card => (card.GetType(), card.Id, ((SpotLabelAnnotationDefinition)card).Placement)),
            second.Select(card => (card.GetType(), card.Id, ((SpotLabelAnnotationDefinition)card).Placement)));
    }

    /// <summary>
    /// 1.6.0 drew slope arrows uphill unless flipped; 1.7.0 draws them downhill. A card that was not flipped
    /// comes across flipped, so it still points uphill on screen.
    /// </summary>
    [Fact]
    public void UnflippedSlopeCard_IsFlippedSoItsArrowsStillPointTheSameWay()
    {
        string json = Document(33, "annotations",
            Card("slope-arrows", "10000000-0000-0000-0000-000000000006", "Arrows", true, "\"gridSpacing\": 3,"));

        var card = Assert.IsType<SpotSlopeAnnotationDefinition>(Load(json).Single());

        Assert.True(card.FlipDirection);
    }

    /// <summary>Every source set is the card's data, active or not: a layer rename must reach all three.</summary>
    [Fact]
    public void EveryPlacementsSources_AreEnumerated()
    {
        var card = new SpotSlopeAnnotationDefinition();

        Assert.Equal(
            new[] { card.Sources, card.CurveSources, card.Boundaries },
            card.EnumerateSourceSets().ToArray());
    }
}
