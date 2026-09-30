using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>What a terrain rename moves: which layers, and which source layers follow them.</summary>
public class TerrainLayerRenamerTests
{
    private static LayerTemplateDefinition Template(params (string Path, string? Role)[] entries) =>
        new()
        {
            Version = 3,
            Name = "Mine",
            Entries = entries.Select(item => new LayerTemplateEntry
            {
                Path = item.Path,
                Roles = item.Role == null ? new List<string>() : new List<string> { item.Role }
            }).ToList()
        };

    private static IReadOnlyList<TerrainLayerMove> PlanRename(LayerTemplateDefinition template, string from, string to) =>
        TerrainLayerRenamer.Plan(LayerRoleTable.Build(template, from), LayerRoleTable.Build(template, to));

    [Fact]
    public void Plan_TokenRoot_IsOneMoveOfTheRootLayer()
    {
        var template = Template((TerrainLayerNaming.DefaultRoot + "::Terrain", "terrain"));

        var moves = PlanRename(template, "Old", "New");

        var move = Assert.Single(moves);
        Assert.Equal("MoleHill Old", move.OldPath);
        Assert.Equal("MoleHill New", move.NewName);
        Assert.Equal("MoleHill New", move.NewPath);
    }

    [Fact]
    public void Plan_LiteralRole_IsNotMoved()
    {
        var template = Template(("Site::Terrain", "terrain"));

        Assert.DoesNotContain(PlanRename(template, "Old", "New"), move => move.OldPath.StartsWith("Site"));
    }

    [Fact]
    public void Plan_TokenBelowTheRoot_RenamesOnlyThatLayer()
    {
        var template = Template(("Site::{terrain}::Terrain", "terrain"));

        var move = Assert.Single(PlanRename(template, "Old", "New"), item => item.OldPath.StartsWith("Site"));

        Assert.Equal("Site::Old", move.OldPath);
        Assert.Equal("Site::New", move.NewPath);
    }

    [Fact]
    public void Rewrite_FollowsTheMovedRoot_AndLeavesOtherLayersAlone()
    {
        var moves = new[] { new TerrainLayerMove("MoleHill Old", "MoleHill New", "MoleHill New") };

        Assert.Equal("MoleHill New::Inputs::Spots", TerrainLayerRenamer.Rewrite("MoleHill Old::Inputs::Spots", moves));
        Assert.Equal("MoleHill New", TerrainLayerRenamer.Rewrite("molehill old", moves));
        Assert.Null(TerrainLayerRenamer.Rewrite("MoleHill Older::Inputs", moves));
        Assert.Null(TerrainLayerRenamer.Rewrite("Survey::Spots", moves));
    }

    [Fact]
    public void RewriteSources_RepointsOwnedLayers_AndKeepsSharedOnes()
    {
        var terrain = new TerrainDefinition { Name = "New" };
        var zone = new CollageZoneDefinition { Name = "Lawn" };
        zone.Boundaries.ReplaceLayers(new[] { "MoleHill Old::Inputs::Spots", "Survey::Contours" });
        terrain.Zones.Add(zone);
        var moves = PlanRename(Template((TerrainLayerNaming.DefaultRoot + "::Terrain", "terrain")), "Old", "New");

        int rewritten = TerrainLayerRenamer.RewriteSources(terrain, moves);

        Assert.Equal(1, rewritten);
        Assert.Equal(
            new[] { "MoleHill New::Inputs::Spots", "Survey::Contours" },
            zone.Boundaries.LayerPaths);
    }
}
