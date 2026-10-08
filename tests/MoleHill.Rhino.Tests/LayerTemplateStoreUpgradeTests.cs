using System.Text.Json;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Loading templates written before role bindings existed, and the shipped template itself.
/// </summary>
public class LayerTemplateStoreUpgradeTests
{
    private static List<LayerTemplateDefinition> Normalize(params LayerTemplateDefinition[] templates) =>
        LayerTemplateStore.NormalizeTemplates(templates.ToList());

    /// <summary>
    /// The whole point of the upgrade: a version 0 file keyed only by layer path comes back with its
    /// roles bound, so an existing user's template starts routing output without them touching it.
    /// </summary>
    [Fact]
    public void LegacyTemplate_AcquiresItsRoleBindingsFromItsLayerPaths()
    {
        var legacy = new LayerTemplateDefinition
        {
            Name = "MoleHill Terrain",
            Entries = new List<LayerTemplateEntry>
            {
                new() { Path = "MoleHill::Terrain", ColorArgb = 1, PlotWeight = 0.18 },
                new() { Path = "MoleHill::Annotation::Contours::Major", PlotWeight = 0.35 },
                new() { Path = "MoleHill::Inputs::Spots", PlotWeight = 0.18 }
            }
        };

        var upgraded = Normalize(legacy).Single();

        Assert.Equal(3, upgraded.Version);
        // The user's own layout is kept as written: only the shipped tree gained the Output branch.
        Assert.Equal(new[] { "terrain" }, Find(upgraded, TerrainLayerNaming.DefaultRoot + "::Terrain").Roles);
        Assert.Equal(
            new[] { "contours-major" },
            Find(upgraded, TerrainLayerNaming.DefaultRoot + "::Annotation::Contours::Major").Roles);

        // A layer the user draws on is not an output destination and must stay unbound.
        Assert.Empty(Find(upgraded, TerrainLayerNaming.DefaultRoot + "::Inputs::Spots").Roles);
    }

    /// <summary>
    /// A layer used to carry at most one role, written as a singular "role" property. Those files
    /// have to keep loading, with the single value folded into the list a layer has now.
    /// </summary>
    [Fact]
    public void ASingleRoleFromAnOlderFile_BecomesAOneItemList()
    {
        var older = new LayerTemplateDefinition
        {
            Version = 1,
            Name = "Older",
            Entries = new List<LayerTemplateEntry>
            {
                new() { Path = "Drawing::Site", LegacyRole = "annotation" }
            }
        };

        var entry = Normalize(older).Single().Entries.Single();

        Assert.Equal(new[] { "annotation" }, entry.Roles);
        // The singular form is read once and never written back, so there is one place it lives.
        Assert.Null(entry.LegacyRole);
    }

    [Fact]
    public void SeveralRolesOnOneLayer_SurviveNormalization()
    {
        var template = new LayerTemplateDefinition
        {
            Version = 2,
            Name = "Shared",
            Entries = new List<LayerTemplateEntry>
            {
                new() { Path = "Drawing::Section", Roles = { "sections", "sections-grid", "sections-ticks" } }
            }
        };

        Assert.Equal(
            new[] { "sections", "sections-grid", "sections-ticks" },
            Normalize(template).Single().Entries.Single().Roles);
    }

    /// <summary>
    /// Zero used to be what the old serializer wrote for an unset print width. Keeping it would
    /// turn every inherited weight into a deliberate hairline.
    /// </summary>
    [Fact]
    public void LegacyZeroPrintWidth_BecomesInherit()
    {
        var legacy = new LayerTemplateDefinition
        {
            Name = "Legacy",
            Entries = new List<LayerTemplateEntry> { new() { Path = "Some::Layer", PlotWeight = 0.0 } }
        };

        Assert.Null(Normalize(legacy).Single().Entries.Single().PlotWeight);
    }

    [Fact]
    public void AlreadyUpgradedTemplate_IsLeftAlone()
    {
        var current = new LayerTemplateDefinition
        {
            Version = 2,
            Name = "Current",
            // A deliberate hairline on a layer that happens to sit at a role's default path.
            Entries = new List<LayerTemplateEntry>
            {
                new() { Path = TerrainDefinition.DefaultTerrainLayerPath, PlotWeight = 0.0 }
            }
        };

        var entry = Normalize(current).Single().Entries.Single();
        Assert.Equal(0.0, entry.PlotWeight);
        Assert.Empty(entry.Roles);
    }

    [Fact]
    public void ARoleBoundTwice_KeepsOnlyTheFirstBinding()
    {
        var template = new LayerTemplateDefinition
        {
            Version = 2,
            Name = "Conflicted",
            Entries = new List<LayerTemplateEntry>
            {
                new() { Path = "First", Roles = { "contours-major" } },
                new() { Path = "Second", Roles = { "contours-major" } }
            }
        };

        var entries = Normalize(template).Single().Entries;
        Assert.Equal(new[] { "contours-major" }, entries[0].Roles);
        Assert.Empty(entries[1].Roles);
        // The layer itself survives; only its claim on the role is dropped.
        Assert.Equal("Second", entries[1].Path);
    }

    [Fact]
    public void ShippedTemplate_BindsEveryRoleExactlyOnce()
    {
        var shipped = new LayerTemplateStore().GetDefaultTemplates().Single();

        var boundRoles = shipped.Entries
            .Where(entry => entry.Roles.Count > 0)
            .SelectMany(entry => entry.Roles)
            .ToList();

        Assert.Equal(boundRoles.Count, boundRoles.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Roles that share their parent's layer contribute no entry of their own, and resolve
        // through the parent instead.
        var sharing = LayerRoleRegistry.All
            .Where(d => d.Parent.HasValue && d.RelativeSuffix.Length == 0)
            .Select(d => d.Id);

        Assert.Equal(
            LayerRoleRegistry.All.Select(d => d.Id).Except(sharing).OrderBy(id => id),
            boundRoles.OrderBy(id => id));
    }

    /// <summary>
    /// The shipped template must resolve to exactly the built-in defaults, or applying it would
    /// silently restyle a document away from what an unbound build produces.
    /// </summary>
    [Fact]
    public void ShippedTemplate_ResolvesToTheRegistryDefaults()
    {
        var shipped = new LayerTemplateStore().GetDefaultTemplates().Single();
        var table = LayerRoleTable.Build(shipped);

        Assert.Equal(LayerRoleTable.Default.Fingerprint, table.Fingerprint);
    }

    [Fact]
    public void RoundTrip_ThroughJson_PreservesRolesAndAppearance()
    {
        var shipped = new LayerTemplateStore().GetDefaultTemplates().ToList();

        string json = JsonSerializer.Serialize(shipped);
        var restored = LayerTemplateStore.NormalizeTemplates(
            JsonSerializer.Deserialize<List<LayerTemplateDefinition>>(json)!);

        Assert.Equal(
            LayerRoleTable.Build(shipped.Single()).Fingerprint,
            LayerRoleTable.Build(restored.Single()).Fingerprint);
    }

    private static LayerTemplateEntry Find(LayerTemplateDefinition template, string path) =>
        template.Entries.Single(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
}
