using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class SectionOutputLayersTests
{
    [Fact]
    public void ResolveLayerPath_UsesExplicitRootWithExpectedChildLayers()
    {
        Assert.Equal("Sections", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Profile));
        Assert.Equal("Sections::Cuts", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Cuts));
        Assert.Equal("Sections::Grid", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Grid));
        Assert.Equal("Sections::Ticks", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Ticks));
        Assert.Equal("Sections::Labels", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.Labels));
        Assert.Equal("Sections::CutFill::Cut", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.CutFillCut));
        Assert.Equal("Sections::CutFill::Fill", SectionOutputLayers.ResolveLayerPath("Sections", "Fallback", SectionLayerKind.CutFillFill));
    }

    [Fact]
    public void ResolveLayerPath_FallsBackToAnnotationLayer()
    {
        string? path = SectionOutputLayers.ResolveLayerPath(null, "Annotations", SectionLayerKind.Grid);

        Assert.Equal("Annotations::Grid", path);
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
    public void GetPlotWeight_LeavesLabelsAndFillsOnLayerDefault()
    {
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("Sections::Labels"));
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("Sections::CutFill::Cut"));
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("Sections::CutFill::Fill"));
    }

    [Fact]
    public void GetPlotWeight_UserChosenRootLayer_IsNeverRestyled()
    {
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("Sections"));
        Assert.Null(GeneratedLayerDefaults.GetPlotWeight("Some::Office::Standard"));
    }
}
