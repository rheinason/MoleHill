using System.Text.Json;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class PerfComparisonTests
{
    private static PerfRunResult Run(string machine, params (string Metric, double Median)[] metrics)
    {
        var result = new PerfRunResult { Environment = new PerfEnvironment { Machine = machine } };
        foreach ((string metric, double median) in metrics)
        {
            result.Metrics[metric] = new PerfMetricStats { MedianMs = median, P95Ms = median, MinMs = median, MaxMs = median, Samples = 5 };
            string scenario = PerfComparison.ScenarioOf(metric);
            if (!result.Scenarios.Contains(scenario))
                result.Scenarios.Add(scenario);
        }

        return result;
    }

    private static PerfVerdict VerdictOf(PerfComparison comparison, string metric) =>
        comparison.Metrics.Single(m => m.Metric == metric).Verdict;

    [Fact]
    public void Compare_SlowerByMarginAndFloor_Regresses()
    {
        PerfComparison comparison = PerfComparison.Compare(
            Run("M", ("s/cold/wall", 1000)), Run("M", ("s/cold/wall", 1300)), "b.json", margin: 0.2, floorMs: 25);

        Assert.Equal(PerfVerdict.Regressed, VerdictOf(comparison, "s/cold/wall"));
        Assert.False(comparison.Passed);
    }

    [Fact]
    public void Compare_RelativelySlowerButUnderFloor_IsWithin()
    {
        // 2 ms -> 10 ms is 5x slower and still noise at stage scale.
        PerfComparison comparison = PerfComparison.Compare(
            Run("M", ("s/cold/Tiny", 2)), Run("M", ("s/cold/Tiny", 10)), "b.json", 0.2, 25);

        Assert.Equal(PerfVerdict.Within, VerdictOf(comparison, "s/cold/Tiny"));
        Assert.True(comparison.Passed);
    }

    [Fact]
    public void Compare_OverFloorButInsideMargin_IsWithin()
    {
        PerfComparison comparison = PerfComparison.Compare(
            Run("M", ("s/cold/wall", 5000)), Run("M", ("s/cold/wall", 5900)), "b.json", 0.2, 25);

        Assert.Equal(PerfVerdict.Within, VerdictOf(comparison, "s/cold/wall"));
    }

    [Fact]
    public void Compare_FasterByMarginAndFloor_Improves()
    {
        PerfComparison comparison = PerfComparison.Compare(
            Run("M", ("s/cold/Analysis Ponding", 3700)), Run("M", ("s/cold/Analysis Ponding", 900)), "b.json", 0.2, 25);

        Assert.Equal(PerfVerdict.Improved, VerdictOf(comparison, "s/cold/Analysis Ponding"));
        Assert.True(comparison.Passed);
    }

    [Fact]
    public void Compare_MetricAbsentFromUnrunScenario_IsNotReportedMissing()
    {
        PerfComparison comparison = PerfComparison.Compare(
            Run("M", ("a/cold/wall", 100), ("b/cold/wall", 100)),
            Run("M", ("a/cold/wall", 100)),
            "b.json", 0.2, 25);

        Assert.DoesNotContain(comparison.Metrics, m => m.Metric == "b/cold/wall");
    }

    [Fact]
    public void Compare_MetricAbsentFromRunScenario_IsMissingNotFailed()
    {
        PerfComparison comparison = PerfComparison.Compare(
            Run("M", ("a/cold/wall", 100), ("a/cold/Renamed", 50)),
            Run("M", ("a/cold/wall", 100), ("a/cold/NewName", 50)),
            "b.json", 0.2, 25);

        Assert.Equal(PerfVerdict.Missing, VerdictOf(comparison, "a/cold/Renamed"));
        Assert.Equal(PerfVerdict.New, VerdictOf(comparison, "a/cold/NewName"));
        Assert.True(comparison.Passed);
    }

    [Fact]
    public void Compare_OtherMachine_IsNotComparableAndFails()
    {
        PerfComparison comparison = PerfComparison.Compare(
            Run("OTHER", ("s/cold/wall", 1000)), Run("M", ("s/cold/wall", 1000)), "b.json", 0.2, 25);

        Assert.NotNull(comparison.NotComparableReason);
        Assert.Empty(comparison.Metrics);
        Assert.False(comparison.Passed);
    }

    [Fact]
    public void Compare_OutputMeshHashDiffers_IsReportedButDoesNotFail()
    {
        string key = "s/cold/" + PerfSampleRecorder.OutputDetail;
        PerfRunResult then = Run("M", ("s/cold/wall", 100));
        PerfRunResult now = Run("M", ("s/cold/wall", 100));
        then.Details[key] = "10 verts, 8 faces, 00000000000000aa";
        now.Details[key] = "10 verts, 8 faces, 00000000000000bb";

        PerfComparison comparison = PerfComparison.Compare(then, now, "b.json", 0.2, 25);

        Assert.Single(comparison.OutputChanges);
        Assert.True(comparison.Passed);
    }

    [Fact]
    public void Compare_OutputMeshHashSame_ReportsNoChange()
    {
        string key = "s/cold/" + PerfSampleRecorder.OutputDetail;
        PerfRunResult then = Run("M", ("s/cold/wall", 100));
        PerfRunResult now = Run("M", ("s/cold/wall", 100));
        then.Details[key] = now.Details[key] = "10 verts, 8 faces, 00000000000000aa";

        Assert.Empty(PerfComparison.Compare(then, now, "b.json", 0.2, 25).OutputChanges);
    }

    [Fact]
    public void Stats_FiveSamples_MedianIsMiddleAndP95IsMax()
    {
        PerfMetricStats stats = PerfMetricStats.From(new double[] { 50, 10, 40, 20, 30 });

        Assert.Equal(30, stats.MedianMs);
        Assert.Equal(50, stats.P95Ms);
        Assert.Equal(10, stats.MinMs);
        Assert.Equal(5, stats.Samples);
    }

    [Fact]
    public void Stats_EvenSampleCount_MedianAveragesTheMiddlePair()
    {
        Assert.Equal(25, PerfMetricStats.From(new double[] { 10, 20, 30, 40 }).MedianMs);
    }

    [Fact]
    public void RunResult_RoundTripsThroughJson_SoAResultCanBecomeTheBaseline()
    {
        PerfRunResult original = Run("M", ("s/cold/Analysis Ponding", 3720.5));
        original.Comparison = PerfComparison.Compare(original, original, "b.json", 0.2, 25);

        string json = JsonSerializer.Serialize(original, PerfRunResult.JsonOptions);
        PerfRunResult read = JsonSerializer.Deserialize<PerfRunResult>(json, PerfRunResult.JsonOptions)!;

        Assert.Equal(3720.5, read.Metrics["s/cold/Analysis Ponding"].MedianMs);
        Assert.Equal(PerfVerdict.Within, read.Comparison!.Metrics.Single().Verdict);
        Assert.Contains("\"Within\"", json);
    }
}
