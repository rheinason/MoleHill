using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class ScatterReportTests
{
    [Fact]
    public void Build_PlantingSection_UsesPlacedCountsAndOmitsUnmeasuredCards()
    {
        var scatter = new ScatterObjectDefinition { Name = "Woodland" };
        var terrain = new TerrainDefinition { Objects = { scatter, new ScatterObjectDefinition { Name = "Unbuilt" } } };
        var result = new ScatterBuildSummary
        {
            DefinitionId = scatter.Id, Sampled = 100,
            BlockCounts = new Dictionary<string, int> { ["Oak"] = 30, ["Birch"] = 10 }
        };
        var report = TerrainReportBuilder.Build(terrain, Array.Empty<ZoneAnalysisSummary>(), Array.Empty<TerrainAnalysisSummary>(),
            new ModelUnitContext(UnitSystem.Meters, 1, "m", 0.001), SlopeAnalyzer.SlopeUnit.Percent,
            DateTime.UnixEpoch, TerrainReportSections.Planting, new[] { result });
        var table = Assert.Single(report.Tables);
        Assert.Equal("Planting — Woodland", table.Title);
        Assert.Equal(new[] { "Birch", "10", "25.00" }, table.Rows[0]);
        Assert.Equal(new[] { "Oak", "30", "75.00" }, table.Rows[1]);
        Assert.Equal(new[] { "Total", "40", "100" }, table.Rows[2]);
    }
}
