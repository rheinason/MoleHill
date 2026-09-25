using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class AnalysisColorMapperTests
{
    private static readonly SlopeAnalyzer.ColorStop[] Palette =
    {
        new(0, 0, 0, 0),
        new(1, 255, 255, 255)
    };

    [Fact]
    public void Gradient_InterpolatesWithinRange()
    {
        var color = AnalysisColorMapper.Sample(5, 0, 10, AnalysisColorMapper.Mode.Gradient, 0, Palette);

        Assert.Equal(128, color.R);
        Assert.Equal(128, color.G);
        Assert.Equal(128, color.B);
    }

    [Fact]
    public void Stepped_UsesBandMidpointColor()
    {
        var color = AnalysisColorMapper.Sample(2.1, 0, 10, AnalysisColorMapper.Mode.Stepped, 2, Palette);

        Assert.Equal(76, color.R);
        Assert.Equal(76, color.G);
        Assert.Equal(76, color.B);
    }

    [Fact]
    public void Stepped_CutFillIsSymmetricAroundZero()
    {
        var negative = AnalysisColorMapper.Sample(-0.75, -2, 2, AnalysisColorMapper.Mode.Stepped, 0.5, Palette);
        var positive = AnalysisColorMapper.Sample(0.75, -2, 2, AnalysisColorMapper.Mode.Stepped, 0.5, Palette);

        Assert.Equal(negative.R, (byte)(255 - positive.R));
    }

    [Fact]
    public void ResolveBands_TileTheRangeExactly()
    {
        var range = new AnalysisRange(0.0, 10.0, false);

        var bands = AnalysisColorMapper.ResolveBands(range, 2.0, Palette);

        Assert.Equal(5, bands.Count);
        Assert.Equal(range.Low, bands[0].Low);
        Assert.Equal(range.High, bands[^1].High);
        for (int i = 1; i < bands.Count; i++)
            Assert.Equal(bands[i - 1].High, bands[i].Low, precision: 9);
    }

    [Fact]
    public void ResolveBands_IntervalThatDoesNotDivideTheSpan_LastBandAbsorbsTheRemainder()
    {
        // The old classifier snapped to a band midpoint that could sit past the top of the range, so the
        // final partial band silently clamped instead of being drawn as the short band it is.
        var bands = AnalysisColorMapper.ResolveBands(new AnalysisRange(0.0, 10.0, false), 3.0, Palette);

        Assert.Equal(4, bands.Count);
        Assert.Equal(10.0, bands[^1].High, precision: 9);
        Assert.Equal(9.0, bands[^1].Low, precision: 9);
    }

    [Fact]
    public void ResolveBands_EachBandIsOneFlatColour()
    {
        var range = new AnalysisRange(0.0, 10.0, false);
        var bands = AnalysisColorMapper.ResolveBands(range, 2.0, Palette);

        // Every value inside a band gets that band's colour — that is what makes stepped mode legible.
        var low = AnalysisColorMapper.SampleBanded(2.1, bands);
        var high = AnalysisColorMapper.SampleBanded(3.9, bands);

        Assert.Equal(low.R, high.R);
        Assert.Equal(bands[1].Color.R, low.R);
    }

    [Fact]
    public void ResolveBands_AbsurdIntervalIsCoarsenedRatherThanExploding()
    {
        var bands = AnalysisColorMapper.ResolveBands(new AnalysisRange(0.0, 1000.0, false), 0.001, Palette);

        Assert.True(bands.Count <= AnalysisColorMapper.MaximumBands);
        Assert.Equal(1000.0, bands[^1].High, precision: 6);
    }

    [Fact]
    public void ResolveBands_TinyIntervalBeyondIntRange_IsCoarsenedToManyBands()
    {
        // span / 1e-9 is 1e10 bands — past int.MaxValue, which used to overflow the cast and leave one band.
        var bands = AnalysisColorMapper.ResolveBands(new AnalysisRange(0.0, 10.0, false), 1e-9, Palette);

        Assert.True(bands.Count > 1);
        Assert.True(bands.Count <= AnalysisColorMapper.MaximumBands);
        Assert.Equal(0.0, bands[0].Low);
        Assert.Equal(10.0, bands[^1].High, precision: 9);
        Assert.NotEqual(bands[0].Color.R, bands[^1].Color.R);
    }

    [Fact]
    public void SampleBanded_ValuesBeyondTheRangeUseTheEndBands()
    {
        var bands = AnalysisColorMapper.ResolveBands(new AnalysisRange(0.0, 10.0, false), 2.0, Palette);

        Assert.Equal(bands[0].Color.R, AnalysisColorMapper.SampleBanded(-50.0, bands).R);
        Assert.Equal(bands[^1].Color.R, AnalysisColorMapper.SampleBanded(500.0, bands).R);
        Assert.Equal(bands[^1].Color.R, AnalysisColorMapper.SampleBanded(double.PositiveInfinity, bands).R);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.999, 0)]
    [InlineData(2.0, 1)]
    [InlineData(4.0, 2)]
    [InlineData(8.0, 4)]
    [InlineData(10.0, 4)]
    public void FindBand_ValueOnABandEdge_BelongsToTheBandAbove(double value, int expected)
    {
        // Bands are half-open [Low, High) — the first as much as the others; only the range's top edge
        // falls into the last band, because the end band absorbs everything at or beyond it.
        var bands = AnalysisColorMapper.ResolveBands(new AnalysisRange(0.0, 10.0, false), 2.0, Palette);

        Assert.Equal(expected, AnalysisColorMapper.FindBand(bands, value));
    }

    [Fact]
    public void InfiniteValues_MapToMatchingRangeEnd()
    {
        var negative = AnalysisColorMapper.Sample(double.NegativeInfinity, 0, 10, AnalysisColorMapper.Mode.Gradient, 0, Palette);
        var positive = AnalysisColorMapper.Sample(double.PositiveInfinity, 0, 10, AnalysisColorMapper.Mode.Gradient, 0, Palette);

        Assert.Equal(0, negative.R);
        Assert.Equal(255, positive.R);
    }
}
