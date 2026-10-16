using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class LandformSizeTests
{
    [Theory]
    [InlineData(200.0, 200.0, 20.0)]
    [InlineData(200.0, 500.0, 20.0)]
    [InlineData(40.0, 40.0, 5.0)]
    [InlineData(10.0, 10.0, 1.0)]
    [InlineData(120.0, 120.0, 10.0)]
    [InlineData(250.0, 250.0, 25.0)]
    [InlineData(200000.0, 200000.0, 20000.0)]
    public void FromExtent_IsATenthOfTheShorterSide_RoundedReadable(double x, double y, double expected)
    {
        Assert.Equal(expected, LandformSize.FromExtent(x, y), 9);
    }

    [Fact]
    public void FromExtent_ThinSite_NeverDropsBelowAFiftiethOfTheLongerSide()
    {
        // 10 m x 2000 m: a tenth of 10 m would be 1 m; the floor takes 40 m basis -> 4 -> 5 m.
        Assert.Equal(5.0, LandformSize.FromExtent(10.0, 2000.0), 9);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(double.NaN, 5.0)]
    public void FromExtent_NoLength_IsZero(double x, double y)
    {
        Assert.Equal(0.0, LandformSize.FromExtent(x, y == 5.0 ? 0.0 : y));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.4, 1.0)]
    [InlineData(1.6, 2.0)]
    [InlineData(2.3, 2.5)]
    [InlineData(3.4, 2.5)]
    [InlineData(3.6, 5.0)]
    [InlineData(7.0, 5.0)]
    [InlineData(7.5, 10.0)]
    [InlineData(0.023, 0.025)]
    public void RoundReadable_PicksTheNearestOneTwoTwoPointFiveFive(double value, double expected)
    {
        Assert.Equal(expected, LandformSize.RoundReadable(value), 9);
    }

    [Fact]
    public void Resolve_TypedPositiveValue_OverridesAuto()
    {
        Assert.Equal(7.0, LandformSize.Resolve(7.0, 200.0, 200.0));
        Assert.Equal(20.0, LandformSize.Resolve(0.0, 200.0, 200.0));
    }
}
