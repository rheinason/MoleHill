using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using Xunit;

namespace MoleHill.Rhino.Tests;

[Collection("ComparisonModes")]
public class ComparisonModesTests : IDisposable
{
    public ComparisonModesTests() => ComparisonModes.ResetForTests();

    public void Dispose() => ComparisonModes.ResetForTests();

    // ---- Existing cards read as the comparison they already build ----

    [Fact]
    public void Analysis_GeometryAndTerrainBothSet_IsGeometry_WhichAlwaysTookPrecedence()
    {
        var earthwork = new EarthworkAnalysisDefinition { ReferenceTerrainId = Guid.NewGuid() };
        earthwork.Reference.ReplaceObjects(new[] { Guid.NewGuid() });

        Assert.Equal(ComparisonMode.Geometry, ComparisonModes.Of(earthwork));
    }

    [Fact]
    public void Analysis_OnlyTerrain_IsTerrain_AndNothing_IsOriginal()
    {
        Assert.Equal(ComparisonMode.Terrain, ComparisonModes.Of(new CutFillAnalysisDefinition { ReferenceTerrainId = Guid.NewGuid() }));
        Assert.Equal(ComparisonMode.Original, ComparisonModes.Of(new CutFillAnalysisDefinition()));
    }

    [Fact]
    public void Section_NoReferenceAndNoShading_IsOff_ButWithShadingIsOriginal()
    {
        Assert.Equal(ComparisonMode.Off, ComparisonModes.Of(new TerrainSectionAnnotationDefinition { ShowCutFillRegions = false }));
        Assert.Equal(ComparisonMode.Original, ComparisonModes.Of(new TerrainSectionAnnotationDefinition { ShowCutFillRegions = true }));
    }

    // ---- Switching ----

    [Fact]
    public void Switching_ClearsTheOtherReference_AndSwitchingBackRestoresIt()
    {
        Guid terrainId = Guid.NewGuid(), meshId = Guid.NewGuid();
        var earthwork = new EarthworkAnalysisDefinition { ReferenceTerrainId = terrainId };
        earthwork.Reference.ReplaceObjects(new[] { meshId });
        earthwork.Reference.ReplaceLayers(new[] { "Survey" });

        ComparisonModes.Set(earthwork, ComparisonMode.Original);
        Assert.Null(earthwork.ReferenceTerrainId);
        Assert.Empty(earthwork.Reference.ObjectIds);
        Assert.Equal(ComparisonMode.Original, ComparisonModes.Of(earthwork));

        ComparisonModes.Set(earthwork, ComparisonMode.Terrain);
        Assert.Equal(terrainId, earthwork.ReferenceTerrainId);
        Assert.Empty(earthwork.Reference.ObjectIds);

        ComparisonModes.Set(earthwork, ComparisonMode.Geometry);
        Assert.Null(earthwork.ReferenceTerrainId);
        Assert.Equal(new[] { meshId }, earthwork.Reference.ObjectIds);
        Assert.Equal(new[] { "Survey" }, earthwork.Reference.LayerPaths);
    }

    [Fact]
    public void ChosenModeWithoutItsReferenceYet_StaysChosen_UntilAnotherIsPicked()
    {
        var cutFill = new CutFillAnalysisDefinition();

        ComparisonModes.Set(cutFill, ComparisonMode.Terrain);
        Assert.Equal(ComparisonMode.Terrain, ComparisonModes.Of(cutFill));

        ComparisonModes.Set(cutFill, ComparisonMode.Original);
        Assert.Equal(ComparisonMode.Original, ComparisonModes.Of(cutFill));
    }

    [Fact]
    public void Section_TurningComparisonOn_StartsShading_ButBetweenModesTheUsersChoiceStands()
    {
        var section = new TerrainSectionAnnotationDefinition { ShowCutFillRegions = false };
        void SetTerrain(Guid? id) => section.CutFillReferenceTerrainId = id;

        ComparisonModes.Set(section, ComparisonMode.Terrain, SetTerrain);
        Assert.True(section.ShowCutFillRegions);

        section.ShowCutFillRegions = false;
        ComparisonModes.Set(section, ComparisonMode.Geometry, SetTerrain);
        Assert.False(section.ShowCutFillRegions);

        ComparisonModes.Set(section, ComparisonMode.Off, SetTerrain);
        Assert.False(section.ShowCutFillRegions);
        Assert.Equal(ComparisonMode.Off, ComparisonModes.Of(section));
    }

    [Fact]
    public void ChosenModeWithoutItsReference_AsksForIt_InsteadOfReadingAsNothingMoved()
    {
        var cutFill = new CutFillAnalysisDefinition();
        ComparisonModes.Set(cutFill, ComparisonMode.Geometry);

        string? blocker = AnalysisTypeRegistry.ForType(typeof(CutFillAnalysisDefinition))!
            .DescribeBlocker(new TerrainDefinition(), cutFill);

        Assert.NotNull(blocker);
        Assert.StartsWith("Pick the survey mesh", blocker);
    }

    [Fact]
    public void Section_BackToOriginal_AfterUntickingShading_IsOriginalNotOff()
    {
        var section = new TerrainSectionAnnotationDefinition();
        void SetTerrain(Guid? id) => section.CutFillReferenceTerrainId = id;
        ComparisonModes.Set(section, ComparisonMode.Terrain, SetTerrain);
        SetTerrain(Guid.NewGuid());
        section.ShowCutFillRegions = false;

        ComparisonModes.Set(section, ComparisonMode.Original, SetTerrain);

        Assert.Equal(ComparisonMode.Original, ComparisonModes.Of(section));
        Assert.True(section.ShowCutFillRegions);
    }
}
