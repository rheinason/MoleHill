using MoleHill.Core.Sculpting;
using Xunit;

namespace MoleHill.Core.Tests;

public class SculptFalloffTests
{
    [Theory]
    [InlineData(SculptFalloff.Smooth)]
    [InlineData(SculptFalloff.Linear)]
    [InlineData(SculptFalloff.Sharp)]
    [InlineData(SculptFalloff.Constant)]
    public void Evaluate_AtCenter_ReturnsOne(SculptFalloff falloff)
    {
        Assert.Equal(1.0, SculptFalloffs.Evaluate(falloff, 0.0), 9);
    }

    [Theory]
    [InlineData(SculptFalloff.Smooth)]
    [InlineData(SculptFalloff.Linear)]
    [InlineData(SculptFalloff.Sharp)]
    public void Evaluate_AtRim_ReturnsZero(SculptFalloff falloff)
    {
        Assert.Equal(0.0, SculptFalloffs.Evaluate(falloff, 1.0), 9);
    }

    [Fact]
    public void Evaluate_Constant_IsOneInsideRimAndZeroBeyond()
    {
        Assert.Equal(1.0, SculptFalloffs.Evaluate(SculptFalloff.Constant, 0.999), 9);
        Assert.Equal(1.0, SculptFalloffs.Evaluate(SculptFalloff.Constant, 1.0), 9);
        Assert.Equal(0.0, SculptFalloffs.Evaluate(SculptFalloff.Constant, 1.001), 9);
    }

    [Theory]
    [InlineData(SculptFalloff.Smooth)]
    [InlineData(SculptFalloff.Linear)]
    [InlineData(SculptFalloff.Sharp)]
    public void Evaluate_MonotonicallyDecreases(SculptFalloff falloff)
    {
        double previous = double.MaxValue;
        for (double d = 0.0; d <= 1.0; d += 0.05)
        {
            double w = SculptFalloffs.Evaluate(falloff, d);
            Assert.True(w <= previous + 1e-12, $"{falloff} increased at d={d}");
            Assert.InRange(w, 0.0, 1.0);
            previous = w;
        }
    }

    [Theory]
    [InlineData(SculptFalloff.Smooth)]
    [InlineData(SculptFalloff.Linear)]
    [InlineData(SculptFalloff.Sharp)]
    [InlineData(SculptFalloff.Constant)]
    public void Evaluate_BeyondRim_ReturnsZero(SculptFalloff falloff)
    {
        Assert.Equal(0.0, SculptFalloffs.Evaluate(falloff, 1.5), 9);
    }
}
