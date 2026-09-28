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
    public void BuildFaceColors_ReportAndWarnReadDifferently_AndLevelAreasOverrideRoutes()
    {
        var level = new GradientComplianceAnalyzer.Result
        {
            Verdicts = new[]
            {
                GradientComplianceAnalyzer.FaceVerdict.Unchecked,
                GradientComplianceAnalyzer.FaceVerdict.Pass,
                GradientComplianceAnalyzer.FaceVerdict.Exceeds,
                GradientComplianceAnalyzer.FaceVerdict.Unchecked,
                GradientComplianceAnalyzer.FaceVerdict.Unchecked,
            },
            MeasuredSlopeRatios = new[] { double.NaN, 0.01, 0.05, double.NaN, double.NaN },
        };
        var routes = GradientComplianceAnalyzer.RouteResult.Empty(5);
        routes.Verdicts[1] = GradientComplianceAnalyzer.RouteVerdict.RunningExceeds; // overridden by the level pass
        routes.Verdicts[3] = GradientComplianceAnalyzer.RouteVerdict.Ramp;
        routes.Verdicts[4] = GradientComplianceAnalyzer.RouteVerdict.CrossExceeds;
        var evaluation = new GradientComplianceEvaluator.Evaluation(level, routes);

        var rules = GradientRulePresets.Default.Create();
        byte[] warn = GradientComplianceEvaluator.BuildFaceColors(evaluation, rules);
        rules.LevelAreaMode = GradientRuleMode.Report;
        rules.RouteMode = GradientRuleMode.Report;
        byte[] report = GradientComplianceEvaluator.BuildFaceColors(evaluation, rules);

        Assert.Equal(GradientComplianceEvaluator.UncheckedColor.R, warn[0]);
        Assert.Equal(GradientComplianceEvaluator.PassColor.G, warn[4]);
        Assert.Equal(GradientComplianceEvaluator.WarnColor.R, warn[6]);
        Assert.Equal(GradientComplianceEvaluator.ReportColor.R, report[6]);
        Assert.Equal(GradientComplianceEvaluator.RampColor.B, warn[11]);
        Assert.Equal(GradientComplianceEvaluator.RampColor.B, report[11]);
        Assert.Equal(GradientComplianceEvaluator.WarnColor.R, warn[12]);
        Assert.Equal(GradientComplianceEvaluator.ReportColor.R, report[12]);
    }

    [Theory]
    [InlineData("ada-2010", 20.0, 12.0, 48.0)]
    [InlineData("adm-vol2-2015", 20.0, 12.0, 40.0)]
    public void Presets_CarryTheirPublishedRouteLimits(string key, double walkRun, double rampRun, double crossRun)
    {
        GradientRuleSet rules = GradientRulePresets.Find(key)!.Create();

        Assert.Equal(1.0 / walkRun, DegreesToRatio(rules.WalkMaxSlopeDegrees), 12);
        Assert.Equal(1.0 / rampRun, DegreesToRatio(rules.RampMaxSlopeDegrees), 12);
        Assert.Equal(1.0 / crossRun, DegreesToRatio(rules.CrossMaxSlopeDegrees), 12);
        Assert.Equal(GradientRuleMode.Warn, rules.RouteMode);
    }

    [Theory]
    [InlineData("WalkMaxSlope")]
    [InlineData("RampMaxSlope")]
    [InlineData("CrossMaxSlope")]
    public void EditingARouteLimit_MarksTheStandardModified(string key)
    {
        var analysis = new GradientComplianceAnalysisDefinition();

        Row(key).SetNumber!(analysis, 5.0);

        Assert.True(analysis.Rules.IsModified);
        Assert.Equal(ParameterUnit.Slope, Row(key).Unit);
    }

    [Fact]
    public void DescribeBlocker_IsSatisfiedByRoutesAlone()
    {
        var descriptor = AnalysisTypeRegistry.ForKind(Kind)!;
        var analysis = new GradientComplianceAnalysisDefinition();
        analysis.Routes.ObjectIds.Add(Guid.NewGuid());

        Assert.Null(descriptor.DescribeBlocker(new TerrainDefinition(), analysis));

        analysis.Rules.RouteMode = GradientRuleMode.Off;
        Assert.NotNull(descriptor.DescribeBlocker(new TerrainDefinition(), analysis));
    }

    [Fact]
    public void RoutesAndWidth_RoundTripAndScale()
    {
        var analysis = new GradientComplianceAnalysisDefinition { RouteWidth = 1.8 };
        analysis.Routes.ObjectIds.Add(Guid.NewGuid());
        analysis.Rules.RouteMode = GradientRuleMode.Report;

        GradientComplianceAnalysisDefinition restored = RoundTrip(analysis);
        Assert.Equal(1.8, restored.RouteWidth);
        Assert.Single(restored.Routes.ObjectIds);
        Assert.Equal(GradientRuleMode.Report, restored.Rules.RouteMode);
        Assert.Contains(restored.Routes, restored.EnumerateSourceSets());

        TerrainUnitScaler.Scale(restored, 1000.0);
        Assert.Equal(1800.0, restored.RouteWidth, 9);
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

        Assert.Equal(string.Empty, landings[5]);
        Assert.Equal(14, table.Columns.Count);

        IReadOnlyList<string> plaza = table.Rows[1];
        Assert.Equal("Custom", plaza[1]);
        Assert.Equal(string.Empty, plaza[2]);
        Assert.Equal(string.Empty, plaza[4]);
    }

    [Fact]
    public void Presets_CarryTheirPublishedRunAndLandingLimits()
    {
        GradientRuleSet ada = GradientRulePresets.Find("ada-2010")!.Create();
        Assert.Equal(1.525, ada.LandingMinLength, 9);   // §405.7.3
        Assert.Equal(0.76, ada.RampMaxRise, 9);         // §405.6
        Assert.Equal(0.0, ada.WalkMaxRise);             // no walk rise limit
        Assert.Empty(ada.RampGoingLimits);

        GradientRuleSet adm = GradientRulePresets.Find("adm-vol2-2015")!.Create();
        Assert.Equal(1.5, adm.LandingMinLength, 9);     // §1.26(i)
        Assert.Equal(0.5, adm.WalkMaxRise, 9);          // §1.13(c)
        Assert.Equal(0.5, adm.RampMaxRise, 9);          // §1.26(c)
        Assert.True(adm.InterpolateGoing);
        Assert.Equal(
            new[] { (20.0, 10.0), (15.0, 5.0), (12.0, 2.0) },
            adm.RampGoingLimits.Select(limit => (Math.Round(1.0 / DegreesToRatio(limit.SlopeDegrees), 6), limit.MaxGoing)));
    }

    /// <summary>
    /// Presets are written in metres. In a millimetre document a card is created at 1000 model units per
    /// metre, and choosing a standard must land in millimetres too, and must not read as modified.
    /// </summary>
    [Fact]
    public void ChoosingAPreset_InAMillimetreDocument_ScalesItsLengthsAndIsNotModified()
    {
        var analysis = new GradientComplianceAnalysisDefinition();
        TerrainUnitScaler.Scale(analysis, 1000.0);
        Assert.Equal(1525.0, analysis.Rules.LandingMinLength, 6);

        Row("Standard").SetText!(analysis, "adm-vol2-2015");

        Assert.Equal(1500.0, analysis.Rules.LandingMinLength, 6);
        Assert.Equal(10_000.0, analysis.Rules.RampGoingLimits[0].MaxGoing, 6);
        Assert.Equal(1000.0, analysis.Rules.ModelUnitsPerMeter, 9);
        Assert.False(analysis.Rules.IsModified);

        Row("RampMaxRise").SetNumber!(analysis, 400.0);
        Assert.True(analysis.Rules.IsModified);
        Row("RampMaxRise").SetNumber!(analysis, 500.0);
        Assert.False(analysis.Rules.IsModified);
    }

    [Fact]
    public void RuleSetClone_DoesNotShareTheGoingTable()
    {
        GradientRuleSet adm = GradientRulePresets.Find("adm-vol2-2015")!.Create();

        GradientRuleSet clone = adm.Clone();
        clone.RampGoingLimits[0].MaxGoing = 1.0;

        Assert.Equal(10.0, adm.RampGoingLimits[0].MaxGoing);
    }

    [Fact]
    public void RunRules_RoundTrip()
    {
        var analysis = new GradientComplianceAnalysisDefinition { Rules = GradientRulePresets.Find("adm-vol2-2015")!.Create() };

        GradientComplianceAnalysisDefinition restored = RoundTrip(analysis);

        Assert.Equal(3, restored.Rules.RampGoingLimits.Count);
        Assert.Equal(0.5, restored.Rules.WalkMaxRise);
        Assert.True(restored.Rules.InterpolateGoing);
        Assert.Equal(1.0, restored.Rules.ModelUnitsPerMeter);
    }

    [Fact]
    public void GoingTable_AddCopiesTheSteepestRow_AndRemoveDropsIt_BothMarkingModified()
    {
        var analysis = new GradientComplianceAnalysisDefinition { Rules = GradientRulePresets.Find("adm-vol2-2015")!.Create() };
        GradientRuleSet rules = analysis.Rules;

        rules.AddGoingLimit();
        GradientRulePresets.RefreshModified(rules);

        Assert.Equal(4, rules.RampGoingLimits.Count);
        Assert.Equal(1.0 / 12.0, DegreesToRatio(rules.RampGoingLimits[3].SlopeDegrees), 12);
        Assert.Equal(2.0, rules.RampGoingLimits[3].MaxGoing);
        Assert.True(rules.IsModified);

        rules.RemoveGoingLimitAt(3);
        rules.RemoveGoingLimitAt(99);
        GradientRulePresets.RefreshModified(rules);

        Assert.Equal(3, rules.RampGoingLimits.Count);
        Assert.False(rules.IsModified);
    }

    [Fact]
    public void GoingTable_AddToAnEmptyTable_StartsAtOneInTwelveOverTwoMetresInModelUnits()
    {
        var rules = new GradientRuleSet { ModelUnitsPerMeter = 1000.0 };

        rules.AddGoingLimit();

        Assert.Equal(1.0 / 12.0, DegreesToRatio(Assert.Single(rules.RampGoingLimits).SlopeDegrees), 12);
        Assert.Equal(2000.0, rules.RampGoingLimits[0].MaxGoing, 9);
    }

    [Fact]
    public void Card_DeclaresTheGoingTableAsAnInPlaceEditor()
    {
        Assert.Equal(ParameterKind.GoingTable, Row("RampGoingLimits").Kind);
    }
}
