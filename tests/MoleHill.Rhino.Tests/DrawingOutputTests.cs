using System.Collections.Generic;
using System.Linq;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Covers the sheet-readiness changes: hatch pattern resolution, layer-driven print widths, and
/// major/minor contour routing.
/// </summary>
public class DrawingOutputTests
{
    // --- hatch patterns -------------------------------------------------------------------------

    [Fact]
    public void ResolvePatternName_BlankFallsBackToCallerDefault()
    {
        Assert.Equal("Hatch2", HatchPatternService.ResolvePatternName(null, "Hatch2"));
        Assert.Equal("Hatch2", HatchPatternService.ResolvePatternName("   ", "Hatch2"));
        Assert.Equal("Solid", HatchPatternService.ResolvePatternName(" Solid ", "Hatch2"));
    }

    [Fact]
    public void ResolveIndex_KnownPattern_ReturnsCapturedIndex()
    {
        var snapshot = Patterns(("Hatch1", 7, 0.125), ("Solid", 1, 0.0));

        Assert.Equal(7, snapshot.ResolveIndex("Hatch1", HatchPatternService.DefaultFillPatternName));
    }

    [Fact]
    public void ResolveIndex_UnknownPattern_DegradesToSolidRatherThanVanishing()
    {
        var snapshot = Patterns(("Solid", 3, 0.0));

        Assert.Equal(3, snapshot.ResolveIndex("NotInThisDocument", "AlsoMissing"));
    }

    [Fact]
    public void ResolveIndex_PatternPresentButUnresolved_FallsBackToSolid()
    {
        // EnsurePattern returns -1 for a name that is neither in the document nor a known built-in.
        var snapshot = Patterns(("OfficeStandard", -1, 0.0), ("Solid", 2, 0.0));

        Assert.Equal(2, snapshot.ResolveIndex("OfficeStandard", "Solid"));
    }

    [Fact]
    public void ResolveScale_ExplicitScale_IsUsedVerbatim()
    {
        var snapshot = Patterns(("Hatch1", 0, 0.125));

        Assert.Equal(3.0, snapshot.ResolveScale("Hatch1", "Hatch1", storedScale: 3.0, annotationTextHeight: 0.25));
    }

    [Fact]
    public void ResolveScale_Derived_TargetsSpacingProportionalToTextHeight()
    {
        // Rhino's Hatch1 spaces lines 0.125 model units apart, so a fixed scale of 1 draws 8 lines per
        // model unit and prints as a solid smear on a real section. The derived scale targets
        // 0.8 x text height instead: 0.2 m here, which is 2 mm on paper at 1:100.
        var snapshot = Patterns(("Hatch1", 0, 0.125));

        double scale = snapshot.ResolveScale("Hatch1", "Hatch1", storedScale: 0.0, annotationTextHeight: 0.25);

        Assert.Equal(1.6, scale, 9);
        Assert.Equal(0.2, scale * 0.125, 9);
    }

    [Fact]
    public void ResolveScale_Derived_AdaptsToEachPatternsOwnSpacing()
    {
        // Two patterns with different native spacing must end up at the same drawn spacing.
        var snapshot = Patterns(("Fine", 0, 0.125), ("Coarse", 1, 0.5));

        double fine = snapshot.ResolveScale("Fine", "Fine", 0.0, 0.25);
        double coarse = snapshot.ResolveScale("Coarse", "Coarse", 0.0, 0.25);

        Assert.Equal(fine * 0.125, coarse * 0.5, 9);
    }

    [Fact]
    public void ResolveScale_SolidOrUnknownSpacing_FallsBackToUnitScale()
    {
        var snapshot = Patterns(("Solid", 0, 0.0));

        Assert.Equal(1.0, snapshot.ResolveScale("Solid", "Solid", 0.0, 0.25));
    }

    [Fact]
    public void ResolveScale_NoAnnotationHeight_FallsBackToUnitScale()
    {
        var snapshot = Patterns(("Hatch1", 0, 0.125));

        Assert.Equal(1.0, snapshot.ResolveScale("Hatch1", "Hatch1", 0.0, annotationTextHeight: 0.0));
    }

    [Fact]
    public void SectionAnalysis_DefaultsToDerivedHatchScale()
    {
        Assert.Equal(0.0, new TerrainSectionAnalysisDefinition().HatchScale);
    }

