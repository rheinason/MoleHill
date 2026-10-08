using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The per-terrain layer root: the token, what it resolves to, and how a template written before it
/// is carried across.
/// </summary>
public class TerrainLayerNamingTests
{
    private static LayerTemplateDefinition Template(int version, params (string Path, string? Role)[] entries) =>
        new()
        {
            Version = version,
            Name = "Mine",
            Entries = entries.Select(item => new LayerTemplateEntry
            {
                Path = item.Path,
                Roles = item.Role == null ? new List<string>() : new List<string> { item.Role }
            }).ToList()
        };

    [Theory]
    [InlineData("Hillside", "Hillside")]
    [InlineData("  North   Field ", "North Field")]
    [InlineData("A::B", "A B")]
    [InlineData("Plot (3)", "Plot 3")]
    [InlineData("{terrain}", "terrain")]
    [InlineData("   ", "Terrain")]
    [InlineData(null, "Terrain")]
    public void Sanitize_MakesANameALayerSegmentCanHold(string? name, string expected) =>
        Assert.Equal(expected, TerrainLayerNaming.Sanitize(name));

    [Fact]
    public void Resolve_ReplacesTheToken_AndLeavesItWhenThereIsNoName()
    {
        Assert.Equal("MoleHill Hillside::Terrain", TerrainLayerNaming.Resolve("MoleHill {terrain}::Terrain", "Hillside"));
        Assert.Equal("MoleHill {terrain}::Terrain", TerrainLayerNaming.Resolve("MoleHill {terrain}::Terrain", null));
        Assert.Equal("Site::Terrain", TerrainLayerNaming.Resolve("Site::Terrain", "Hillside"));
    }

    [Fact]
    public void Table_ForTwoTerrains_RoutesEachUnderItsOwnRoot()
    {
        var template = Template(3, (TerrainLayerNaming.DefaultRoot + "::Terrain", "terrain"));

        var north = LayerRoleTable.Build(template, "North");
        var south = LayerRoleTable.Build(template, "South");

        Assert.Equal("MoleHill North::Terrain", north.Path(LayerRole.Terrain));
        Assert.Equal("MoleHill South::Terrain", south.Path(LayerRole.Terrain));
        Assert.Equal("MoleHill North::Output::Annotation", north.Path(LayerRole.Annotation));
        Assert.Equal("MoleHill South::Output::Zones::Site", south.Path(LayerRole.Zones, "Site"));
        Assert.NotEqual(north.Fingerprint, south.Fingerprint);
    }

    [Fact]
    public void Table_PlainTemplateLayers_AreResolvedToo()
    {
        var template = Template(3, (TerrainLayerNaming.DefaultRoot + "::Inputs::Spots", null));

        var table = LayerRoleTable.Build(template, "North");

        Assert.Contains(table.AllLayers, layer => layer.Path == "MoleHill North::Inputs::Spots");
    }

    [Fact]
    public void Table_RenamingATerrain_ChangesTheFingerprint_SoItsOutputReroutes()
    {
        var template = Template(3, (TerrainLayerNaming.DefaultRoot + "::Terrain", "terrain"));

        Assert.NotEqual(
            LayerRoleTable.Build(template, "Before").Fingerprint,
            LayerRoleTable.Build(template, "After").Fingerprint);
    }

    [Fact]
    public void Table_LiteralTemplate_IsTheSameForEveryTerrain()
    {
        var template = Template(3, ("Site::Terrain", "terrain"));

        Assert.Equal(
            LayerRoleTable.Build(template, "North").Path(LayerRole.Terrain),
            LayerRoleTable.Build(template, "South").Path(LayerRole.Terrain));
    }

    [Fact]
    public void Rebase_MovesAPathBetweenRoots_AndIgnoresOthers()
    {
        Assert.Equal(
            "MoleHill New::Inputs::Spots",
            TerrainLayerNaming.Rebase("MoleHill Old::Inputs::Spots", "Old", "New"));
        Assert.Equal("MoleHill New", TerrainLayerNaming.Rebase("MoleHill Old", "Old", "New"));
        Assert.Null(TerrainLayerNaming.Rebase("Survey::Spots", "Old", "New"));
        Assert.Null(TerrainLayerNaming.Rebase("MoleHill Older::Spots", "Old", "New"));
    }

    [Fact]
    public void IsUnderRoot_OnlyCountsTheTerrainsOwnTree()
    {
        Assert.True(TerrainLayerNaming.IsUnderRoot("MoleHill North::Inputs::Spots", "North"));
        Assert.True(TerrainLayerNaming.IsUnderRoot("molehill north", "North"));
        Assert.False(TerrainLayerNaming.IsUnderRoot("MoleHill North 2::Inputs", "North"));
        Assert.False(TerrainLayerNaming.IsUnderRoot("Survey::Spots", "North"));
        Assert.False(TerrainLayerNaming.IsUnderRoot(null, "North"));
    }

    [Fact]
    public void NextFreeName_CountsUpPastNamesThatShareALayer()
    {
        var taken = new[] { "Hill", "Hill 2" };

        string next = TerrainLayerNaming.NextFreeName(
            "Hill", name => taken.Any(other => TerrainLayerNaming.SameRoot(other, name)));

        Assert.Equal("Hill 3", next);
        Assert.True(TerrainLayerNaming.SameRoot("Hill (A)", "hill a"));
    }

    [Fact]
    public void Upgrade_ShippedLegacyLayout_MovesToThePerTerrainRoot()
    {
        var legacy = Template(
            2,
            ("MoleHill::Terrain", "terrain"),
            ("MoleHill::Annotation::Contours", "contours"),
            ("MoleHill::Inputs::Spots", null));

        var upgraded = LayerTemplateStore.NormalizeTemplates(new List<LayerTemplateDefinition> { legacy }).Single();

        Assert.Equal(3, upgraded.Version);
        Assert.Equal(
            new[]
            {
                "MoleHill {terrain}::Terrain",
                "MoleHill {terrain}::Annotation::Contours",
                "MoleHill {terrain}::Inputs::Spots"
            },
            upgraded.Entries.Select(entry => entry.Path));
    }

    [Fact]
    public void Upgrade_ACustomRoot_IsLeftExactlyAsTheUserWroteIt()
    {
        var custom = Template(2, ("Site::Terrain", "terrain"), ("MoleHill::Inputs::Spots", null));

        var upgraded = LayerTemplateStore.NormalizeTemplates(new List<LayerTemplateDefinition> { custom }).Single();

        Assert.Equal(new[] { "Site::Terrain", "MoleHill::Inputs::Spots" }, upgraded.Entries.Select(entry => entry.Path));
    }

    [Fact]
    public void Upgrade_AlreadyCurrent_IsNotTouched()
    {
        var current = Template(3, ("MoleHill::Terrain", "terrain"));

        var upgraded = LayerTemplateStore.NormalizeTemplates(new List<LayerTemplateDefinition> { current }).Single();

        Assert.Equal("MoleHill::Terrain", upgraded.Entries[0].Path);
    }
}
