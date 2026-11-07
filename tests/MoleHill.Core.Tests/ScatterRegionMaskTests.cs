using MoleHill.Core.Scattering;
using Xunit;

namespace MoleHill.Core.Tests;

public class ScatterRegionMaskTests
{
    private static double[] Box(double min, double max) => new[] { min, min, max, min, max, max, min, max };

    [Fact]
    public void Contains_ResolvedZoneHasHole_RejectsHoleAndAcceptsZone()
    {
        var mask = new ScatterRegionMask(new IReadOnlyList<double[]>[] { new[] { Box(-10, 10), Box(-2, 2) } }, Array.Empty<double[]>());
        Assert.True(mask.Contains(5, 0));
        Assert.False(mask.Contains(0, 0));
        Assert.False(mask.Contains(12, 0));
        Assert.Equal(3, mask.BoundaryDistance(5, 0));
        Assert.Equal(384, mask.PlanArea);
    }

    [Fact]
    public void Sample_ResolvedDomainWithHole_UsesNetAreaAndNeverSamplesHole()
    {
        var mask = new ScatterRegionMask(new IReadOnlyList<double[]>[] { new[] { Box(-10, 10), Box(-2, 2) } }, Array.Empty<double[]>());
        var points = ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = new[] { Box(-10, 10), Box(-2, 2) }, DomainContains = mask.Contains,
            DomainArea = mask.PlanArea, DensityMode = ScatterDensityMode.PerArea, PerAreaDensity = 0.25, Seed = 5
        });
        Assert.Equal(96, points.Count);
        Assert.All(points, point => Assert.True(mask.Contains(point.X, point.Y)));
    }

    [Fact]
    public void IsExcluded_Clearance_IsMeasuredFromCurveSegments()
    {
        var mask = new ScatterRegionMask(new IReadOnlyList<double[]>[] { new[] { Box(-10, 10) } }, new[] { Box(-2, 2) });
        Assert.True(mask.IsExcluded(0, 0, 0));
        Assert.True(mask.IsExcluded(2.5, 0, 1));
        Assert.False(mask.IsExcluded(3.5, 0, 1));
    }

    [Theory]
    [InlineData(0.5, 1, 2, 0)]
    [InlineData(1, 1, 2, 0)]
    [InlineData(2, 1, 2, 0.5)]
    [InlineData(3, 1, 2, 1)]
    [InlineData(9, 1, 2, 1)]
    [InlineData(1, 1, 0, 1)]
    public void FeatherProbability_SetbackAndTransition_ReturnsExpectedProbability(double distance, double setback, double feather, double expected) =>
        Assert.Equal(expected, ScatterRegionMask.FeatherProbability(distance, setback, feather));

    [Fact]
    public void ContainsFootprint_BoxEnclosesExclusion_RejectsEvenWhenEveryCornerIsClear()
    {
        var mask = new ScatterRegionMask(new IReadOnlyList<double[]>[] { new[] { Box(-10, 10) } }, new[] { Box(-1, 1) });
        Assert.False(mask.ContainsFootprint(Box(-2, 2), 0, 0));
        Assert.True(mask.ContainsFootprint(new double[] { 3, 3, 5, 3, 5, 5, 3, 5 }, 1, 1));
        Assert.False(mask.ContainsFootprint(Box(-9.5, 9.5), 1, 0));
    }
}
