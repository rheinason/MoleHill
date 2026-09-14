using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class ProjectToModifierDefinitionTests
{
    [Fact]
    public void Serialize_RoundTrip_PreservesTargetAndBlendSettings()
    {
        Guid meshId = Guid.NewGuid();
        Guid boundaryId = Guid.NewGuid();
        Guid terrainId = Guid.NewGuid();
        var modifier = new ProjectToModifierDefinition
        {
            TargetMesh = new SourceReferenceSet { ObjectIds = [meshId] },
            TargetTerrainId = terrainId,
            Boundaries = new SourceReferenceSet { ObjectIds = [boundaryId] },
            Strength = 0.35,
            FeatherDistance = 2.5
        };

        string json = TerrainSerializer.Serialize([new TerrainDefinition { Modifiers = [modifier] }]);
        var restored = Assert.Single(
            Assert.Single(TerrainSerializer.Deserialize(json)).Modifiers.OfType<ProjectToModifierDefinition>());

        Assert.Equal(meshId, Assert.Single(restored.TargetMesh.ObjectIds));
        Assert.Null(restored.TargetTerrainId);
        Assert.Equal(boundaryId, Assert.Single(restored.Boundaries.ObjectIds));
        Assert.Equal(0.35, restored.Strength, 10);
        Assert.Equal(2.5, restored.FeatherDistance, 10);
    }

    [Fact]
    public void Serialize_TerrainTargetWithoutMesh_PreservesTerrainId()
    {
        Guid terrainId = Guid.NewGuid();
        var modifier = new ProjectToModifierDefinition { TargetTerrainId = terrainId };

        string json = TerrainSerializer.Serialize([new TerrainDefinition { Modifiers = [modifier] }]);
        var restored = Assert.Single(
            Assert.Single(TerrainSerializer.Deserialize(json)).Modifiers.OfType<ProjectToModifierDefinition>());

        Assert.Equal(terrainId, restored.TargetTerrainId);
        Assert.False(restored.TargetMesh.HasReferences);
    }
}
