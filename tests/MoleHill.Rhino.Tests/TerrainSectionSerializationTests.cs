using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainSectionSerializationTests
{
    [Fact]
    public void SerializeDeserialize_MultiTerrainSection_PreservesComparisonSettings()
    {
        Guid referenceId = Guid.NewGuid();
        var section = new TerrainSectionAnnotationDefinition
        {
            ComparisonTerrainIds = new List<Guid> { referenceId },
            CutFillReferenceTerrainId = referenceId,
            ShowCutFillRegions = true,
            CutColorArgb = unchecked((int)0xFF112233),
            FillColorArgb = unchecked((int)0xFF445566),
            CutFillOpacityPercent = 55
        };
        var terrain = new TerrainDefinition { Annotations = new List<AnnotationDefinition> { section } };
        terrain.EnsureBaseModifier();

        string json = TerrainSerializer.Serialize(new[] { terrain });
        TerrainDefinition restoredTerrain = Assert.Single(TerrainSerializer.Deserialize(json));
        var restored = Assert.IsType<TerrainSectionAnnotationDefinition>(Assert.Single(restoredTerrain.Annotations));

        Assert.Equal(new[] { referenceId }, restored.ComparisonTerrainIds);
        Assert.Equal(referenceId, restored.CutFillReferenceTerrainId);
        Assert.True(restored.ShowCutFillRegions);
        Assert.Equal(unchecked((int)0xFF112233), restored.CutColorArgb);
        Assert.Equal(unchecked((int)0xFF445566), restored.FillColorArgb);
        Assert.Equal(55, restored.CutFillOpacityPercent);
    }

    [Fact]
    public void Deserialize_LegacySection_DefaultsToOwnerOnly()
    {
        const string json = """
        {
          "schemaVersion": 25,
          "terrains": [
            {
              "schemaVersion": 25,
              "name": "Terrain",
              "analyses": [
                { "$type": "terrain-section", "label": "Section Cut" }
              ]
            }
          ]
        }
        """;

        TerrainDefinition terrain = Assert.Single(TerrainSerializer.Deserialize(json));
        var section = Assert.IsType<TerrainSectionAnnotationDefinition>(Assert.Single(terrain.Annotations));

        Assert.Empty(section.ComparisonTerrainIds);
        Assert.Null(section.CutFillReferenceTerrainId);
        Assert.Equal(40, section.CutFillOpacityPercent);
        Assert.Equal(TerrainDefinition.CurrentSchemaVersion, terrain.SchemaVersion);
    }
}
