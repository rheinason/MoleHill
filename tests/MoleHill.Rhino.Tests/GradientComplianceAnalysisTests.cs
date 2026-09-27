using MoleHill.Core.Analysis;
using MoleHill.Core.Reporting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The document, registry and standard-handling contract for the gradient compliance analysis. The
/// geometry is tested in Core (<c>GradientComplianceAnalyzerTests</c>); what is pinned here is that a
/// project's standard is stored as data, stays put, and says where it came from.
/// </summary>
public class GradientComplianceAnalysisTests
{
    private const string Kind = "gradient-compliance";

    private static T RoundTrip<T>(T analysis) where T : AnalysisDefinition
    {
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(analysis);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        return Assert.IsType<T>(Assert.Single(Assert.Single(TerrainSerializer.Deserialize(json)).Analyses));
    }

    private static ParameterDescriptor<AnalysisDefinition> Row(string key) =>
        Assert.Single(AnalysisTypeRegistry.ForKind(Kind)!.Parameters, p => p.Key == key);

    private static double DegreesToRatio(double degrees) => Math.Tan(degrees * Math.PI / 180.0);

    [Fact]
    public void Registry_KnowsTheGradientComplianceKind()
    {
        AnalysisTypeDescriptor descriptor = Assert.IsType<GradientComplianceAnalysisDescriptor>(
            AnalysisTypeRegistry.ForKind(Kind));

        Assert.Equal(typeof(GradientComplianceAnalysisDefinition), descriptor.DefinitionType);
        Assert.IsType<GradientComplianceAnalysisDefinition>(AnalysisTypeRegistry.Create(Kind));
        Assert.True(TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(new GradientComplianceAnalysisDefinition()));
    }

    [Fact]
    public void RoundTrip_PreservesTheWholeRuleSetAndItsProvenance()
    {
        var analysis = new GradientComplianceAnalysisDefinition
        {
            MeasurementLength = 2.25,
            Rules = new GradientRuleSet
            {
                PresetKey = "adm-vol2-2015",
                IsModified = true,
                LevelAreaMode = GradientRuleMode.Report,
                LevelAreaMaxSlopeDegrees = 1.5,
            },
        };

        GradientComplianceAnalysisDefinition restored = RoundTrip(analysis);

        Assert.Equal(2.25, restored.MeasurementLength);
        Assert.Equal("adm-vol2-2015", restored.Rules.PresetKey);
        Assert.True(restored.Rules.IsModified);
        Assert.Equal(GradientRuleMode.Report, restored.Rules.LevelAreaMode);
        Assert.Equal(1.5, restored.Rules.LevelAreaMaxSlopeDegrees);
    }

    /// <summary>
    /// The limits are checked against the published text, so pin them: a typo in a preset is a wrong
    /// verdict on every project that uses it, and nothing else would notice.
    /// </summary>
    [Theory]
    [InlineData("ada-2010", 48.0)]
    [InlineData("adm-vol2-2015", 60.0)]
    public void Presets_CarryTheirPublishedLevelAreaLimit(string key, double run)
    {
        GradientRuleSet rules = GradientRulePresets.Find(key)!.Create();

        Assert.Equal(1.0 / run, DegreesToRatio(rules.LevelAreaMaxSlopeDegrees), 12);
        Assert.Equal(key, rules.PresetKey);
        Assert.False(rules.IsModified);
    }

    [Fact]
    public void Presets_EachCiteAClauseAndHaveUniqueKeys()
    {
        Assert.All(GradientRulePresets.All, preset => Assert.Contains("§", preset.Source));
        Assert.Equal(
            GradientRulePresets.All.Count,
            GradientRulePresets.All.Select(preset => preset.Key).Distinct().Count());
        Assert.DoesNotContain(GradientRulePresets.All, preset => preset.Key == GradientRulePresets.CustomKey);
    }

    [Fact]
    public void ChoosingAPreset_CopiesItsLimitsRatherThanReferencingIt()
    {
        var analysis = new GradientComplianceAnalysisDefinition();

        Row("Standard").SetText!(analysis, "adm-vol2-2015");
        GradientRuleSet first = analysis.Rules;
        Row("Standard").SetText!(analysis, "adm-vol2-2015");

        Assert.Equal("adm-vol2-2015", analysis.Rules.PresetKey);
        Assert.NotSame(first, analysis.Rules);
        Assert.Equal(1.0 / 60.0, DegreesToRatio(analysis.Rules.LevelAreaMaxSlopeDegrees), 12);
    }

