using System;
using System.Linq;
using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The histogram the analysis card draws behind its ramp. It is a display aid, so the contract that matters
/// is that it is always safe to hand to a painter: right length, normalized, or empty.
/// </summary>
public sealed class AnalysisHistogramResampleTests
{
    private static AnalysisRange.Histogram Build(params double[] values) =>
        AnalysisRange.Histogram.Build(values, ReadOnlySpan<double>.Empty);

    [Fact]
    public void Resample_ReturnsRequestedBinCount()
    {
        double[] bars = Build(0, 1, 2, 3, 4, 5).Resample(44);

        Assert.Equal(44, bars.Length);
    }

    [Fact]
    public void Resample_ScalesTallestBarToOne()
    {
        double[] bars = Build(0, 5, 5, 5, 10).Resample(16);

        Assert.Equal(1.0, bars.Max(), 6);
        Assert.All(bars, bar => Assert.InRange(bar, 0.0, 1.0));
    }

    [Fact]
    public void Resample_NoData_ReturnsEmptySoCallersCanSkipDrawing()
    {
        Assert.Empty(new AnalysisRange.Histogram().Resample(44));
        Assert.Empty(Build().Resample(44));
    }

    [Fact]
    public void Resample_NonPositiveBinCount_ReturnsEmpty()
    {
        Assert.Empty(Build(1, 2, 3).Resample(0));
        Assert.Empty(Build(1, 2, 3).Resample(-5));
    }

    [Fact]
    public void Resample_PreservesTotalWeightAcrossBars()
    {
        // Downsampling must move weight between bars, never lose it — otherwise the drawn shape stops
        // being the distribution the range was fitted to.
        var histogram = Build(0, 1, 1, 2, 2, 2, 3);
        double[] bars = histogram.Resample(8);
        double[] finer = histogram.Resample(32);

        Assert.Equal(bars.Sum() / bars.Max(), finer.Sum() / finer.Max(), 6);
    }

    [Fact]
    public void Resample_WeightedInput_FollowsTheWeights()
    {
        // One heavily weighted value must dominate a crowd of lightly weighted ones — the same rule that
        // stops a thousand slivers out-voting the ground they sit on when auto-fit runs.
        var values = new[] { 0.0, 1.0, 1.0, 1.0 };
        var weights = new[] { 100.0, 1.0, 1.0, 1.0 };
        double[] bars = AnalysisRange.Histogram.Build(values, weights).Resample(4);

        Assert.Equal(1.0, bars[0], 6);
        Assert.True(bars[^1] < 0.1);
    }

    [Fact]
    public void Resample_IgnoresNonFiniteValues()
    {
        double[] bars = Build(0, 1, 2, double.NaN, double.PositiveInfinity).Resample(8);

        Assert.Equal(8, bars.Length);
        Assert.Equal(1.0, bars.Max(), 6);
    }
}
