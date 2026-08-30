using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Cut and fill are the one generated-layer colour that carries meaning. A section that shades both the
/// same is unreadable, so these are seeded when the layer is created rather than waiting for someone to
/// find "Bake Layers".
/// </summary>
public class GeneratedLayerDefaultsColorTests
{
    private const string Annotation = "MoleHill::Annotation";

    [Theory]
    [InlineData(Annotation + "::Sections::CutFill::Cut")]
    [InlineData(Annotation + "::CutFill::Cut")]
    public void Cut_SeedsTheCutColour(string path)
    {
        Assert.Equal(unchecked((int)0xFFEB462D), GeneratedLayerDefaults.GetColorArgb(path));
    }

    [Theory]
    [InlineData(Annotation + "::Sections::CutFill::Fill")]
    [InlineData(Annotation + "::CutFill::Fill")]
    public void Fill_SeedsTheFillColour(string path)
    {
        Assert.Equal(unchecked((int)0xFF4C849E), GeneratedLayerDefaults.GetColorArgb(path));
    }

    [Fact]
    public void CutAndFill_AreNeverTheSameColour()
    {
        Assert.NotEqual(
            GeneratedLayerDefaults.GetColorArgb(Annotation + "::Sections::CutFill::Cut"),
            GeneratedLayerDefaults.GetColorArgb(Annotation + "::Sections::CutFill::Fill"));
    }

    [Theory]
    [InlineData(Annotation + "::Sections", 0xFF000000)]
    [InlineData(Annotation + "::Sections::Existing", 0xFF8C8C8C)]
    [InlineData(Annotation + "::Sections::Grid", 0xFFB4B4B4)]
    public void SectionDrawingLayers_SeedTheirDrawingConventionColours(string path, uint expected)
    {
        // The proposed profile is the subject and is black; existing ground and the grid are context and
        // recede. Seeded so a section reads correctly without anyone visiting the Layers panel.
        Assert.Equal(unchecked((int)expected), GeneratedLayerDefaults.GetColorArgb(path));
    }

    [Theory]
    [InlineData(Annotation + "::Contours::Major")]
    [InlineData(Annotation + "::Waterflow")]
    [InlineData("Some::User::Layer")]
    [InlineData(null)]
    public void EverythingElse_KeepsRhinosDefault(string? path)
    {
        // Only colours that mean something are seeded; a user's own layer is never restyled.
        Assert.Null(GeneratedLayerDefaults.GetColorArgb(path));
    }

    [Fact]
    public void ColourSeeding_DoesNotDisturbTheExistingWidthSeeding()
    {
        Assert.Equal(0.13, GeneratedLayerDefaults.GetPlotWeight(Annotation + "::Sections::CutFill::Cut"));
        Assert.Equal(0.50, GeneratedLayerDefaults.GetPlotWeight(Annotation + "::Sections::Cuts"));
        Assert.Equal(1, GeneratedLayerDefaults.GetPreviewWidth(Annotation + "::Sections::CutFill::Fill"));
    }
}