    [Fact]
    public void ChoosingCustom_KeepsTheNumbersAndDropsTheirSource()
    {
        var analysis = new GradientComplianceAnalysisDefinition();
        Row("Standard").SetText!(analysis, "adm-vol2-2015");

        Row("Standard").SetText!(analysis, GradientRulePresets.CustomKey);

        Assert.Null(analysis.Rules.PresetKey);
        Assert.False(analysis.Rules.IsModified);
        Assert.Equal(1.0 / 60.0, DegreesToRatio(analysis.Rules.LevelAreaMaxSlopeDegrees), 12);
        Assert.Equal(GradientRulePresets.CustomKey, Row("Standard").GetText!(analysis));
    }

    [Fact]
    public void EditingALimit_MarksTheStandardModified_AndEditingItBackClearsIt()
    {
        var analysis = new GradientComplianceAnalysisDefinition();
        double original = analysis.Rules.LevelAreaMaxSlopeDegrees;
        ParameterDescriptor<AnalysisDefinition> limit = Row("LevelAreaMaxSlope");

        limit.SetNumber!(analysis, 2.0);
        Assert.True(analysis.Rules.IsModified);
        Assert.Contains(
            Row("Standard").ChoiceOptionsFor!(analysis),
            option => option.Key == "ada-2010" && option.Label.EndsWith("(modified)", StringComparison.Ordinal));

        limit.SetNumber!(analysis, original);
        Assert.False(analysis.Rules.IsModified);
    }

    [Fact]
    public void EditingTheRuleMode_MarksTheStandardModified()
    {
        var analysis = new GradientComplianceAnalysisDefinition();

        Row("LevelAreaMode").SetText!(analysis, nameof(GradientRuleMode.Report));

        Assert.Equal(GradientRuleMode.Report, analysis.Rules.LevelAreaMode);
        Assert.True(analysis.Rules.IsModified);
    }

    [Fact]
    public void Card_OffersNoColourRamp_AndEveryNumberHasAUnit()
    {
        var parameters = AnalysisTypeRegistry.ForKind(Kind)!.Parameters;

        Assert.DoesNotContain(parameters, p => p.Kind == ParameterKind.ColorRamp);
        Assert.DoesNotContain(
            parameters,
            p => p.Kind is ParameterKind.Number or ParameterKind.Slider && p.Unit == ParameterUnit.None);
        Assert.Equal(ParameterUnit.Slope, Row("LevelAreaMaxSlope").Unit);
        Assert.Equal(ParameterUnit.ModelLength, Row("MeasurementLength").Unit);
    }

    [Fact]
    public void DescribeBlocker_AsksForLevelAreasUntilThereAreSome()
    {
        var descriptor = AnalysisTypeRegistry.ForKind(Kind)!;
        var terrain = new TerrainDefinition();
        var analysis = new GradientComplianceAnalysisDefinition();

        Assert.Contains("Level Areas", descriptor.DescribeBlocker(terrain, analysis));

        analysis.LevelAreas.ObjectIds.Add(Guid.NewGuid());
        Assert.Null(descriptor.DescribeBlocker(terrain, analysis));
    }

    [Fact]
    public void FindDocumentStandard_PrefersThisTerrain_ThenTheDocument_ThenNothing()
    {
        var here = new TerrainDefinition();
        var elsewhere = new TerrainDefinition();
        var documentTerrains = new[] { elsewhere, here };

        Assert.Null(GradientRulePresets.FindDocumentStandard(here, documentTerrains));

        var otherCard = new GradientComplianceAnalysisDefinition { Rules = GradientRulePresets.Find("adm-vol2-2015")!.Create() };
        elsewhere.Analyses.Add(otherCard);
        GradientRuleSet? inherited = GradientRulePresets.FindDocumentStandard(here, documentTerrains);
        Assert.Equal("adm-vol2-2015", inherited!.PresetKey);
        Assert.NotSame(otherCard.Rules, inherited);

        here.Analyses.Add(new GradientComplianceAnalysisDefinition { Rules = GradientRulePresets.CreateCustom() });
        Assert.Null(GradientRulePresets.FindDocumentStandard(here, documentTerrains)!.PresetKey);
    }

