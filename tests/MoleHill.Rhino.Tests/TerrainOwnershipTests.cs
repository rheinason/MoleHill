using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>The owned-inputs rule a duplicate follows: copy what sits under the terrain, share the rest.</summary>
public class TerrainOwnershipTests
{
    private static LayerRoleTable ShippedTable(string terrainName) =>
        LayerRoleTable.Build(
            new LayerTemplateDefinition
            {
                Version = 3,
                Name = "Shipped",
                Entries = new List<LayerTemplateEntry>
                {
                    new() { Path = TerrainLayerNaming.DefaultRoot + "::Terrain", Roles = new List<string> { "terrain" } },
                    new() { Path = TerrainLayerNaming.DefaultRoot + "::Inputs::Spots" }
                }
            },
            terrainName);

    [Theory]
    [InlineData("MoleHill North::Inputs::Spots", true)]
    [InlineData("MoleHill North::My Drawing", true)]
    [InlineData("MoleHill North::Terrain", false)]
    [InlineData("MoleHill North::Annotation::Contours", false)]
    [InlineData("MoleHill South::Inputs::Spots", false)]
    [InlineData("Survey::Spots", false)]
    public void IsOwnedInputLayer_OwnsInputsUnderTheRoot_NotOutputOrStrangers(string layer, bool owned) =>
        Assert.Equal(owned, TerrainOwnership.IsOwnedInputLayer(layer, "North", ShippedTable("North")));

    [Fact]
    public void Remap_CopiesOwnedLayersAndObjects_AndSharesEverythingElse()
    {
        Guid owned = Guid.NewGuid();
        Guid copy = Guid.NewGuid();
        Guid shared = Guid.NewGuid();
        var zone = new CollageZoneDefinition { Name = "Lawn" };
        zone.Boundaries.ReplaceLayers(new[]
        {
            "MoleHill North::Inputs::Spots",
            "MoleHill North::Terrain",
            "Survey::Contours"
        });
        zone.Boundaries.ReplaceObjects(new[] { owned, shared });
        var clone = new TerrainDefinition { Name = "North Copy" };
        clone.Zones.Add(zone);

        TerrainOwnership.Remap(clone, "North", ShippedTable("North"), new Dictionary<Guid, Guid> { [owned] = copy });

        Assert.Equal(
            new[] { "MoleHill North Copy::Inputs::Spots", "MoleHill North::Terrain", "Survey::Contours" },
            zone.Boundaries.LayerPaths);
        Assert.Equal(new[] { copy, shared }, zone.Boundaries.ObjectIds);
    }
}
