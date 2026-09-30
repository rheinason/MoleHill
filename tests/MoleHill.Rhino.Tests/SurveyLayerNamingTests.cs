using MoleHill.Core.Interop;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class SurveyLayerNamingTests
{
    [Theory]
    [InlineData(@"C:\jobs\Verandi Survey.csv", "Verandi Survey")]
    [InlineData(@"C:\jobs\site (rev 2).txt", "site rev 2")]
    [InlineData(@"C:\jobs\a::b;c.pnt", "a b c")]
    [InlineData(@"C:\jobs\noextension", "noextension")]
    public void RootName_FileName_IsTheCleanedNameWithoutExtension(string path, string expected) =>
        Assert.Equal(expected, SurveyLayerNaming.RootName(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\jobs\(((.csv")]
    public void RootName_NothingUsable_FallsBackToSurvey(string? path) =>
        Assert.Equal(SurveyLayerNaming.FallbackRootName, SurveyLayerNaming.RootName(path));

    [Fact]
    public void NextFreeRootName_FreeName_IsReturnedUnchanged() =>
        Assert.Equal("Site", SurveyLayerNaming.NextFreeRootName("Site", _ => false));

    [Fact]
    public void NextFreeRootName_TakenNames_CountsUpFromTwo()
    {
        var taken = new HashSet<string> { "Site", "Site 2" };

        Assert.Equal("Site 3", SurveyLayerNaming.NextFreeRootName("Site", taken.Contains));
    }

    [Theory]
    [InlineData("Site", "Breaklines", "Site::Breaklines")]
    [InlineData("Site", "Edge::Kerb", "Site::Edge::Kerb")]
    [InlineData("Site", "", "Site")]
    public void FullPath_JoinsRootAndRelativePath(string root, string relative, string expected) =>
        Assert.Equal(expected, SurveyLayerNaming.FullPath(root, relative));

    [Fact]
    public void DefaultColorArgb_InputRoles_HaveAColourAndOthersDoNot()
    {
        Assert.NotNull(SurveyLayerNaming.DefaultColorArgb(FieldCodeRole.Breakline));
        Assert.NotNull(SurveyLayerNaming.DefaultColorArgb(FieldCodeRole.Spot));
        Assert.Null(SurveyLayerNaming.DefaultColorArgb(FieldCodeRole.Ignore));
    }
}
