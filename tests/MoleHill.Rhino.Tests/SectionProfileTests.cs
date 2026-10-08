using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The section's terrain profiles: which ones it draws, the colour this card gives each, and the key that
/// says which line is which.
/// </summary>
public class SectionProfileTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Survey = Guid.NewGuid();
    private static readonly Guid Phase2 = Guid.NewGuid();

    private static T RoundTrip<T>(T section) where T : AnnotationDefinition
    {
        var terrain = new TerrainDefinition();
        terrain.Annotations.Add(section);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        return Assert.IsType<T>(Assert.Single(Assert.Single(TerrainSerializer.Deserialize(json)).Annotations));
    }

    [Fact]
    public void RoundTrip_ProfileColours_SurviveSaveAndLoad()
    {
        var section = new TerrainSectionAnnotationDefinition();
        section.ComparisonTerrainIds.Add(Phase2);
        section.ProfileColorArgbs[Phase2] = unchecked((int)0xFF1E88E5);

        TerrainSectionAnnotationDefinition restored = RoundTrip(section);

        Assert.Equal(unchecked((int)0xFF1E88E5), restored.ProfileColorArgbs[Phase2]);
    }

    [Fact]
    public void ResolveProfileColor_NoChoiceOnTheCard_UsesTheTerrainsOwnColour()
    {
        var section = new CrossSectionStationAnnotationDefinition();
        section.ProfileColorArgbs[Phase2] = 0x123456;

        Assert.Equal(0x123456, section.ResolveProfileColorArgb(Phase2, terrainColorArgb: 0x654321));
        Assert.Equal(0x654321, section.ResolveProfileColorArgb(Survey, terrainColorArgb: 0x654321));
    }

    [Fact]
    public void NormalizeAfterLoad_DropsAColourForTheOwningTerrain()
    {
        var section = new LongitudinalSectionAnnotationDefinition();
        section.ProfileColorArgbs[Owner] = 1;
        section.ProfileColorArgbs[Phase2] = 2;

        section.NormalizeAfterLoad(ModelUnitContext.FromUnitSystem(global::Rhino.UnitSystem.Meters), Owner);

        Assert.False(section.ProfileColorArgbs.ContainsKey(Owner));
        Assert.Equal(2, section.ProfileColorArgbs[Phase2]);
    }

    [Fact]
    public void ProfileKey_ComparedTerrain_IsNamedAsExistingAndListedOnce()
    {
        var section = new TerrainSectionAnnotationDefinition { CutFillReferenceTerrainId = Survey };

        var key = TerrainAnalysisAnnotationBuilder.DescribeProfileKey(
            section,
            new[]
            {
                (Owner, "Site", 0, true, false),
                (Survey, "Survey", 0, false, true),
                (Phase2, "Phase 2", 0x00FF00, false, false)
            },
            hasExistingGround: true);

        Assert.Equal(new[] { "Site (proposed)", "Survey (existing)", "Phase 2" }, key.Select(entry => entry.Text));
        Assert.Equal(LayerRole.SectionsExisting, key[1].Role);
        Assert.Null(key[1].ColorArgb);
        Assert.Equal(0x00FF00, key[2].ColorArgb);
    }

    [Fact]
    public void ProfileKey_GeometryReference_IsCalledExistingGround()
    {
        var section = new TerrainSectionAnnotationDefinition();
        section.CutFillReference.ObjectIds.Add(Guid.NewGuid());

        var key = TerrainAnalysisAnnotationBuilder.DescribeProfileKey(
            section,
            new[] { (Owner, "Site", 0, true, false) },
            hasExistingGround: true);

        Assert.Equal(new[] { "Site (proposed)", "Existing ground" }, key.Select(entry => entry.Text));
    }

    [Fact]
    public void ProfileKey_SingleProfile_NeedsNoKey()
    {
        var key = TerrainAnalysisAnnotationBuilder.DescribeProfileKey(
            new TerrainSectionAnnotationDefinition(),
            new[] { (Owner, "Site", 0, true, false) },
            hasExistingGround: false);

        Assert.Empty(key);
    }

    [Fact]
    public void ProfileKey_CutMissesThisTerrain_NobodyIsCalledProposed()
    {
        var key = TerrainAnalysisAnnotationBuilder.DescribeProfileKey(
            new TerrainSectionAnnotationDefinition(),
            new[] { (Survey, "Survey", 0x111111, false, false), (Phase2, "Phase 2", 0x222222, false, false) },
            hasExistingGround: false);

        Assert.Equal(new[] { "Survey", "Phase 2" }, key.Select(entry => entry.Text));
    }
}
