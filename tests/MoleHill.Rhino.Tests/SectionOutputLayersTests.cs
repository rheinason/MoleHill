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
    public void GetPlotWeight_ReturnsExpectedLineWeights()
    {
        Assert.Equal(0.50, SectionOutputLayers.GetPlotWeight(SectionLayerKind.Profile));
        Assert.Equal(0.50, SectionOutputLayers.GetPlotWeight(SectionLayerKind.Cuts));
        Assert.Equal(0.13, SectionOutputLayers.GetPlotWeight(SectionLayerKind.Grid));
        Assert.Equal(0.18, SectionOutputLayers.GetPlotWeight(SectionLayerKind.Ticks));
    }

    [Fact]
    public void GetPlotWeight_LeavesLabelsOnLayerDefault()
    {
        Assert.Null(SectionOutputLayers.GetPlotWeight(SectionLayerKind.Labels));
    }
}
