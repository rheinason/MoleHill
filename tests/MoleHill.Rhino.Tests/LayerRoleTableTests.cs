using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// How a layer template's role bindings resolve into paths and appearance.
/// </summary>
public class LayerRoleTableTests
{
    private static LayerTemplateDefinition Template(params LayerTemplateEntry[] entries) =>
        new() { Version = 1, Name = "Test", Entries = entries.ToList() };

    private static LayerTemplateEntry Bind(LayerRole role, string path) =>
        new() { Role = LayerRoleRegistry.For(role).Id, Path = path };

    [Fact]
    public void UnboundRole_InheritsItsPathThroughTheParentChain()
    {
        var table = LayerRoleTable.Default;

        Assert.Equal("MoleHill::Annotation", table.Path(LayerRole.Annotation));
        Assert.Equal("MoleHill::Annotation::Contours", table.Path(LayerRole.Contours));
        Assert.Equal("MoleHill::Annotation::Contours::Major", table.Path(LayerRole.ContoursMajor));
    }

    /// <summary>
    /// Rebinding a root moves everything under it. This is the property the schema 29 to 30
    /// migration relies on: a document that pointed its annotation output somewhere else keeps its
    /// whole drawing family there, from one binding.
    /// </summary>
    [Fact]
    public void RebindingARoot_MovesItsWholeFamily()
    {
        var table = LayerRoleTable.Build(Template(Bind(LayerRole.Annotation, "Drawing::Site")));

        Assert.Equal("Drawing::Site", table.Path(LayerRole.Annotation));
        Assert.Equal("Drawing::Site::Contours::Major", table.Path(LayerRole.ContoursMajor));
        Assert.Equal("Drawing::Site::Sections::CutFill::Cut", table.Path(LayerRole.SectionsCutFillCut));

        // Roots that were not rebound stay where they were.
        Assert.Equal(TerrainDefinition.DefaultTerrainLayerPath, table.Path(LayerRole.Terrain));
    }

    [Fact]
    public void ExplicitBinding_IsAnAbsolutePathAndWinsOverTheChain()
    {
        var table = LayerRoleTable.Build(Template(
            Bind(LayerRole.Annotation, "Drawing::Site"),
            Bind(LayerRole.ContoursMajor, "Survey::Heavy Contours")));

        Assert.Equal("Survey::Heavy Contours", table.Path(LayerRole.ContoursMajor));
        // Its sibling still follows the rebound parent.
        Assert.Equal("Drawing::Site::Contours::Minor", table.Path(LayerRole.ContoursMinor));
    }

    [Fact]
    public void Path_IsNeverNullOrEmpty_ForEveryRole()
    {
        foreach (var table in new[] { LayerRoleTable.Default, LayerRoleTable.Build(Template()) })
        {
            foreach (LayerRole role in Enum.GetValues<LayerRole>())
                Assert.False(string.IsNullOrWhiteSpace(table.Path(role)));
        }
    }

    [Fact]
    public void ZonePaths_MirrorTheInputLayerTheyWereReadFrom()
    {
        var table = LayerRoleTable.Default;

        Assert.Equal("MoleHill::Zones::Site::Lawn", table.Path(LayerRole.Zones, "Site::Lawn"));
        Assert.Equal("MoleHill::Zones", table.Path(LayerRole.Zones, null));
        Assert.Equal("MoleHill::Zones", table.Path(LayerRole.Zones, "   "));
        // Empty segments in a source path must not produce a doubled separator.
        Assert.Equal("MoleHill::Zones::Site", table.Path(LayerRole.Zones, "::Site::"));
    }

    /// <summary>
    /// Appearance inherits field by field, so a template can restate one value without having to
    /// repeat everything beside it.
    /// </summary>
    [Fact]
    public void Appearance_InheritsFieldByField()
    {
        var entry = Bind(LayerRole.ContoursMajor, "MoleHill::Annotation::Contours::Major");
        entry.PlotWeight = 0.9;

        var appearance = LayerRoleTable.Build(Template(entry)).Appearance(LayerRole.ContoursMajor);

        Assert.Equal(0.9, appearance.PlotWeight);
        // Untouched fields keep the role's own defaults rather than being reset.
        Assert.Equal(unchecked((int)0xFF6E4B1F), appearance.ColorArgb);
        Assert.Equal(unchecked((int)0xFF000000), appearance.PrintColorArgb);
    }

