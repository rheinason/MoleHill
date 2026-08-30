using System.Text.Json;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// What a fresh install gets, and what happens when it is taken away.
///
/// Routing and appearance both come from the template now, so "no usable template" must still be a
/// working configuration rather than output with nowhere to go.
/// </summary>
public class ShippedDefaultsTests
{
    [Fact]
    public void TheShippedTemplate_CoversEveryRoleAndTheLayersAUserDrawsOn()
    {
        var shipped = new LayerTemplateStore().GetDefaultTemplates().Single();

        Assert.Equal("MoleHill Terrain", shipped.Name);

        // Every role either has an entry or shares its parent's layer.
        var table = LayerRoleTable.Build(shipped);
        foreach (LayerRole role in Enum.GetValues<LayerRole>())
            Assert.False(string.IsNullOrWhiteSpace(table.Path(role)));

        // Plus the layers nothing routes to, which exist for the user's own geometry.
        foreach (string path in new[]
                 {
                     "MoleHill::Inputs::Spots", "MoleHill::Inputs::Contours",
                     "MoleHill::Inputs::Breaklines", "MoleHill::Inputs::Boundary",
                     "MoleHill::Features::Walls", "MoleHill::Features::Pads", "MoleHill::Features::Paths"
                 })
        {
            Assert.Contains(shipped.Entries, entry =>
                string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase) && entry.Roles.Count == 0);
        }
    }

    /// <summary>
    /// The shipped template and the built-in defaults must agree exactly, or installing MoleHill and
    /// then applying its own template would restyle the document.
    /// </summary>
    [Fact]
    public void TheShippedTemplate_MatchesTheBuiltInDefaults()
    {
        var shipped = new LayerTemplateStore().GetDefaultTemplates().Single();
        Assert.Equal(LayerRoleTable.Default.Fingerprint, LayerRoleTable.Build(shipped).Fingerprint);
    }

    /// <summary>
    /// No settings file, an unreadable one, an empty one, a template with nothing bound: all of them
    /// have to leave every role with a real layer. This is the floor the whole feature stands on.
    /// </summary>
    [Theory]
    [InlineData("no template at all")]
    [InlineData("empty template")]
    [InlineData("template binding nothing")]
    [InlineData("template with only an unknown role")]
    public void WithoutAUsableTemplate_EveryRoleStillResolves(string scenario)
    {
        LayerRoleTable table = scenario switch
        {
            "no template at all" => LayerRoleTable.Build(null),
            "empty template" => LayerRoleTable.Build(new LayerTemplateDefinition()),
            "template binding nothing" => LayerRoleTable.Build(new LayerTemplateDefinition
            {
                Version = 1,
                Entries = new List<LayerTemplateEntry> { new() { Path = "Just::A::Layer" } }
            }),
            _ => LayerRoleTable.Build(new LayerTemplateDefinition
            {
                Version = 1,
                Entries = new List<LayerTemplateEntry>
                {
                    new() { Path = "Future", Roles = { "a-role-from-a-newer-build" } }
                }
            })
        };

        foreach (LayerRole role in Enum.GetValues<LayerRole>())
            Assert.False(string.IsNullOrWhiteSpace(table.Path(role)), $"{scenario}: {role} has no layer.");

        Assert.Equal(TerrainDefinition.DefaultTerrainLayerPath, table.Path(LayerRole.Terrain));
    }

    /// <summary>
    /// An unreadable settings file must not stop a terrain building. The store rewrites it with the
    /// defaults rather than leaving the user with nothing.
    /// </summary>
    [Fact]
    public void AnUnreadableSettingsFile_FallsBackToTheDefaults()
    {
        var recovered = JsonSerializer.Deserialize<List<LayerTemplateDefinition>>("[]");
        Assert.NotNull(recovered);
        Assert.Empty(recovered);

        // Which is the case LoadTemplates treats as "nothing usable" and replaces.
        Assert.Single(new LayerTemplateStore().GetDefaultTemplates());
    }

    /// <summary>
    /// The defaults are what a drawing looks like out of the box, so the drafting hierarchy they
    /// encode is worth pinning: heavier print reads heavier on screen, and the section profile is
    /// the heaviest thing on its own drawing.
    /// </summary>
    [Fact]
    public void TheDefaults_EncodeAReadableDraftingHierarchy()
    {
        var table = LayerRoleTable.Default;

        double profile = table.Appearance(LayerRole.Sections).PlotWeight!.Value;
        double existing = table.Appearance(LayerRole.SectionsExisting).PlotWeight!.Value;
        double grid = table.Appearance(LayerRole.SectionsGrid).PlotWeight!.Value;

        Assert.True(profile > existing, "The proposed profile is the subject of a section.");
        Assert.True(existing > grid, "Grid lines are the faintest thing on the drawing.");
        Assert.True(
            table.Appearance(LayerRole.ContoursMajor).PlotWeight >
            table.Appearance(LayerRole.ContoursMinor).PlotWeight);

        // Filled regions print hairline so their boundary does not compete with what crosses them.
        Assert.Equal(0.13, table.Appearance(LayerRole.SectionsCutFillCut).PlotWeight);
    }

    /// <summary>
    /// Everything MoleHill generates hangs off one root, and drawing output is grouped by what it is,
    /// so the Layers pane can hide, lock or restyle a whole drawing family at once. A section drawing
    /// in particular is one branch: turning off Sections turns off its profile, grid, ticks, labels
    /// and shading together.
    /// </summary>
    [Fact]
    public void OutputIsGroupedForTheLayersPane()
    {
        var table = LayerRoleTable.Default;

        foreach (LayerRole role in Enum.GetValues<LayerRole>())
            Assert.StartsWith("MoleHill::", table.Path(role), StringComparison.OrdinalIgnoreCase);

        string sections = table.Path(LayerRole.Sections);
        foreach (var role in new[]
                 {
                     LayerRole.SectionsExisting, LayerRole.SectionsCuts, LayerRole.SectionsGrid,
                     LayerRole.SectionsTicks, LayerRole.SectionsLabels,
                     LayerRole.SectionsCutFillCut, LayerRole.SectionsCutFillFill
                 })
        {
            Assert.StartsWith(sections + "::", table.Path(role), StringComparison.OrdinalIgnoreCase);
        }

        // Every drawing family sits under Annotation, so plan output is one branch too.
        string annotation = table.Path(LayerRole.Annotation);
        foreach (var role in new[]
                 {
                     LayerRole.Contours, LayerRole.ContoursMajor, LayerRole.ContoursMinor,
                     LayerRole.Waterflow, LayerRole.Labels, LayerRole.Markers, LayerRole.Sections
                 })
        {
            Assert.StartsWith(annotation + "::", table.Path(role), StringComparison.OrdinalIgnoreCase);
        }

        // Model output is deliberately *not* under Annotation: you turn a drawing off without
        // turning the terrain off.
        foreach (var role in new[] { LayerRole.Terrain, LayerRole.Auxiliary, LayerRole.Zones, LayerRole.Scatter })
            Assert.DoesNotContain(annotation, table.Path(role), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every layer the template creates is one something actually lands on, or a parent of one.
    /// A permanently empty layer is clutter in the Layers pane, and six of them shipped before this
    /// test existed — mirrored from a suffix table where they were patterns, not destinations.
    /// </summary>
    [Fact]
    public void NoRoleLayerIsPermanentlyEmpty()
    {
        var table = LayerRoleTable.Default;
        var rolePaths = Enum.GetValues<LayerRole>().Select(table.Path).ToList();

        foreach (var (path, _) in table.AllLayers)
        {
            bool isRoleLayer = rolePaths.Contains(path, StringComparer.OrdinalIgnoreCase);
            bool isParentOfOne = rolePaths.Any(p =>
                p.StartsWith(path + "::", StringComparison.OrdinalIgnoreCase));
            bool isUserLayer = path.Contains("::Inputs", StringComparison.OrdinalIgnoreCase)
                || path.Contains("::Features", StringComparison.OrdinalIgnoreCase);

            Assert.True(
                isRoleLayer || isParentOfOne || isUserLayer,
                $"'{path}' receives no output and is not a parent of a layer that does.");
        }
    }

    /// <summary>Cut and fill default to a solid fill, which is the only pattern that previews exactly.</summary>
    [Fact]
    public void CutAndFill_DefaultToSolid()
    {
        var table = LayerRoleTable.Default;

        Assert.Equal("Solid", table.Appearance(LayerRole.SectionsCutFillCut).HatchPatternName);
        Assert.Equal("Solid", table.Appearance(LayerRole.SectionsCutFillFill).HatchPatternName);
    }
}
