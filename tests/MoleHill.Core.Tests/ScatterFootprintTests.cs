using MoleHill.Core.Scattering;
using Xunit;

namespace MoleHill.Core.Tests;

public class ScatterFootprintTests
{
    [Fact]
    public void IsInside_OverhangingBounds_RejectsDespiteInteriorInsertionPoint()
    {
        double[] boundary = { 0, 0, 10, 0, 10, 10, 0, 10 };
        Assert.False(ScatterFootprint.IsInside(new[] { boundary }, new double[] { 8, 4, 12, 4, 12, 6, 8, 6 }));
        Assert.True(ScatterFootprint.IsInside(new[] { boundary }, new double[] { 0, 0, 10, 0, 10, 10, 0, 10 }));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void IsInside_CornersInsideButEdgeSpansConcaveNotch_Rejects(double bottom)
    {
        double[] boundary = { 0, 0, 10, 0, 10, 10, 6, 10, 6, 5, 4, 5, 4, 10, 0, 10 };
        Assert.False(ScatterFootprint.IsInside(new[] { boundary }, new double[] { 2, bottom, 8, bottom, 8, 8, 2, 8 }));
    }

    [Fact]
    public void IsInside_DisjointRegions_AcceptsWithinEitherAndRejectsSpanningGap()
    {
        double[][] boundaries = { new double[] { 0, 0, 4, 0, 4, 4, 0, 4 }, new double[] { 6, 0, 10, 0, 10, 4, 6, 4 } };
        Assert.True(ScatterFootprint.IsInside(boundaries, new double[] { 7, 1, 9, 1, 9, 3, 7, 3 }));
        Assert.False(ScatterFootprint.IsInside(boundaries, new double[] { 2, 1, 8, 1, 8, 3, 2, 3 }));
    }
}
