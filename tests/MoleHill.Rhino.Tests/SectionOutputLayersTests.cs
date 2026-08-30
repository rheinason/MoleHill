using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class SectionOutputLayersTests
{
    [Fact]
    public void ResolveLayerPath_RootAlreadyNamedSections_IsNotDoubled()
    {
        // A root the user already pointed at a Sections branch is taken at their word.
        Assert.Equal("Sections", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Profile));
        Assert.Equal("Sections::Cuts", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Cuts));
        Assert.Equal("Sections::Grid", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Grid));
        Assert.Equal("Sections::Ticks", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Ticks));
        Assert.Equal("Sections::Labels", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Labels));
        Assert.Equal("Sections::CutFill::Cut", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.CutFillCut));
        Assert.Equal("Sections::CutFill::Fill", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.CutFillFill));
    }

    [Fact]
    public void ResolveLayerPath_AddsSectionsBranchSoLayerTemplateStylingApplies()
    {
        // The office layer template styles MoleHill::Annotation::Sections::*, so output has to land there:
        // siblings of those layers are created unstyled, which is what made section hatches invisible.
        Assert.Equal(
            "MoleHill::Annotation::Sections::CutFill::Cut",
            SectionOutputLayers.ResolveLayerPath(null, "MoleHill::Annotation", SectionLayerKind.CutFillCut));
        Assert.Equal(
            "MoleHill::Annotation::Sections",
            SectionOutputLayers.ResolveLayerPath(null, "MoleHill::Annotation", SectionLayerKind.Profile));
    }

    [Fact]
    public void ResolveLayerPath_FallsBackToAnnotationLayer()
    {
        string? path = SectionOutputLayers.ResolveLayerPath(null, "Annotations", SectionLayerKind.Grid);

        Assert.Equal("Annotations::Sections::Grid", path);
    }

    [Fact]
    public void GetPlotWeight_SeedsGeneratedSublayersWithDraftingWidths()
    {
        // Print widths now live on the layer, seeded once at creation, so the user's Layers-panel edits
        // (including per-detail overrides) survive rebuilds.
        Assert.Equal(0.13, GeneratedLayerDefaults.GetPlotWeight("Sections::Grid"));
        Assert.Equal(0.18, GeneratedLayerDefaults.GetPlotWeight("Sections::Ticks"));
        Assert.Equal(0.50, GeneratedLayerDefaults.GetPlotWeight("Sections::Cuts"));
        Assert.Equal(0.35, GeneratedLayerDefaults.GetPlotWeight("MoleHill::Annotation::Contours::Major"));
        Assert.Equal(0.13, GeneratedLayerDefaults.GetPlotWeight("MoleHill::Annotation::Contours::Minor"));
    }

    [Fact]
    public void GetPlotWeight_LeavesLabelsOnLayerDefault()
    {
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("Sections::Labels"));
    }

    [Fact]
    public void GetPlotWeight_GivesCutFillFillsAHairlineBoundary()
    {
        // A fill is a region: it prints hairline so its pattern reads without the boundary competing with
        // the profiles crossing it. Leaving it at Rhino's default printed it at the same weight as a
        // profile line.
        Assert.Equal(0.13, GeneratedLayerDefaults.GetPlotWeight("Sections::CutFill::Cut"));
        Assert.Equal(0.13, GeneratedLayerDefaults.GetPlotWeight("Sections::CutFill::Fill"));
    }

    [Fact]
    public void GetPreviewWidth_MirrorsThePrintHierarchy()
    {
        // The viewport shows the same weight ordering the drawing does.
        Assert.True(
            GeneratedLayerDefaults.GetPreviewWidth("Sections::Cuts") >
            GeneratedLayerDefaults.GetPreviewWidth("Sections::Grid"));
        Assert.True(
            GeneratedLayerDefaults.GetPreviewWidth("MoleHill::Annotation::Contours::Major") >
            GeneratedLayerDefaults.GetPreviewWidth("MoleHill::Annotation::Contours::Minor"));
        Assert.Equal(
            GeneratedLayerDefaults.DefaultPreviewWidth,
            GeneratedLayerDefaults.GetPreviewWidth("Some::Office::Standard"));
    }

    [Fact]
    public void ScalePreviewWidth_NeverVanishesAndNeverRunsAway()
    {
        Assert.Equal(6, GeneratedLayerDefaults.ScalePreviewWidth(3, 2.0));
        Assert.Equal(1, GeneratedLayerDefaults.ScalePreviewWidth(1, 0.1));
        Assert.Equal(32, GeneratedLayerDefaults.ScalePreviewWidth(4, 100.0));
        Assert.Equal(3, GeneratedLayerDefaults.ScalePreviewWidth(3, 0.0));
    }

    [Fact]
    public void GetPlotWeight_UserChosenRootLayer_IsNeverRestyled()
    {
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("Sections"));
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("Some::Office::Standard"));
    }
}
