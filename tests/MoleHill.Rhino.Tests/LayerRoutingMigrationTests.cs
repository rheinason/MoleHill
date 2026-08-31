using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// A pre-schema-30 document that customised its output layers must keep rendering exactly as it did.
/// Nothing is retargeted: the customisation is carried into a template of the document's own.
/// </summary>
public class LayerRoutingMigrationTests
{
    /// <summary>
    /// The overwhelming majority of documents. Nothing was customised, so nothing is synthesized —
    /// a migrated template for every existing file would be clutter with no purpose.
    /// </summary>
    [Fact]
    public void ADocumentThatUsedTheDefaults_GetsNoTemplate()
    {
        var terrain = new TerrainDefinition
        {
            LegacyTerrainLayerPath = TerrainDefinition.DefaultTerrainLayerPath,
            LegacyAuxiliaryLayerPath = TerrainDefinition.DefaultAuxiliaryLayerPath,
            LegacyAnnotationLayerPath = TerrainDefinition.DefaultAnnotationLayerPath
        };

        Assert.False(LayerRoutingMigration.Plan(new[] { terrain }, "Site").HasTemplate);
    }

    [Fact]
    public void ADocumentWithNoLegacyValuesAtAll_GetsNoTemplate()
    {
        Assert.False(LayerRoutingMigration.Plan(new[] { new TerrainDefinition() }, "Site").HasTemplate);
    }

    /// <summary>
    /// The point of the migration: a customised path becomes a role binding, so the geometry keeps
    /// landing where it landed rather than snapping back to MoleHill's defaults.
    /// </summary>
    [Fact]
    public void ACustomisedPath_BecomesARoleBinding()
    {
        var terrain = new TerrainDefinition { LegacyAnnotationLayerPath = "Drawing::Site" };

        var result = LayerRoutingMigration.Plan(new[] { terrain }, "Site");

        Assert.True(result.HasTemplate);
        Assert.Equal("Site (migrated)", result.Template!.Name);

        var table = LayerRoleTable.Build(result.Template);
        Assert.Equal("Drawing::Site", table.Path(LayerRole.Annotation));
        // Everything under it follows, from the one binding.
        Assert.Equal("Drawing::Site::Contours::Major", table.Path(LayerRole.ContoursMajor));
        // Roles it never customised keep MoleHill's defaults.
        Assert.Equal(TerrainDefinition.DefaultTerrainLayerPath, table.Path(LayerRole.Terrain));
    }

    [Fact]
    public void PerCardOutputLayers_BecomeTheirOwnRoleBindings()
    {
        var terrain = new TerrainDefinition();
        terrain.Annotations.Add(new ContourAnnotationDefinition { LegacyOutputLayerPath = "Survey::Contours" });
        terrain.Analyses.Add(new WaterflowAnalysisDefinition { LegacyOutputLayerPath = "Survey::Flow" });
        terrain.Modifiers.Add(new RetainingWallModifierDefinition { LegacyOutputLayerPath = "Model::Walls" });

        var table = LayerRoleTable.Build(LayerRoutingMigration.Plan(new[] { terrain }, "Site").Template!);

        Assert.Equal("Survey::Contours", table.Path(LayerRole.Contours));
        Assert.Equal("Survey::Flow", table.Path(LayerRole.Waterflow));
        Assert.Equal("Model::Walls", table.Path(LayerRole.Walls));
    }

    /// <summary>
    /// The one real capability loss. Bindings are per role and the old data was per card, so two
    /// contour analyses pointed at different layers cannot both keep theirs — the first wins and the
    /// other is named, so the user learns about it from a message rather than from the drawing.
    /// </summary>
    [Fact]
    public void TwoCardsOfTheSameKindOnDifferentLayers_KeepTheFirstAndReportTheRest()
    {
        var terrain = new TerrainDefinition();
        terrain.Annotations.Add(new ContourAnnotationDefinition
        {
            Label = "Site contours",
            LegacyOutputLayerPath = "Survey::Contours"
        });
        terrain.Annotations.Add(new ContourAnnotationDefinition
        {
            Label = "Detail contours",
            LegacyOutputLayerPath = "Detail::Contours"
        });

        var result = LayerRoutingMigration.Plan(new[] { terrain }, "Site");

        Assert.Equal("Survey::Contours", LayerRoleTable.Build(result.Template!).Path(LayerRole.Contours));
        string conflict = Assert.Single(result.Conflicts);
        Assert.Contains("Detail contours", conflict, StringComparison.Ordinal);
        Assert.Contains("Detail::Contours", conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoCardsOnTheSameLayer_AreNotAConflict()
    {
        var terrain = new TerrainDefinition();
        terrain.Annotations.Add(new ContourAnnotationDefinition { LegacyOutputLayerPath = "Survey::Contours" });
        terrain.Annotations.Add(new ContourAnnotationDefinition { LegacyOutputLayerPath = "Survey::Contours" });

        Assert.Empty(LayerRoutingMigration.Plan(new[] { terrain }, "Site").Conflicts);
    }

    [Fact]
    public void AChosenAnnotationStyle_MovesOntoTheTemplate()
    {
        var terrain = new TerrainDefinition { LegacyAnnotationStyleName = "Site Plan" };

        var result = LayerRoutingMigration.Plan(new[] { terrain }, "Site");

        Assert.True(result.HasTemplate);
        var table = LayerRoleTable.Build(result.Template!);
        Assert.Equal("Site Plan", table.Appearance(LayerRole.Annotation).AnnotationStyleName);
        // The style alone must not move any geometry.
        Assert.Equal(TerrainDefinition.DefaultAnnotationLayerPath, table.Path(LayerRole.Annotation));
    }

    [Fact]
    public void ClearingLegacyValues_LeavesNoSecondAnswer()
    {
        var terrain = new TerrainDefinition
        {
            LegacyTerrainLayerPath = "Model::Proposed",
            LegacyAnnotationStyleName = "Site Plan"
        };
        terrain.Annotations.Add(new ContourAnnotationDefinition { LegacyOutputLayerPath = "Survey::Contours" });
        terrain.Modifiers.Add(new RetainingWallModifierDefinition { LegacyOutputLayerPath = "Model::Walls" });

        LayerRoutingMigration.ClearLegacyValues(new[] { terrain });

        Assert.Null(terrain.LegacyTerrainLayerPath);
        Assert.Null(terrain.LegacyAnnotationStyleName);
        Assert.Null(terrain.Annotations.OfType<ContourAnnotationDefinition>().Single().LegacyOutputLayerPath);
        Assert.Null(terrain.Modifiers.OfType<RetainingWallModifierDefinition>().Single().LegacyOutputLayerPath);
    }

    /// <summary>
    /// The legacy values have to survive a round trip, or a document opened and saved by this build
    /// would lose its customisation before the migration ever got to read it.
    /// </summary>
    [Fact]
    public void LegacyValues_RoundTripThroughJson()
    {
        var terrain = new TerrainDefinition { Name = "T", LegacyTerrainLayerPath = "Model::Proposed" };
        terrain.Annotations.Add(new ContourAnnotationDefinition { LegacyOutputLayerPath = "Survey::Contours" });

        TerrainDefinition restored = TerrainSerializer
            .Deserialize(TerrainSerializer.Serialize(new[] { terrain }))
            .Single();

        Assert.Equal("Model::Proposed", restored.LegacyTerrainLayerPath);
        Assert.Equal(
            "Survey::Contours",
            restored.Annotations.OfType<ContourAnnotationDefinition>().Single().LegacyOutputLayerPath);
    }
}
