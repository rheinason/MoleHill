using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// An analysis that cannot produce anything must say which input it is waiting on. These types otherwise
/// run, succeed, and emit nothing, which reads as a broken map rather than an unconfigured one.
/// </summary>
public class AnalysisBlockerTests
{
    private static TerrainDefinition TerrainWith(AnalysisDefinition analysis, params ModifierDefinition[] modifiers)
    {
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(analysis);
        foreach (var modifier in modifiers)
            terrain.Modifiers.Add(modifier);
        return terrain;
    }

    private static string? Blocker(AnalysisDefinition analysis, params ModifierDefinition[] modifiers)
    {
        var terrain = TerrainWith(analysis, modifiers);
        return AnalysisTypeRegistry.ForType(analysis.GetType())!.DescribeBlocker(terrain, analysis);
    }

    private static string? Basis(AnalysisDefinition analysis, params ModifierDefinition[] modifiers)
    {
        var terrain = TerrainWith(analysis, modifiers);
        return AnalysisTypeRegistry.ForType(analysis.GetType())!.DescribeBasis(terrain, analysis);
    }

    private static GradePadModifierDefinition Grading() => new() { IsEnabled = true };

    private static void AddSource(SourceReferenceSet set) => set.ObjectIds.Add(System.Guid.NewGuid());

    [Fact]
    public void CutFill_WithoutReference_IsNotBlocked_ItComparesAgainstTheInitialTriangulation()
    {
        // The whole point: asking "how much did my grading move" must not require a second terrain.
        Assert.Null(Blocker(new CutFillAnalysisDefinition(), Grading()));
    }

    [Fact]
    public void CutFill_WithoutReference_SaysWhatItIsComparing()
    {
        string? basis = Basis(new CutFillAnalysisDefinition(), Grading());

        Assert.Equal(AnalysisPrerequisites.BaseTriangulationBasis, basis);
        // It must not ask for a mesh it does not need.
        Assert.DoesNotContain("Needs", basis);
    }

    [Fact]
    public void CutFill_WithReference_SaysItIsUsingTheReference()
    {
        var analysis = new CutFillAnalysisDefinition();
        AddSource(analysis.Reference);

        Assert.Null(Blocker(analysis));
        Assert.Equal(AnalysisPrerequisites.ReferenceBasis, Basis(analysis));
    }

    [Fact]
    public void CutFill_WithNothingGradedAndNoReference_SaysThereIsNothingToCompare()
    {
        // Zero everywhere is the correct answer here; the card has to say why so it does not read broken.
        string? blocker = Blocker(new CutFillAnalysisDefinition());

        Assert.Equal(AnalysisPrerequisites.NothingToCompareMessage, blocker);
    }

    [Fact]
    public void CutFill_DisabledGradingDoesNotCount()
    {
        var off = new GradePadModifierDefinition { IsEnabled = false };

        Assert.Equal(AnalysisPrerequisites.NothingToCompareMessage, Blocker(new CutFillAnalysisDefinition(), off));
    }

    [Fact]
    public void Waterflow_WithoutPoints_NamesTheMissingInput()
    {
        string? blocker = Blocker(new WaterflowAnalysisDefinition());

        Assert.NotNull(blocker);
        Assert.Contains("Points", blocker);
    }

    [Fact]
    public void Waterflow_WithPoints_IsNotBlocked()
    {
        var analysis = new WaterflowAnalysisDefinition();
        AddSource(analysis.Sources);

        Assert.Null(Blocker(analysis));
    }

    [Fact]
    public void Section_WithoutSources_NamesTheMissingInput()
    {
        string? blocker = Blocker(new TerrainSectionAnalysisDefinition());

        Assert.NotNull(blocker);
        Assert.Contains("Sources", blocker);
    }

    [Fact]
    public void Section_WithCutFillOnAndNoReference_ShadesAgainstTheInitialTriangulation()
    {
        var analysis = new TerrainSectionAnalysisDefinition { ShowCutFillRegions = true };
        AddSource(analysis.Sources);

        Assert.Null(Blocker(analysis, Grading()));
        Assert.Equal(AnalysisPrerequisites.BaseTriangulationBasis, Basis(analysis, Grading()));
    }

    [Fact]
    public void Section_WithCutFillOff_SaysNothingAboutComparison()
    {
        var analysis = new TerrainSectionAnalysisDefinition { ShowCutFillRegions = false };
        AddSource(analysis.Sources);

        Assert.Null(Basis(analysis, Grading()));
    }

    [Fact]
    public void Section_WithCutFillOff_IsNotBlockedByAMissingReference()
    {
        var analysis = new TerrainSectionAnalysisDefinition { ShowCutFillRegions = false };
        AddSource(analysis.Sources);

        Assert.Null(Blocker(analysis));
    }

    [Fact]
    public void AnalysesWithNoPrerequisites_ReportNoBlocker()
    {
        Assert.Null(Blocker(new SlopeAnalysisDefinition()));
        Assert.Null(Blocker(new ElevationAnalysisDefinition()));
        Assert.Null(Blocker(new ContourAnalysisDefinition()));
    }
}
