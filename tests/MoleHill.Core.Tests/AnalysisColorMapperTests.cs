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
}
