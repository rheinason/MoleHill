using System.Globalization;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The card readouts moved out of the panel's per-type switches into
/// <c>DescribeResult</c> on each descriptor. The expected strings below were transcribed from the
/// hand-written panel code they replaced, so a drift in any label, value or tooltip fails here instead of
/// shipping as a quietly reworded card.
/// </summary>
public class ResultRowTests
{
    private static readonly ResultFormatter Format = new(
        Area: v => $"{v:0.##} m2",
        Volume: v => $"{v:0.##} m3",
        Length: v => $"{v:0.##} m",
        SlopeDegrees: v => $"{v:0.#} deg");

    private static IReadOnlyList<ResultRow> Analysis(AnalysisDefinition definition, TerrainAnalysisSummary? summary)
    {
        using var _ = new InvariantCulture();
        return AnalysisTypeRegistry.ForType(definition.GetType())!.DescribeResult(definition, summary, Format);
    }

    private static IReadOnlyList<ResultRow> Annotation(AnnotationDefinition definition, TerrainAnalysisSummary? summary)
    {
        using var _ = new InvariantCulture();
        return AnnotationTypeRegistry.ForType(definition.GetType())!.DescribeResult(definition, summary, Format);
    }

    private sealed class InvariantCulture : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public InvariantCulture() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }

    [Fact]
    public void RebuildRequired_ForEveryTypeThatShowsOne_IsTheSameCopyableSummary()
    {
        var row = Assert.Single(Annotation(new ContourAnnotationDefinition(), null));

        Assert.Equal(
            new ResultRow("Summary", "Rebuild required", "Rebuild the terrain to generate contour curves.",
                ResultRowKind.SelectableSummary, 42),
            row);
    }

    [Fact]
    public void Earthwork_WithSummary_ListsCutFillNetAndMode()
    {
        var rows = Analysis(new EarthworkAnalysisDefinition(), new TerrainAnalysisSummary
        {
            CutVolume = 12.5,
            FillVolume = 3,
            NetVolume = -9.5,
            EarthworkIsEstimated = true
        });

        ResultRow row = Assert.Single(rows);
        Assert.Equal(ResultRowKind.SelectableSummary, row.Kind);
        Assert.Equal(110, row.MinHeight);
        Assert.Equal(
            $"Cut: 12.5 m3{Environment.NewLine}Fill: 3 m3{Environment.NewLine}Net: -9.5 m3{Environment.NewLine}Mode: Estimated from terrain delta",
            row.Value);
        Assert.Equal("Earthwork summary from the last terrain build. Click into the field to select and copy values.", row.Help);
    }

    [Fact]
    public void Earthwork_WithoutSummary_AsksForARebuild()
    {
        ResultRow row = Assert.Single(Analysis(new EarthworkAnalysisDefinition(), null));

        Assert.Equal("Rebuild required", row.Value);
        Assert.Equal("Rebuild the terrain to populate earthwork values.", row.Help);
    }

    [Fact]
    public void Slope_WithoutSummary_ShowsNothing()
    {
        Assert.Empty(Analysis(new SlopeAnalysisDefinition(), null));
    }

    [Fact]
    public void Slope_WithSummary_ShowsMinAvgMaxInTheCardsUnit()
    {
        var slope = new SlopeAnalysisDefinition { Unit = SlopeAnalyzer.SlopeUnit.Percent };
        ResultRow row = Assert.Single(Analysis(slope, new TerrainAnalysisSummary
        {
            SlopeMinPercent = 1,
            SlopeAveragePercent = 5,
            SlopeMaxPercent = 20
        }));

        Assert.Equal("Min / Avg / Max", row.Label);
        Assert.Equal("1.0% / 5.0% / 20.0%", row.Value);
        Assert.Equal("Current terrain slope summary from the last build.", row.Help);
    }

    [Fact]
    public void Aspect_ReportsFacingAndFlatShare()
    {
        var rows = Analysis(new AspectAnalysisDefinition(), new TerrainAnalysisSummary
        {
            AspectDominantBearing = null,
            AspectFaceCount = 8,
            AspectFlatFaceCount = 2
        });

        Assert.Equal(2, rows.Count);
        Assert.Equal("Facing", rows[0].Label);
        Assert.Equal("None — no mean direction", rows[0].Value);
        Assert.Equal("Flat", rows[1].Label);
        Assert.Equal("2 of 8 (25 %)", rows[1].Value);
        Assert.Equal("Faces too flat to have an aspect. These are drawn neutral grey, not given a direction.", rows[1].Help);
    }

    [Fact]
    public void Aspect_WithNoFaces_ShowsADashForFlat()
    {
        var rows = Analysis(new AspectAnalysisDefinition(), new TerrainAnalysisSummary { AspectDominantBearing = 90 });

        Assert.Equal("—", rows[1].Value);
        Assert.Equal($"{AspectAnalyzer.SectorName(90)} (90°)", rows[0].Value);
    }

    [Fact]
    public void Elevation_ShowsSurfaceArea_OnlyOnceBuilt()
    {
        Assert.Empty(Analysis(new ElevationAnalysisDefinition(), null));

        ResultRow row = Assert.Single(Analysis(new ElevationAnalysisDefinition(), new TerrainAnalysisSummary { SurfaceArea = 250.5 }));
        Assert.Equal(new ResultRow("Area", "250.5 m2", "Terrain surface area from the last build."), row);
    }

    [Fact]
    public void CutFill_DeltaRow_AppearsOnlyWhenDeltaOutputIsDrawn()
    {
        var summary = new TerrainAnalysisSummary
        {
            CutVolume = 1,
            FillVolume = 2,
            NetVolume = 3.456,
            CutFillDeltaContourCount = 1200,
            CutFillBalanceCurveCount = 0
        };

        var plain = new CutFillAnalysisDefinition { ShowDeltaContours = false, ShowBalanceLine = false };
        ResultRow only = Assert.Single(Analysis(plain, summary));
        Assert.Equal("Cut / Fill / Net", only.Label);
        Assert.Equal("1.00 / 2.00 / 3.46", only.Value);

        var drawn = new CutFillAnalysisDefinition { ShowDeltaContours = true, ShowBalanceLine = true };
        Assert.True(drawn.DrawsDeltaOutput);
        var rows = Analysis(drawn, summary);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Drawn", rows[1].Label);
        Assert.Equal("1,200 delta contour(s) | balance: None — no sign change", rows[1].Value);
    }

    [Fact]
    public void Catchment_ListsCountsAndOnlyMentionsDrawingWhenOutputIsOn()
    {
        var summary = new TerrainAnalysisSummary
        {
            CatchmentBasinCount = 7,
            CatchmentLargestArea = 40,
            CatchmentSinkCount = 2,
            GeneratedOutputCount = 9
        };

        var rows = Analysis(new CatchmentAnalysisDefinition { ShowBoundaries = true, ShowFlowPaths = false }, summary);

        Assert.Equal(new[] { "Catchments", "Closed depressions", "Drawn" }, rows.Select(r => r.Label).ToArray());
        Assert.Equal("7 | largest 40 m2", rows[0].Value);
        Assert.Equal("2 — water has nowhere to go", rows[1].Value);
        Assert.Equal("9 curve(s)", rows[2].Value);

        var none = Analysis(
            new CatchmentAnalysisDefinition { ShowBoundaries = false, ShowFlowPaths = false },
            new TerrainAnalysisSummary { CatchmentBasinCount = 3 });
        Assert.Equal("3", none[0].Value);
        Assert.Equal("None", none[1].Value);
        Assert.Equal(2, none.Count);
    }

    [Fact]
    public void Ponding_WithNoDepressions_SaysEverywhereDrains()
    {
        ResultRow row = Assert.Single(Analysis(new PondingAnalysisDefinition(), new TerrainAnalysisSummary { PondCount = 0 }));

        Assert.Equal("Standing water", row.Label);
        Assert.Equal("None — everywhere drains", row.Value);
    }

    [Fact]
    public void Ponding_WithDepressions_AddsDepthVolumeAndArea()
    {
        var rows = Analysis(new PondingAnalysisDefinition(), new TerrainAnalysisSummary
        {
            PondCount = 3,
            PondMaxDepth = 0.5,
            PondTotalVolume = 12,
            PondTotalArea = 30
        });

        Assert.Equal("3 depression(s)", rows[0].Value);
        Assert.Equal("Deepest / Volume", rows[1].Label);
        Assert.Equal("0.5 m / 12 m3", rows[1].Value);
        Assert.Equal("Wet area", rows[2].Label);
        Assert.Equal("30 m2", rows[2].Value);
    }

    [Fact]
    public void Waterflow_ShowsPathsAndHowTheyEnded()
    {
        var rows = Analysis(new WaterflowAnalysisDefinition(), new TerrainAnalysisSummary
        {
            SampleSourceCount = 4,
            GeneratedOutputCount = 4,
            WaterflowBoundaryCount = 2,
            WaterflowSinkCount = 1,
            WaterflowRejectedCount = 1
        });

        Assert.Equal("4 point(s) -> 4 path(s)", rows[0].Value);
        Assert.Equal("2 boundary | 1 sink | 1 outside", rows[1].Value);
    }

    [Fact]
    public void GradientCompliance_WithNothingChecked_SaysSo()
    {
        ResultRow row = Assert.Single(Analysis(new GradientComplianceAnalysisDefinition(), new TerrainAnalysisSummary()));

        Assert.Equal("Result", row.Label);
        Assert.Equal("None checked", row.Value);
    }

    [Fact]
    public void GradientCompliance_LevelAreas_AreAnsweredAsAVerdict()
    {
        var definition = new GradientComplianceAnalysisDefinition();
        definition.Rules.LevelAreaMode = GradientRuleMode.Warn;

        var rows = Analysis(definition, new TerrainAnalysisSummary
        {
            LevelAreaCheckedArea = 100,
            LevelAreaExceedingArea = 10,
            LevelAreaSteepestSlopeDegrees = 3
        });

        Assert.Equal("Level Result", rows[0].Label);
        Assert.Equal("10 m2 of 100 m2 fails", rows[0].Value);
        Assert.Equal("Level Steepest", rows[1].Label);
        Assert.Equal("3 deg", rows[1].Value);

        rows = Analysis(definition, new TerrainAnalysisSummary
        {
            LevelAreaCheckedArea = 100,
            LevelAreaSteepestSlopeDegrees = 1
        });
        Assert.Equal("All 100 m2 within the limit", rows[0].Value);
    }

    [Fact]
    public void GradientCompliance_Routes_ListResultRampsSteepestAndRuns()
    {
        var definition = new GradientComplianceAnalysisDefinition();
        definition.Rules.RouteMode = GradientRuleMode.Warn;

        var rows = Analysis(definition, new TerrainAnalysisSummary
        {
            RouteCheckedArea = 50,
            RouteRunningExceedingArea = 5,
            RouteCrossExceedingArea = 0,
            RouteRampArea = 7,
            RouteSteepestRunningDegrees = 4,
            RouteSteepestCrossDegrees = 2,
            RouteRunCount = 5,
            RouteFailedRunCount = 2,
            RouteLandingCount = 3,
            RouteLargestRunRise = 1.5
        });

        Assert.Equal(
            new[] { "Route Result", "Route Ramps", "Route Steepest", "Route Runs" },
            rows.Select(r => r.Label).ToArray());
        Assert.Equal("5 m2 running, 0 m2 cross fail, of 50 m2", rows[0].Value);
        Assert.Equal("7 m2", rows[1].Value);
        Assert.Equal("4 deg running, 2 deg cross", rows[2].Value);
        Assert.Equal("2 of 5 runs too high or long, 3 landing(s) found, largest rise 1.5 m", rows[3].Value);
    }

    [Fact]
    public void CurveElevation_UsesTheCardsValueFormat_AndSaysNoSamplesWhenEmpty()
    {
        var definition = new CurveElevationLabelAnnotationDefinition { ValueFormat = "F1" };
        var rows = Annotation(definition, new TerrainAnalysisSummary
        {
            SampleSourceCount = 2,
            GeneratedOutputCount = 5,
            SampleMinValue = 10.04,
            SampleMaxValue = 12.96
        });

        Assert.Equal(new ResultRow(
            "Curves / Labels",
            "2 curve(s) -> 5 label(s)",
            "Curve sources resolved and elevation annotation blocks emitted by the last build."), rows[0]);
        Assert.Equal(new ResultRow(
            "Min / Max",
            "10.0 / 13.0",
            "Terrain elevations sampled along the source curves during the last build."), rows[1]);

        rows = Annotation(definition, new TerrainAnalysisSummary { GeneratedOutputCount = 0 });
        Assert.Equal("No samples", rows[1].Value);
    }

    [Fact]
    public void SlopeAnnotations_ShowMinAvgMaxInTheirOwnUnit()
    {
        var summary = new TerrainAnalysisSummary
        {
            GeneratedOutputCount = 3,
            SampleMinValue = 1,
            SampleAverageValue = 2,
            SampleMaxValue = 3
        };

        var arrows = Annotation(new SlopeArrowAnnotationDefinition { Unit = SlopeAnalyzer.SlopeUnit.Percent }, summary);
        Assert.Equal("Arrows", arrows[0].Label);
        Assert.Equal("3 arrow(s)", arrows[0].Value);
        Assert.Equal("1.0% / 2.0% / 3.0%", arrows[1].Value);

        var points = Annotation(new PointSlopeLabelAnnotationDefinition { Unit = SlopeAnalyzer.SlopeUnit.Degrees }, summary);
        Assert.Equal("Points / Labels", points[0].Label);
        Assert.Equal("1.0 deg / 2.0 deg / 3.0 deg", points[1].Value);
    }

    [Fact]
    public void GradeCallout_LabelsItsPercentRow()
    {
        var rows = Annotation(new GradeBetweenPointsAnnotationDefinition { ValueFormat = "F0" }, new TerrainAnalysisSummary
        {
            SampleSourceCount = 1,
            GeneratedOutputCount = 1,
            SampleMinValue = 4.4,
            SampleAverageValue = 5,
            SampleMaxValue = 5.6
        });

        Assert.Equal("Lines / Callouts", rows[0].Label);
        Assert.Equal("1 line(s) -> 1 callout(s)", rows[0].Value);
        Assert.Equal("Min / Avg / Max %", rows[1].Label);
        Assert.Equal("4 / 5 / 6", rows[1].Value);
    }

    [Fact]
    public void Contour_ShowsCurveCountAndLevelSpan()
    {
        var rows = Annotation(new ContourAnnotationDefinition(), new TerrainAnalysisSummary
        {
            ContourCurveCount = 12,
            ContourLevelCount = 4,
            ContourFirstLevel = 100,
            ContourLastLevel = 103.5
        });

        Assert.Equal("12 curve(s) across 4 level(s)", rows[0].Value);
        Assert.Equal("100 to 103.5", rows[1].Value);

        rows = Annotation(new ContourAnnotationDefinition(), new TerrainAnalysisSummary());
        Assert.Equal("No contour levels intersected the terrain", rows[1].Value);
    }

    [Theory]
    [InlineData(typeof(TerrainSectionAnnotationDefinition), "Cuts / Terrains / C-F", "Cut curves")]
    [InlineData(typeof(CrossSectionStationAnnotationDefinition), "Alignments / Terrains / C-F", "Alignments")]
    [InlineData(typeof(LongitudinalSectionAnnotationDefinition), "Curves / Terrains / C-F", "Curves")]
    public void Sections_ShowSourcesTerrainsAndCutFillRegions(Type definitionType, string label, string noun)
    {
        var definition = (AnnotationDefinition)Activator.CreateInstance(definitionType)!;
        ResultRow row = Assert.Single(Annotation(definition, new TerrainAnalysisSummary
        {
            SampleSourceCount = 2,
            SectionTerrainCount = 3,
            SectionCutRegionCount = 4,
            SectionFillRegionCount = 5,
            GeneratedOutputCount = 60
        }));

        Assert.Equal(label, row.Label);
        Assert.Equal("2 / 3 / 4-5", row.Value);
        Assert.Equal($"{noun}, available terrain profiles, and cut-fill regions from the last build (60 objects).", row.Help);
    }

    [Fact]
    public void ReportTable_ShowsWhatWasDrawn_OrAnEmptyNote()
    {
        ResultRow drawn = Assert.Single(Annotation(new ReportTableAnnotationDefinition(), new TerrainAnalysisSummary
        {
            ReportTableCount = 2,
            ReportRowCount = 14
        }));
        Assert.Equal("Drawn", drawn.Label);
        Assert.Equal("2 table(s) | 14 row(s)", drawn.Value);

        ResultRow empty = Assert.Single(Annotation(new ReportTableAnnotationDefinition(), new TerrainAnalysisSummary()));
        Assert.Equal("Nothing measured yet", empty.Value);

        ResultRow unbuilt = Assert.Single(Annotation(new ReportTableAnnotationDefinition(), null));
        Assert.Equal("Rebuild the terrain to draw the report table.", unbuilt.Help);
    }

    [Fact]
    public void Legend_HasNoResultRows_BecauseThePreviewDrawsIt()
    {
        Assert.Empty(Annotation(new LegendAnnotationDefinition(), new TerrainAnalysisSummary()));
        Assert.Empty(Annotation(new LegendAnnotationDefinition(), null));
    }
}
