using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class TerrainUndoSnapshotTests
{
    [Fact]
    public void HasSameState_DifferentDescriptions_ReturnsTrue()
    {
        Guid selected = Guid.NewGuid();
        var first = new TerrainUndoSnapshot
        {
            Json = "[{\"name\":\"Terrain 1\"}]",
            SelectedTerrainId = selected,
            Description = "Edit terrain"
        };
        TerrainUndoSnapshot second = first.WithDescription("Redo terrain");

        Assert.True(first.HasSameState(second));
    }

    [Fact]
    public void HasSameState_DifferentJsonOrSelection_ReturnsFalse()
    {
        Guid selected = Guid.NewGuid();
        var baseline = new TerrainUndoSnapshot { Json = "[]", SelectedTerrainId = selected };

        Assert.False(baseline.HasSameState(baseline with { Json = "[{}]" }));
        Assert.False(baseline.HasSameState(baseline with { SelectedTerrainId = Guid.NewGuid() }));
    }

    [Fact]
    public void WithDescription_PreservesSerializedState()
    {
        Guid selected = Guid.NewGuid();
        var baseline = new TerrainUndoSnapshot { Json = "[{}]", SelectedTerrainId = selected };

        TerrainUndoSnapshot changed = baseline.WithDescription("Delete MoleHill Terrain");

        Assert.Equal("Delete MoleHill Terrain", changed.Description);
        Assert.Equal(baseline.Json, changed.Json);
        Assert.Equal(baseline.SelectedTerrainId, changed.SelectedTerrainId);
    }
}
