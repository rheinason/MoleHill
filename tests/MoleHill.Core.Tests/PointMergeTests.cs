using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class PointMergeTests
{
    [Theory]
    [InlineData(PointMergeMode.AtCenter, 4, 6, 8)]
    [InlineData(PointMergeMode.First, 1, 3, 5)]
    [InlineData(PointMergeMode.Last, 7, 9, 11)]
    public void CreateTargets_Mode_MovesEveryPointToExpectedLocation(PointMergeMode mode, double x, double y, double z)
    {
        double[] source = { 1, 3, 5, 7, 9, 11 };
        double[] result = PointMerge.CreateTargets(source, mode);
        Assert.Equal(new[] { x, y, z, x, y, z }, result);
        Assert.Equal(new double[] { 1, 3, 5, 7, 9, 11 }, source);
    }

    [Fact]
    public void CreateTargets_ByDistance_ClustersTransitivelyAndLeavesSingletons()
    {
        double[] result = PointMerge.CreateTargets(new double[] { 0, 0, 0, 1, 0, 0, 2, 0, 0, 8, 0, 0 },
            PointMergeMode.ByDistance, 1);
        Assert.Equal(new double[] { 1, 0, 0, 1, 0, 0, 1, 0, 0, 8, 0, 0 }, result);
    }

    [Fact]
    public void CreateTargets_ByDistance_UsesFull3DAndInclusiveThreshold()
    {
        double[] source = { 0, 0, 0, 3, 0, 4 };
        Assert.Equal(source, PointMerge.CreateTargets(source, PointMergeMode.ByDistance, 4));
        Assert.Equal(new double[] { 1.5, 0, 2, 1.5, 0, 2 }, PointMerge.CreateTargets(source, PointMergeMode.ByDistance, 5));
    }

    [Fact]
    public void CreateTargets_ZeroDistance_MergesOnlyExactDuplicates()
    {
        double[] source = { 1, 2, 3, 1, 2, 3, 1, 2, 3.00001 };
        Assert.Equal(source, PointMerge.CreateTargets(source, PointMergeMode.ByDistance, 0));
    }

    [Fact]
    public void CreateTargets_LargeCoordinates_AvoidsOverflowAndRetainsSmallMean()
    {
        double[] large = { double.MaxValue, 0, 0, double.MaxValue, 0, 0 };
        Assert.Equal(large, PointMerge.CreateTargets(large, PointMergeMode.AtCenter));
        double[] cancellation = { 1e100, 0, 0, 3, 0, 0, -1e100, 0, 0 };
        Assert.Equal(new double[] { 1, 0, 0, 1, 0, 0, 1, 0, 0 }, PointMerge.CreateTargets(cancellation, PointMergeMode.AtCenter));
        Assert.Equal(large, PointMerge.CreateTargets(large, PointMergeMode.ByDistance, double.MaxValue));
    }

    [Fact]
    public void CreateTargets_InvalidInputs_RejectsInsteadOfProducingNonfiniteTargets()
    {
        Assert.Throws<ArgumentException>(() => PointMerge.CreateTargets(new double[3], PointMergeMode.First));
        Assert.Throws<ArgumentException>(() => PointMerge.CreateTargets(new double[7], PointMergeMode.First));
        Assert.Throws<ArgumentException>(() => PointMerge.CreateTargets(new[] { 0, 0, 0, 1, 2, double.NaN }, PointMergeMode.First));
        foreach (double distance in new[] { -1, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => PointMerge.CreateTargets(new double[6], PointMergeMode.ByDistance, distance));
        Assert.Throws<ArgumentOutOfRangeException>(() => PointMerge.CreateTargets(new double[6], (PointMergeMode)99));
    }
}