    private static HatchPatternSnapshot Patterns(params (string Name, int Index, double Offset)[] entries)
    {
        var map = new Dictionary<string, HatchPatternEntry>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (name, index, offset) in entries)
            map[name] = new HatchPatternEntry(index, offset);
        return new HatchPatternSnapshot(map);
    }

    // --- layer-driven print widths -------------------------------------------------------------

    [Fact]
    public void GeneratedLayerDefaults_LongerSuffixWins()
    {
        // "::CutFill::Cut" must not be shadowed by a shorter suffix match.
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("Sections::CutFill::Cut"));
        Assert.Equal(0.50, GeneratedLayerDefaults.GetPlotWeight("Sections::Cuts"));
    }

    [Fact]
    public void GeneratedLayerDefaults_IsCaseInsensitive()
    {
        Assert.Equal(0.13, GeneratedLayerDefaults.GetPlotWeight("sections::grid"));
    }

    [Fact]
    public void GeneratedLayerDefaults_NullOrBlank_ReturnsNull()
    {
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight(null));
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("   "));
    }

    // --- major / minor contours ----------------------------------------------------------------

    [Fact]
    public void IsMajorContourLevel_EveryFifthLevelFromStart_IsMajor()
    {
        var analysis = new ContourAnalysisDefinition { Interval = 1.0, StartZ = 0.0, MajorEveryNth = 5 };

        Assert.True(TerrainBuildService.IsMajorContourLevel(0.0, analysis, 1e-6));
        Assert.False(TerrainBuildService.IsMajorContourLevel(1.0, analysis, 1e-6));
        Assert.False(TerrainBuildService.IsMajorContourLevel(4.0, analysis, 1e-6));
        Assert.True(TerrainBuildService.IsMajorContourLevel(5.0, analysis, 1e-6));
        Assert.True(TerrainBuildService.IsMajorContourLevel(10.0, analysis, 1e-6));
    }

    [Fact]
    public void IsMajorContourLevel_NegativeElevations_StayOnTheSameGrid()
    {
        var analysis = new ContourAnalysisDefinition { Interval = 1.0, StartZ = 0.0, MajorEveryNth = 5 };

        Assert.True(TerrainBuildService.IsMajorContourLevel(-5.0, analysis, 1e-6));
        Assert.False(TerrainBuildService.IsMajorContourLevel(-3.0, analysis, 1e-6));
        Assert.True(TerrainBuildService.IsMajorContourLevel(-10.0, analysis, 1e-6));
    }

    [Fact]
    public void IsMajorContourLevel_RespectsStartZOffset()
    {
        var analysis = new ContourAnalysisDefinition { Interval = 0.5, StartZ = 0.25, MajorEveryNth = 4 };

        Assert.True(TerrainBuildService.IsMajorContourLevel(0.25, analysis, 1e-6));
        Assert.True(TerrainBuildService.IsMajorContourLevel(2.25, analysis, 1e-6));
        Assert.False(TerrainBuildService.IsMajorContourLevel(1.25, analysis, 1e-6));
    }

    [Fact]
    public void IsMajorContourLevel_EveryNthOfOne_MakesEveryLevelMajor()
    {
        var analysis = new ContourAnalysisDefinition { Interval = 1.0, StartZ = 0.0, MajorEveryNth = 1 };

        Assert.True(TerrainBuildService.IsMajorContourLevel(3.0, analysis, 1e-6));
        Assert.True(TerrainBuildService.IsMajorContourLevel(7.0, analysis, 1e-6));
    }

    [Fact]
    public void IsMajorContourLevel_LevelOffTheIntervalGrid_IsNotMajor()
    {
        var analysis = new ContourAnalysisDefinition { Interval = 1.0, StartZ = 0.0, MajorEveryNth = 5 };

        Assert.False(TerrainBuildService.IsMajorContourLevel(5.4, analysis, 1e-6));
    }

    [Fact]
    public void ResolveContourLevelLayerPath_SplitEnabled_UsesMajorMinorSublayers()
    {
        var analysis = new ContourAnalysisDefinition { SeparateMajorMinorLayers = true };

        Assert.Equal(
            "MoleHill::Annotation::Contours::Major",
            TerrainBuildService.ResolveContourLevelLayerPath("MoleHill::Annotation", analysis, isMajor: true));
        Assert.Equal(
            "MoleHill::Annotation::Contours::Minor",
            TerrainBuildService.ResolveContourLevelLayerPath("MoleHill::Annotation", analysis, isMajor: false));
    }

    [Fact]
    public void ResolveContourLevelLayerPath_SplitDisabled_KeepsFlatRouting()
    {
        var analysis = new ContourAnalysisDefinition { SeparateMajorMinorLayers = false };

        Assert.Equal(
            "MoleHill::Annotation",
            TerrainBuildService.ResolveContourLevelLayerPath("MoleHill::Annotation", analysis, isMajor: true));
    }

    [Fact]
    public void ResolveContourLevelLayerPath_NoOutputLayer_StaysNull()
    {
        var analysis = new ContourAnalysisDefinition { SeparateMajorMinorLayers = true };

        Assert.Null(TerrainBuildService.ResolveContourLevelLayerPath(null, analysis, isMajor: true));
    }

    [Fact]
    public void ContourSublayerPaths_MatchTheSeededPrintWidths()
    {
        // The routed paths and the print-width defaults must agree, or major/minor lines print identically.
        var analysis = new ContourAnalysisDefinition { SeparateMajorMinorLayers = true };
        string major = TerrainBuildService.ResolveContourLevelLayerPath("MoleHill::Annotation", analysis, true)!;
        string minor = TerrainBuildService.ResolveContourLevelLayerPath("MoleHill::Annotation", analysis, false)!;

        Assert.Equal(0.35, GeneratedLayerDefaults.GetPlotWeight(major));
        Assert.Equal(0.13, GeneratedLayerDefaults.GetPlotWeight(minor));
    }

    [Fact]
    public void Deserialize_LegacyContours_KeepFlatLayerRouting()
    {
        const string legacyJson = """
        {
          "schemaVersion": 26,
          "terrains": [
            {
              "schemaVersion": 26,
              "name": "Legacy",
              "analyses": [ { "$type": "contour", "interval": 1.0 } ]
            }
          ]
        }
        """;

        TerrainDefinition terrain = TerrainSerializer.Deserialize(legacyJson).Single();
        var contour = terrain.Analyses.OfType<ContourAnalysisDefinition>().Single();

        Assert.False(contour.SeparateMajorMinorLayers);
    }

    [Fact]
    public void NewContourAnalysis_SplitsMajorMinorByDefault()
    {
        var contour = new ContourAnalysisDefinition();

        Assert.True(contour.SeparateMajorMinorLayers);
        Assert.Equal(5, contour.MajorEveryNth);
    }
}
