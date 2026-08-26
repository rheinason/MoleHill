using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainDisplayStateTests
{
    [Fact]
    public void InvalidateRenderContent_DisplayOnlyChange_AdvancesRenderHash()
    {
        var state = new TerrainDisplayState();
        uint original = state.RenderHash;

        state.InvalidateRenderContent();

        Assert.NotEqual(original, state.RenderHash);
    }

    [Fact]
    public void InvalidatePreviewBounds_MutablePreviewChange_AdvancesRenderHash()
    {
        var state = new TerrainDisplayState();
        uint original = state.RenderHash;

        state.InvalidatePreviewBounds();

        Assert.NotEqual(original, state.RenderHash);
    }

    [Fact]
    public void HasRenderableContent_CustomMarkerBlock_ReturnsTrue()
    {
        var terrain = new TerrainDefinition
        {
            ShowTerrainMesh = false,
            ShowZoneMeshes = false
        };
        var state = new TerrainDisplayState();
        state.MarkerObjects.Add(new GeneratedRhinoObject
        {
            Name = "Marker",
            InstanceDefinitionName = "CustomMarker"
        });

        Assert.True(state.HasRenderableContent(terrain));
    }

    [Fact]
    public void HasRenderableContent_HiddenZoneBlockOnly_ReturnsFalse()
    {
        var terrain = new TerrainDefinition
        {
            ShowTerrainMesh = false,
            ShowZoneMeshes = false
        };
        var state = new TerrainDisplayState();
        state.ZoneObjects.Add(new GeneratedRhinoObject
        {
            Name = "Zone",
            InstanceDefinitionName = "ZoneBlock"
        });

        Assert.False(state.HasRenderableContent(terrain));
    }

    [Fact]
    public void HasRenderableContent_HiddenAnalysisBlockOnly_ReturnsFalse()
    {
        var terrain = new TerrainDefinition
        {
            ShowTerrainMesh = false,
            ShowZoneMeshes = false,
            ShowAnalysisOutputs = false
        };
        var state = new TerrainDisplayState();
        state.AuxiliaryObjects.Add(new GeneratedRhinoObject
        {
            Name = "Analysis",
            AnalysisId = Guid.NewGuid(),
            InstanceDefinitionName = "AnalysisBlock"
        });

        Assert.False(state.HasRenderableContent(terrain));
    }
}
