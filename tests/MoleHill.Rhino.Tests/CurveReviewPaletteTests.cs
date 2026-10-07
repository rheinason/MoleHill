using MoleHill.Core.Analysis;
using System.Drawing;
using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class CurveReviewPaletteTests
{
    [Fact]
    public void Build_Grade_ColorsTheSteepIntervalHotterThanTheFlatOne()
    {
        CurveReviewAnalysis analysis = Analysis(
            elevations: new[] { 0.0, 0.1, 10.0 },
            grades: new[] { 1.0, 40.0 },
            gradeLimit: null);

        var series = CurveReviewMetricSeries.Build(analysis, CurveReviewMetric.Grade, "m");

        Assert.True(Warmth(series.IntervalColor(1)) > Warmth(series.IntervalColor(0)));
    }

    [Fact]
    public void Build_CutFill_PutsCutAndFillOnOppositeSidesOfNeutral()
    {
        CurveReviewAnalysis analysis = Analysis(
            elevations: new[] { 0.0, 5.0, -5.0 },
            grades: new[] { 10.0, 10.0 },
            gradeLimit: null,
            terrain: new double?[] { 0.0, 0.0, 0.0 });

        var series = CurveReviewMetricSeries.Build(analysis, CurveReviewMetric.CutFill, "m");

        Color fill = series.ColorFor(5.0);
        Color cut = series.ColorFor(-5.0);
        Assert.True(fill.B > fill.R, "fill should read blue");
        Assert.True(cut.R > cut.B, "cut should read warm");
    }

    [Fact]
    public void Build_CutFill_WithoutTerrain_ReportsNoReadings()
    {
        CurveReviewAnalysis analysis = Analysis(
            elevations: new[] { 0.0, 1.0, 2.0 },
            grades: new[] { 5.0, 5.0 },
            gradeLimit: null);

        var series = CurveReviewMetricSeries.Build(analysis, CurveReviewMetric.CutFill, "m");

        Assert.False(series.IsAvailable);
    }

    [Fact]
    public void Build_PlanRadius_RanksCornerTighterThanStraight()
    {
        CurveReviewAnalysis analysis = Analysis(
            elevations: new[] { 0.0, 0.0, 0.0 },
            grades: new[] { 0.0, 0.0 },
            gradeLimit: null,
            radii: new[] { double.PositiveInfinity, 10.0, double.NaN });

        var series = CurveReviewMetricSeries.Build(analysis, CurveReviewMetric.PlanRadius, "m");

        // Tightness, not radius: straight is coolest, a true corner is at the hot end of the ramp.
        Assert.Equal(0.0, series.ValueAt(0));
        Assert.Equal(1.0, series.ValueAt(2));
        Assert.True(series.ValueAt(1) > series.ValueAt(0));
    }

    [Fact]
    public void RampAt_IsClampedAndContinuous()
    {
        CurveReviewAnalysis analysis = Analysis(
            elevations: new[] { 0.0, 1.0, 2.0 },
            grades: new[] { 1.0, 2.0 },
            gradeLimit: 10.0);

        var series = CurveReviewMetricSeries.Build(analysis, CurveReviewMetric.Grade, "m");

        Assert.Equal(series.RampAt(0.0), series.RampAt(-5.0));
        Assert.Equal(series.RampAt(1.0), series.RampAt(5.0));
        Assert.True(Warmth(series.RampAt(1.0)) > Warmth(series.RampAt(0.0)));
    }

    /// <summary>How far along a cool-to-hot ramp a colour sits, so ordering can be asserted without pinning RGB.</summary>
    private static int Warmth(Color color) => color.R - color.G;

    private static CurveReviewAnalysis Analysis(
        double[] elevations,
        double[] grades,
        double? gradeLimit,
        double?[]? terrain = null,
        double[]? radii = null)
    {
        var samples = new List<CurveReviewSample>();
        for (int i = 0; i < elevations.Length; i++)
        {
            double? terrainZ = terrain?[i];
            samples.Add(new CurveReviewSample(i * 10.0, new Point3d(i * 10.0, 0.0, elevations[i]), terrainZ ?? 0.0, terrainZ.HasValue));
        }

        return new CurveReviewAnalysis
        {
            Samples = samples,
            IntervalGrades = grades,
            PlanRadii = radii ?? Enumerable.Repeat(double.PositiveInfinity, elevations.Length).ToArray(),
            Spans = Array.Empty<CurveReviewSpan>(),
            Events = Array.Empty<CurveReviewEvent>(),
            GradeExceedances = Array.Empty<CurveReviewRun>(),
            RadiusViolations = Array.Empty<CurveReviewRun>(),
            Checks = Array.Empty<CurveReviewCheckResult>(),
            PlanLength = (elevations.Length - 1) * 10.0,
            Length3d = (elevations.Length - 1) * 10.0,
            StartElevation = elevations[0],
            EndElevation = elevations[^1],
            MinimumGrade = grades.Min(),
            MaximumGrade = grades.Max(),
            AverageGrade = grades.Average(),
            SteepestGrade = grades.Max(),
            SteepestGradeStation = 0.0,
            ReversalCount = 0,
            VerticalBreakCount = 0,
            KinkCount = 0,
            PlanCornerCount = 0,
            MinimumPlanRadius = double.PositiveInfinity,
            MinimumPlanRadiusStation = double.NaN,
            TerrainSamples = terrain?.Count(value => value.HasValue) ?? 0,
            TerrainMisses = terrain?.Count(value => !value.HasValue) ?? 0,
            MaximumFill = 0.0,
            MaximumFillStation = double.NaN,
            MaximumCut = 0.0,
            MaximumCutStation = double.NaN,
            MaximumFillPoint = Point3d.Unset,
            MaximumCutPoint = Point3d.Unset,
            Bounds = BoundingBox.Empty,
            MaximumGradeLimit = gradeLimit,
            MinimumRadiusLimit = null
        };
    }
}