    [Fact]
    public void UnitScaler_ScalesTheFootprintButNotTheSlope()
    {
        var analysis = new GradientComplianceAnalysisDefinition { MeasurementLength = 1.5 };
        double slope = analysis.Rules.LevelAreaMaxSlopeDegrees;

        TerrainUnitScaler.Scale(analysis, 1000.0);

        Assert.Equal(1500.0, analysis.MeasurementLength, 9);
        Assert.Equal(slope, analysis.Rules.LevelAreaMaxSlopeDegrees);
    }

    [Fact]
    public void BuildFaceColors_ReportAndWarnReadDifferently()
    {
        var result = new GradientComplianceAnalyzer.Result
        {
            Verdicts = new[]
            {
                GradientComplianceAnalyzer.FaceVerdict.Unchecked,
                GradientComplianceAnalyzer.FaceVerdict.Pass,
                GradientComplianceAnalyzer.FaceVerdict.Exceeds,
            },
            MeasuredSlopeRatios = new[] { double.NaN, 0.01, 0.05 },
        };

        byte[] warn = GradientComplianceEvaluator.BuildFaceColors(result, GradientRuleMode.Warn);
        byte[] report = GradientComplianceEvaluator.BuildFaceColors(result, GradientRuleMode.Report);

        Assert.Equal(GradientComplianceEvaluator.WarnColor.R, warn[6]);
        Assert.Equal(GradientComplianceEvaluator.ReportColor.R, report[6]);
        Assert.Equal(GradientComplianceEvaluator.PassColor.G, warn[4]);
        Assert.Equal(GradientComplianceEvaluator.UncheckedColor.R, warn[0]);
    }

    [Fact]
    public void Report_NamesTheStandard_AndLeavesUnmeasuredFiguresBlank()
    {
        var terrain = new TerrainDefinition { Name = "Site" };
        var measured = new GradientComplianceAnalysisDefinition { Label = "Landings" };
        measured.Rules.LevelAreaMaxSlopeDegrees = 2.0;
        GradientRulePresets.RefreshModified(measured.Rules);
        var unmeasured = new GradientComplianceAnalysisDefinition { Label = "Plaza", Rules = GradientRulePresets.CreateCustom() };
        terrain.Analyses.Add(measured);
        terrain.Analyses.Add(unmeasured);

        var summaries = new[]
        {
            new TerrainAnalysisSummary
            {
                AnalysisId = measured.Id,
                LevelAreaCheckedArea = 120.0,
                LevelAreaExceedingArea = 7.5,
                LevelAreaSteepestSlopeDegrees = Math.Atan(0.03) * 180.0 / Math.PI,
            },
            new TerrainAnalysisSummary { AnalysisId = unmeasured.Id },
        };

        ReportDocument document = TerrainReportBuilder.Build(
            terrain,
            Array.Empty<ZoneAnalysisSummary>(),
            summaries,
            ModelUnitContext.FromUnitSystem(UnitSystem.Meters),
            SlopeAnalyzer.SlopeUnit.Percent,
            new DateTime(2026, 9, 27),
            TerrainReportSections.GradientCompliance);
        ReportTable table = Assert.Single(document.Tables);

        IReadOnlyList<string> landings = table.Rows[0];
        Assert.Equal("ADA 2010 (US) (modified)", landings[1]);
        Assert.Equal(CsvWriter.Number(120.0), landings[2]);
        Assert.Equal(CsvWriter.Number(7.5), landings[3]);
        Assert.Equal(SlopeInput.FormatValueForReport(0.03, SlopeAnalyzer.SlopeUnit.Percent), landings[4]);

        IReadOnlyList<string> plaza = table.Rows[1];
        Assert.Equal("Custom", plaza[1]);
        Assert.Equal(string.Empty, plaza[2]);
        Assert.Equal(string.Empty, plaza[4]);
    }
}
