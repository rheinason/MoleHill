using MoleHill.Core.Analysis;
using MoleHill.Core.Reporting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// What the quantity report says, and — as often — what it declines to say. Every assertion here is
/// about a figure a reader would act on, so a wrong one is worse than a missing one.
/// </summary>
public sealed class TerrainReportBuilderTests
{
    private static readonly ModelUnitContext Meters = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);

    private static ReportTable Table(ReportDocument document, string title) =>
        document.Tables.Single(table => table.Title == title);

    private static ReportDocument Build(
        TerrainDefinition terrain,
        IReadOnlyList<ZoneAnalysisSummary>? zones = null,
        IReadOnlyList<TerrainAnalysisSummary>? analyses = null,
        SlopeAnalyzer.SlopeUnit slopeUnit = SlopeAnalyzer.SlopeUnit.Percent,
        TerrainReportSections sections = TerrainReportSections.All) =>
        TerrainReportBuilder.Build(
            terrain,
            zones ?? Array.Empty<ZoneAnalysisSummary>(),
            analyses ?? Array.Empty<TerrainAnalysisSummary>(),
            Meters,
            slopeUnit,
            new DateTime(2026, 9, 14, 10, 0, 0),
            sections);

    [Fact]
    public void Build_ZoneSchedule_CarriesUnitsOnHeadingsAndTotalsTheMeasuredZones()
    {
        var terrain = new TerrainDefinition { Name = "Site" };
        var first = new CollageZoneDefinition { Name = "Lawn" };
        var second = new CollageZoneDefinition { Name = "Car park" };
        terrain.Zones.Add(first);
        terrain.Zones.Add(second);

        var zones = new[]
        {
            new ZoneAnalysisSummary
            {
                ZoneId = first.ZoneId, PlanArea = 100.0, SurfaceArea = 105.0,
                HasEarthwork = true, CutVolume = 30.0, FillVolume = 10.0
            },
            new ZoneAnalysisSummary
            {
                ZoneId = second.ZoneId, PlanArea = 50.0, SurfaceArea = 50.0,
                HasEarthwork = true, CutVolume = 5.0, FillVolume = 25.0
            }
        };

        ReportTable table = Table(Build(terrain, zones), "Zones");

        Assert.Contains(table.Columns, column => column.DisplayHeading == "Plan Area (m²)");
        Assert.Contains(table.Columns, column => column.DisplayHeading == "Cut (m³)");
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Lawn", table.Rows[0][0]);
        Assert.Equal("Car park", table.Rows[1][0]);

        IReadOnlyList<string> totals = table.Rows[2];
        Assert.Equal("Total", totals[0]);
        Assert.Equal("35.00", totals[10]);
        Assert.Equal("35.00", totals[11]);
        Assert.Equal("0.00", totals[12]);
    }

    /// <summary>
    /// Zone order in the report is zone order in the panel, because that order <em>is</em> the priority
    /// the terrain was resolved with. Summaries come back in whatever order the build produced them.
    /// </summary>
    [Fact]
    public void Build_ZoneSummariesOutOfOrder_StillFollowsPanelOrder()
    {
        var terrain = new TerrainDefinition();
        var first = new CollageZoneDefinition { Name = "First" };
        var second = new CollageZoneDefinition { Name = "Second" };
        terrain.Zones.Add(first);
        terrain.Zones.Add(second);

        var zones = new[]
        {
            new ZoneAnalysisSummary { ZoneId = second.ZoneId },
            new ZoneAnalysisSummary { ZoneId = first.ZoneId }
        };

        ReportTable table = Table(Build(terrain, zones), "Zones");

        Assert.Equal("First", table.Rows[0][0]);
        Assert.Equal("Second", table.Rows[1][0]);
    }

    /// <summary>
    /// A zone nothing measured earthwork for must not report 0 m³ of cut: in a quantity report a zero is
    /// a claim, and this one would be a claim that the zone needs no excavation.
    /// </summary>
    [Fact]
    public void Build_ZoneWithNoEarthwork_LeavesTheVolumesBlank()
    {
        var terrain = new TerrainDefinition();
        var zone = new CollageZoneDefinition { Name = "Lawn" };
        terrain.Zones.Add(zone);

        ReportTable table = Table(
            Build(terrain, new[] { new ZoneAnalysisSummary { ZoneId = zone.ZoneId, PlanArea = 10.0 } }),
            "Zones");

        Assert.Equal(string.Empty, table.Rows[0][10]);
        Assert.Equal(string.Empty, table.Rows[0][11]);
        Assert.Equal("Not measured", table.Rows[0][13]);
    }

    [Fact]
    public void Build_PondingThatMeasuredNothing_LeavesTheQuantitiesBlankRatherThanZero()
    {
        var terrain = new TerrainDefinition();
        var ponding = new PondingAnalysisDefinition { Label = "Ponding" };
        terrain.Analyses.Add(ponding);

        ReportTable table = Table(
            Build(terrain, analyses: new[] { new TerrainAnalysisSummary { AnalysisId = ponding.Id } }),
            "Ponding");

        Assert.Equal("0", table.Rows[0][1]);
        Assert.Equal(string.Empty, table.Rows[0][2]);
        Assert.Equal(string.Empty, table.Rows[0][3]);
    }

    [Fact]
    public void Build_SlopeColumns_AreWrittenInTheRequestedUnit()
    {
        var terrain = new TerrainDefinition();
        var zone = new CollageZoneDefinition { Name = "Bank" };
        terrain.Zones.Add(zone);
        var zones = new[] { new ZoneAnalysisSummary { ZoneId = zone.ZoneId, SlopeAveragePercent = 50.0 } };

        ReportTable percent = Table(Build(terrain, zones), "Zones");
        ReportTable ratio = Table(Build(terrain, zones, slopeUnit: SlopeAnalyzer.SlopeUnit.Ratio), "Zones");

        Assert.Equal("50.0", percent.Rows[0][7]);
        Assert.Contains(percent.Columns, column => column.DisplayHeading == "Mean Slope (%)");

        // 50% is a rise of one in two, and a:b is read vertical:horizontal throughout the product.
        Assert.Equal("1:2.0", ratio.Rows[0][7]);
    }

    [Fact]
    public void Build_SectionsNotSelected_AreAbsentEntirely()
    {
        var terrain = new TerrainDefinition();
        var zone = new CollageZoneDefinition();
        terrain.Zones.Add(zone);

        ReportDocument document = Build(
            terrain,
            new[] { new ZoneAnalysisSummary { ZoneId = zone.ZoneId } },
            sections: TerrainReportSections.Zones);

        Assert.Single(document.Tables);
        Assert.Equal("Zones", document.Tables[0].Title);
    }

    [Fact]
    public void Build_NothingMeasured_ProducesNoZonesOrAnalysisTables()
    {
        ReportDocument document = Build(new TerrainDefinition { Name = "Empty" });

        Assert.DoesNotContain(document.Tables, table => table.Title == "Zones");
        Assert.DoesNotContain(document.Tables, table => table.Title == "Earthworks");
        Assert.DoesNotContain(document.Tables, table => table.Title == "Ponding");
    }

    [Fact]
    public void Build_Earthworks_ReportsWhetherTheVolumesAreExact()
    {
        var terrain = new TerrainDefinition();
        var earthwork = new EarthworkAnalysisDefinition { Label = "Earthworks" };
        terrain.Analyses.Add(earthwork);

        var summary = new TerrainAnalysisSummary
        {
            AnalysisId = earthwork.Id,
            CutVolume = 120.0,
            FillVolume = 80.0,
            NetVolume = 40.0,
            EarthworkIsEstimated = false
        };

        ReportTable table = Table(Build(terrain, analyses: new[] { summary }), "Earthworks");

        Assert.Equal(new[] { "Earthworks", "120.00", "80.00", "40.00", "Exact" }, table.Rows[0]);
    }

    [Fact]
    public void Build_CutFillAnalysis_ReportsItsVolumesBesideEarthworks()
    {
        var terrain = new TerrainDefinition();
        var earthwork = new EarthworkAnalysisDefinition { Label = "Earthworks" };
        var cutFill = new CutFillAnalysisDefinition { Label = "Cut / Fill" };
        terrain.Analyses.Add(earthwork);
        terrain.Analyses.Add(cutFill);

        var summaries = new[]
        {
            new TerrainAnalysisSummary
            {
                AnalysisId = earthwork.Id, CutVolume = 120.0, FillVolume = 80.0, NetVolume = 40.0,
                EarthworkIsEstimated = false
            },
            new TerrainAnalysisSummary
            {
                AnalysisId = cutFill.Id, CutVolume = 10.0, FillVolume = 30.0, NetVolume = -20.0,
                EarthworkIsEstimated = true
            }
        };

        ReportTable table = Table(Build(terrain, analyses: summaries), "Earthworks");

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { "Earthworks", "120.00", "80.00", "40.00", "Exact" }, table.Rows[0]);
        Assert.Equal(new[] { "Cut / Fill", "10.00", "30.00", "-20.00", "Estimated" }, table.Rows[1]);
    }
}