    [Fact]
    public void PreviewWidth_DerivesFromPrintWidthUnlessOverridden()
    {
        var entry = Bind(LayerRole.Contours, "MoleHill::Annotation::Contours");
        entry.PlotWeight = 0.70;
        Assert.Equal(5, LayerRoleTable.Build(Template(entry)).Appearance(LayerRole.Contours).PreviewWidthPx);

        entry.PreviewWidthPx = 2;
        Assert.Equal(2, LayerRoleTable.Build(Template(entry)).Appearance(LayerRole.Contours).PreviewWidthPx);
    }

    [Fact]
    public void PreviewLineWeight_ScalesButNeverVanishesOrRunsAway()
    {
        var appearance = LayerRoleTable.Default.Appearance(LayerRole.Sections);

        Assert.Equal(appearance.PreviewWidthPx, appearance.ScalePreviewWidth(1.0));
        Assert.True(appearance.ScalePreviewWidth(0.01) >= 1);
        Assert.True(appearance.ScalePreviewWidth(1000.0) <= 32);
        // A nonsensical multiplier must not silently erase the line.
        Assert.Equal(appearance.PreviewWidthPx, appearance.ScalePreviewWidth(double.NaN));
    }

    [Fact]
    public void UnknownRoleId_KeepsTheLayerButBindsNothing()
    {
        var table = LayerRoleTable.Build(Template(
            new LayerTemplateEntry { Role = "role-from-a-newer-build", Path = "Future::Layer" }));

        Assert.Equal(TerrainDefinition.DefaultTerrainLayerPath, table.Path(LayerRole.Terrain));
        Assert.Contains(table.AllLayers, layer =>
            string.Equals(layer.Path, "Future::Layer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllLayers_CoversRoleLayersAndThePlainOnesATemplateCreates()
    {
        var table = LayerRoleTable.Build(Template(
            new LayerTemplateEntry { Path = "MoleHill::Inputs::Spots", ColorArgb = 123 }));

        Assert.Contains(table.AllLayers, layer => layer.Path == "MoleHill::Inputs::Spots");
        Assert.Contains(table.AllLayers, layer => layer.Path == TerrainDefinition.DefaultTerrainLayerPath);
    }

    [Fact]
    public void FindByLayerPath_PrefersTheLongestMatch()
    {
        var table = LayerRoleTable.Default;

        Assert.Equal(
            LayerRole.ContoursMajor,
            table.FindByLayerPath("MoleHill::Annotation::Contours::Major")!.Role);
        Assert.Equal(LayerRole.Contours, table.FindByLayerPath("MoleHill::Annotation::Contours")!.Role);
        Assert.Null(table.FindByLayerPath("Some::Unrelated::Layer"));
        Assert.Null(table.FindByLayerPath(null));
    }

    /// <summary>
    /// The build cache keys on this, so routing or appearance changes must move it and cosmetic
    /// rebuilds must not.
    /// </summary>
    [Fact]
    public void Fingerprint_TracksRoutingAndAppearance()
    {
        ulong baseline = LayerRoleTable.Build(Template()).Fingerprint;

        Assert.Equal(baseline, LayerRoleTable.Build(Template()).Fingerprint);
        Assert.NotEqual(baseline, LayerRoleTable.Build(Template(Bind(LayerRole.Terrain, "Other"))).Fingerprint);

        var restyled = Bind(LayerRole.Terrain, TerrainDefinition.DefaultTerrainLayerPath);
        restyled.PlotWeight = 1.4;
        Assert.NotEqual(baseline, LayerRoleTable.Build(Template(restyled)).Fingerprint);
    }
}
