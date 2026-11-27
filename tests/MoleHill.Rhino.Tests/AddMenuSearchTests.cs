using MoleHill.Rhino.Registry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class AddMenuSearchTests
{
    private static AddMenuEntry Entry(string label, string? detail = null, string? keywords = null) =>
        new(label, detail, keywords, null, () => { });

    private static readonly AddMenuEntry[] Menu =
    {
        Entry("Grade Pad", "Flat pad with batters"),
        Entry("Retaining Wall", "Wall breaklines, optional batter", "kerb curb"),
        Entry("Smooth", "Relax the surface"),
        Entry("Grade Path", "Road or path corridor"),
        Entry("Remesh", "Rebuild triangles"),
    };

    private static string[] Labels(string query) =>
        AddMenuSearch.Filter(Menu, query).Select(entry => entry.Label).ToArray();

    [Fact]
    public void Filter_EmptyQuery_ReturnsMenuInOrder()
    {
        Assert.Equal(Menu.Select(entry => entry.Label), Labels("  "));
    }

    [Fact]
    public void Filter_LabelPrefix_RanksFirstAndKeepsMenuOrder()
    {
        // Label prefixes first, in menu order; Smooth follows on its description ("Relax ...").
        Assert.Equal(new[] { "Retaining Wall", "Remesh", "Smooth" }, Labels("re"));
        Assert.Equal(new[] { "Grade Pad", "Grade Path" }, Labels("grade"));
    }

    [Fact]
    public void Filter_EveryWordMustMatch()
    {
        Assert.Equal(new[] { "Grade Path" }, Labels("grade pat"));
    }

    [Fact]
    public void Filter_Initials_MatchLabel()
    {
        Assert.Equal(new[] { "Retaining Wall" }, Labels("rw"));
    }

    [Fact]
    public void Filter_Keywords_FindEntry()
    {
        Assert.Equal(new[] { "Retaining Wall" }, Labels("Kerb"));
    }

    [Fact]
    public void Filter_DetailMatch_RanksBelowLabelMatch()
    {
        // "batter" appears only in details; "pad" in a label.
        Assert.Equal(new[] { "Grade Pad", "Retaining Wall" }, Labels("batter"));
        Assert.Equal("Grade Pad", Labels("pad")[0]);
    }

    [Fact]
    public void Filter_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(Labels("zzz"));
    }
}
