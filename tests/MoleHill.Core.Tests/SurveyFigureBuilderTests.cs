using MoleHill.Core.Interop;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class SurveyFigureBuilderTests
{
    private static FieldCodeTable Table() => FieldCodeTable.CreateDefault();

    /// <summary>Points in file order with ascending coordinates; only the description matters here.</summary>
    private static List<SurveyPoint> Points(params string[] descriptions)
    {
        var points = new List<SurveyPoint>(descriptions.Length);
        for (int i = 0; i < descriptions.Length; i++)
            points.Add(new SurveyPoint(i + 1, i, i, 10.0 + i, descriptions[i], i + 1));
        return points;
    }

    [Fact]
    public void Build_RunOfOneCode_BecomesOneOpenFigure()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("EP", "EP", "EP"), Table());

        SurveyFigure figure = Assert.Single(result.Figures);
        Assert.Equal("EP", figure.Code);
        Assert.Equal(FieldCodeRole.Breakline, figure.Role);
        Assert.Equal(new[] { 0, 1, 2 }, figure.PointIndices);
        Assert.False(figure.IsClosed);
    }

    [Fact]
    public void Build_FigureNumberSuffix_SeparatesTwoRunsOfTheSameCode()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("EP1", "EP2", "EP1", "EP2"), Table());

        Assert.Equal(2, result.Figures.Count);
        SurveyFigure first = result.Figures.Single(f => f.FigureNumber == 1);
        SurveyFigure second = result.Figures.Single(f => f.FigureNumber == 2);
        Assert.Equal(new[] { 0, 2 }, first.PointIndices);
        Assert.Equal(new[] { 1, 3 }, second.PointIndices);
    }

    [Fact]
    public void Build_InterleavedCodes_KeepEachRunSeparate()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("EP", "TOE", "EP", "TOE", "EP"), Table());

        Assert.Equal(2, result.Figures.Count);
        SurveyFigure edge = result.Figures.Single(f => f.Code == "EP");
        SurveyFigure toe = result.Figures.Single(f => f.Code == "TOE");
        Assert.Equal(new[] { 0, 2, 4 }, edge.PointIndices);
        Assert.Equal(new[] { 1, 3 }, toe.PointIndices);
    }

    [Fact]
    public void Build_StartAndEndMarkers_BoundTheRun()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(
            Points("EP ST", "EP", "EP END", "EP ST", "EP", "EP END"),
            Table());

        Assert.Equal(2, result.Figures.Count);
        Assert.Equal(new[] { 0, 1, 2 }, result.Figures[0].PointIndices);
        Assert.Equal(new[] { 3, 4, 5 }, result.Figures[1].PointIndices);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_StartMarkerWhileRunOpen_ClosesTheOldRunAndOpensANewOne()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("EP", "EP", "EP ST", "EP"), Table());

        Assert.Equal(2, result.Figures.Count);
        Assert.Equal(new[] { 0, 1 }, result.Figures[0].PointIndices);
        Assert.Equal(new[] { 2, 3 }, result.Figures[1].PointIndices);
    }

    [Fact]
    public void Build_ContinuationSuffix_JoinsTheOpenRunOfThatCode()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("EP", "EP-", "EP-"), Table());

        SurveyFigure figure = Assert.Single(result.Figures);
        Assert.Equal(new[] { 0, 1, 2 }, figure.PointIndices);
    }

    [Fact]
    public void Build_CloseMarker_ClosesTheFigureIntoALoop()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("BLD", "BLD", "BLD", "BLD CL"), Table());

        SurveyFigure figure = Assert.Single(result.Figures);
        Assert.True(figure.IsClosed);
        Assert.Equal(4, figure.PointCount);
    }

    [Fact]
    public void Build_CodeClosedByDefault_ClosesWithoutAMarker()
    {
        // BLD is a building footprint: always an outline, so the crew should not need a close marker.
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("BLD", "BLD", "BLD"), Table());

        Assert.True(Assert.Single(result.Figures).IsClosed);
    }

    [Fact]
    public void Build_ArcToken_FlagsOnlyThatPoint()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("EP", "EP AR", "EP"), Table());

        SurveyFigure figure = Assert.Single(result.Figures);
        Assert.Equal(new[] { false, true, false }, figure.ArcFlags);
    }

    [Fact]
    public void Build_UnclosedRunAtEndOfFile_IsClosedAndReported()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("EP", "EP"), Table());

        Assert.Single(result.Figures);
        SurveyReadDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("still open at the end of the file", diagnostic.Message);
    }

    [Fact]
    public void Build_SingleFigurePoint_IsKeptAsASpotRatherThanDiscarded()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("EP ST", "EP END", "TOE"), Table());

        // TOE never got a second point, so it cannot be a line — but its level must not vanish.
        Assert.DoesNotContain(result.Figures, f => f.Code == "TOE");
        Assert.Contains(2, result.SpotPointIndices);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("kept as a spot level"));
    }

    [Fact]
    public void Build_SpotRole_NeverJoinsARun()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("SPOT", "SPOT", "SPOT"), Table());

        Assert.Empty(result.Figures);
        Assert.Equal(new[] { 0, 1, 2 }, result.SpotPointIndices);
    }

    [Fact]
    public void Build_UnknownCode_IsCountedAndKeptNotDropped()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("TREE", "TREE", "EP", "EP"), Table());

        Assert.Equal(new[] { 0, 1 }, result.UnmatchedPointIndices);
        Assert.Equal(2, result.UnmatchedCodes["TREE"]);
        Assert.Single(result.Figures);
    }

    [Fact]
    public void Build_BlankDescription_IsReportedUnderItsOwnLabel()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("", "  "), Table());

        Assert.Equal(2, result.UnmatchedCodes[SurveyImportResult.BlankCodeLabel]);
        Assert.Equal(2, result.UnmatchedPointIndices.Count);
    }

    [Fact]
    public void Build_IgnoreRule_DrawsNothingAndIsNotReportedAsUnmatched()
    {
        FieldCodeTable table = Table();
        table.Rules.Add(new FieldCodeRule { Code = "TREE", Role = FieldCodeRole.Ignore });

        SurveyImportResult result = SurveyFigureBuilder.Build(Points("TREE", "TREE", "EP", "EP"), table);

        Assert.Equal(2, result.IgnoredPointCount);
        Assert.Empty(result.UnmatchedPointIndices);
        Assert.Empty(result.UnmatchedCodes);
    }

    [Fact]
    public void Build_DecreasingPointNumbers_DoNotReorderTheFigure()
    {
        // A file merged from two days can renumber. File order is the authority.
        var points = new List<SurveyPoint>
        {
            new(500, 0, 0, 10, "EP", 1),
            new(3, 1, 1, 11, "EP", 2),
            new(412, 2, 2, 12, "EP", 3)
        };

        SurveyFigure figure = Assert.Single(SurveyFigureBuilder.Build(points, Table()).Figures);

        Assert.Equal(new[] { 0, 1, 2 }, figure.PointIndices);
    }

    [Fact]
    public void Build_RuleWithNoLayer_FallsBackToTheRoleLayer()
    {
        // The shipped rules name no layer, so one "Layers" assignment picks up every breakline code.
        SurveyFigure figure = Assert.Single(SurveyFigureBuilder.Build(Points("TC", "TC"), Table()).Figures);

        Assert.Equal("MoleHill::Inputs::Breaklines", figure.Layer);
    }

    [Fact]
    public void Build_RuleWithItsOwnLayer_OverridesTheRoleLayer()
    {
        FieldCodeTable table = Table();
        table.Find("TC")!.Layer = "Survey::Top of Kerb";

        SurveyFigure figure = Assert.Single(SurveyFigureBuilder.Build(Points("TC", "TC"), table).Figures);

        Assert.Equal("Survey::Top of Kerb", figure.Layer);
    }

    [Fact]
    public void Build_BoundaryAndBreaklineRoles_LandOnDifferentDefaultLayers()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Points("EP", "EP", "BDY", "BDY"), Table());

        Assert.Equal("MoleHill::Inputs::Breaklines", result.Figures.Single(f => f.Code == "EP").Layer);
        Assert.Equal("MoleHill::Inputs::Boundary", result.Figures.Single(f => f.Code == "BDY").Layer);
    }

    [Fact]
    public void UnmatchedLayer_IsNotOneOfTheRoleLayers()
    {
        // An unmatched point's meaning is unknown; feeding it to the terrain would be worse than losing it.
        FieldCodeTable table = Table();

        Assert.DoesNotContain(
            table.UnmatchedLayer,
            Enum.GetValues<FieldCodeRole>().Select(FieldCodeTable.DefaultLayerFor).Where(layer => layer.Length > 0));
    }

    [Fact]
    public void Build_NoPoints_ReturnsEmptyResult()
    {
        SurveyImportResult result = SurveyFigureBuilder.Build(Array.Empty<SurveyPoint>(), Table());

        Assert.Empty(result.Figures);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.IgnoredPointCount);
    }
}
